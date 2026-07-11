using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VALOWATCH;

public sealed class GitUpdateChecker
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private readonly GitUpdateSettingsStore settingsStore;

    public GitUpdateChecker(GitUpdateSettingsStore settingsStore)
    {
        this.settingsStore = settingsStore;
    }

    public async Task<GitUpdateCheckResult> CheckLatestReleaseAsync(CancellationToken cancellationToken)
    {
        GitUpdateSettings settings = settingsStore.Load();
        if (!settings.Enabled)
        {
            return new GitUpdateCheckResult(
                GitUpdateCheckStatus.Disabled,
                settings.CurrentVersion,
                string.Empty,
                null,
                null,
                "Git update check is disabled.");
        }

        if (string.IsNullOrWhiteSpace(settings.Repository))
        {
            return new GitUpdateCheckResult(
                GitUpdateCheckStatus.MissingRepository,
                settings.CurrentVersion,
                string.Empty,
                null,
                null,
                "GitHub repository is not configured.");
        }

        if (!TryNormalizeRepository(settings.Repository, out string normalizedRepository))
        {
            return new GitUpdateCheckResult(
                GitUpdateCheckStatus.InvalidRepository,
                settings.CurrentVersion,
                string.Empty,
                null,
                null,
                "GitHub repository setting is invalid.");
        }

        Uri latestReleaseApiUri = new($"https://api.github.com/repos/{normalizedRepository}/releases/latest");

        try
        {
            using HttpClient httpClient = new()
            {
                Timeout = RequestTimeout
            };
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("VALOWATCH/0.1");
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            if (!string.IsNullOrWhiteSpace(settings.GitHubToken))
            {
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.GitHubToken);
            }

            GitUpdateCheckResult releaseResult = await CheckLatestReleaseAsync(
                httpClient,
                latestReleaseApiUri,
                settings,
                cancellationToken)
                .ConfigureAwait(false);

            if (releaseResult.Status != GitUpdateCheckStatus.NoRelease)
            {
                return releaseResult;
            }

            return await CheckLatestCommitAsync(
                httpClient,
                normalizedRepository,
                settings,
                cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            return new GitUpdateCheckResult(
                GitUpdateCheckStatus.NetworkFailed,
                settings.CurrentVersion,
                string.Empty,
                null,
                null,
                exception.Message);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return new GitUpdateCheckResult(
                GitUpdateCheckStatus.Failed,
                settings.CurrentVersion,
                string.Empty,
                null,
                null,
                exception.Message);
        }
    }

    private static async Task<GitUpdateCheckResult> CheckLatestReleaseAsync(
        HttpClient httpClient,
        Uri latestReleaseApiUri,
        GitUpdateSettings settings,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await httpClient
            .GetAsync(latestReleaseApiUri, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new GitUpdateCheckResult(
                GitUpdateCheckStatus.NoRelease,
                settings.CurrentVersion,
                string.Empty,
                null,
                null,
                "Latest release was not found.");
        }

        response.EnsureSuccessStatusCode();
        await using Stream responseStream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        using JsonDocument releaseJson = await JsonDocument
            .ParseAsync(responseStream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        JsonElement root = releaseJson.RootElement;
        string latestVersion = ReadJsonString(root, "tag_name");
        Uri? releaseUri = TryReadUri(root, "html_url");
        Uri? downloadUri = TryReadDownloadUri(root);
        string releaseCommit = TryReadReleaseCommit(root, latestVersion);

        if (string.IsNullOrWhiteSpace(latestVersion))
        {
            return new GitUpdateCheckResult(
                GitUpdateCheckStatus.Failed,
                settings.CurrentVersion,
                string.Empty,
                releaseUri,
                downloadUri,
                "Latest release tag was empty.");
        }

        bool hasUpdate = HasReleaseUpdate(latestVersion, releaseCommit, settings);
        return new GitUpdateCheckResult(
            hasUpdate ? GitUpdateCheckStatus.UpdateAvailable : GitUpdateCheckStatus.UpToDate,
            settings.CurrentVersion,
            latestVersion,
            releaseUri,
            downloadUri,
            hasUpdate ? "Update available." : "Already up to date.");
    }

    private static async Task<GitUpdateCheckResult> CheckLatestCommitAsync(
        HttpClient httpClient,
        string normalizedRepository,
        GitUpdateSettings settings,
        CancellationToken cancellationToken)
    {
        string branch = string.IsNullOrWhiteSpace(settings.Branch) ? "main" : settings.Branch.Trim();
        Uri latestCommitApiUri = new($"https://api.github.com/repos/{normalizedRepository}/commits/{Uri.EscapeDataString(branch)}");

        using HttpResponseMessage response = await httpClient
            .GetAsync(latestCommitApiUri, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return new GitUpdateCheckResult(
                GitUpdateCheckStatus.EmptyRepository,
                settings.CurrentVersion,
                string.Empty,
                new Uri($"https://github.com/{normalizedRepository}"),
                null,
                "Git repository is empty.");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new GitUpdateCheckResult(
                GitUpdateCheckStatus.NoRelease,
                settings.CurrentVersion,
                string.Empty,
                new Uri($"https://github.com/{normalizedRepository}"),
                null,
                "Latest commit was not found.");
        }

        response.EnsureSuccessStatusCode();
        await using Stream responseStream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        using JsonDocument commitJson = await JsonDocument
            .ParseAsync(responseStream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        JsonElement root = commitJson.RootElement;
        string latestCommit = ReadJsonString(root, "sha");
        Uri? commitUri = TryReadUri(root, "html_url");
        Uri archiveUri = new($"https://github.com/{normalizedRepository}/archive/refs/heads/{Uri.EscapeDataString(branch)}.zip");

        if (string.IsNullOrWhiteSpace(latestCommit))
        {
            return new GitUpdateCheckResult(
                GitUpdateCheckStatus.Failed,
                settings.CurrentVersion,
                string.Empty,
                commitUri,
                archiveUri,
                "Latest commit sha was empty.");
        }

        string currentCommit = settings.CurrentCommit.Trim();
        bool hasUpdate = string.IsNullOrWhiteSpace(currentCommit) ||
            !IsSameCommit(latestCommit, currentCommit);
        string currentText = string.IsNullOrWhiteSpace(currentCommit)
            ? "not set"
            : ShortenCommit(currentCommit);

        return new GitUpdateCheckResult(
            hasUpdate ? GitUpdateCheckStatus.UpdateAvailable : GitUpdateCheckStatus.UpToDate,
            currentText,
            $"commit {ShortenCommit(latestCommit)}",
            commitUri,
            archiveUri,
            hasUpdate ? "Update available." : "Already up to date.");
    }

    private static bool TryNormalizeRepository(string rawRepository, out string normalizedRepository)
    {
        string trimmedRepository = rawRepository.Trim();
        normalizedRepository = string.Empty;

        if (Uri.TryCreate(trimmedRepository, UriKind.Absolute, out Uri? repositoryUri) &&
            string.Equals(repositoryUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            string[] pathParts = repositoryUri.AbsolutePath
                .Trim('/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (pathParts.Length >= 2)
            {
                normalizedRepository = $"{pathParts[0]}/{TrimGitSuffix(pathParts[1])}";
                return true;
            }
        }

        const string sshPrefix = "git@github.com:";
        if (trimmedRepository.StartsWith(sshPrefix, StringComparison.OrdinalIgnoreCase))
        {
            trimmedRepository = trimmedRepository[sshPrefix.Length..];
        }

        string[] ownerRepoParts = trimmedRepository
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (ownerRepoParts.Length != 2)
        {
            return false;
        }

        string owner = ownerRepoParts[0].Trim();
        string repo = TrimGitSuffix(ownerRepoParts[1].Trim());
        if (string.IsNullOrWhiteSpace(owner) ||
            string.IsNullOrWhiteSpace(repo) ||
            owner.Contains(' ') ||
            repo.Contains(' '))
        {
            return false;
        }

        normalizedRepository = $"{owner}/{repo}";
        return true;
    }

    private static string TrimGitSuffix(string repositoryName)
    {
        return repositoryName.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? repositoryName[..^4]
            : repositoryName;
    }

    private static string ReadJsonString(JsonElement parentElement, string propertyName)
    {
        if (!parentElement.TryGetProperty(propertyName, out JsonElement propertyElement) ||
            propertyElement.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return propertyElement.GetString() ?? string.Empty;
    }

    private static Uri? TryReadUri(JsonElement parentElement, string propertyName)
    {
        string rawUri = ReadJsonString(parentElement, propertyName);
        return Uri.TryCreate(rawUri, UriKind.Absolute, out Uri? uri) ? uri : null;
    }

    private static Uri? TryReadDownloadUri(JsonElement releaseElement)
    {
        if (!releaseElement.TryGetProperty("assets", out JsonElement assetsElement) ||
            assetsElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        Uri? fallbackUri = null;
        foreach (JsonElement assetElement in assetsElement.EnumerateArray())
        {
            string assetName = ReadJsonString(assetElement, "name");
            Uri? browserDownloadUri = TryReadUri(assetElement, "browser_download_url");
            if (browserDownloadUri is null)
            {
                continue;
            }

            fallbackUri ??= browserDownloadUri;
            if (assetName.Contains("setup", StringComparison.OrdinalIgnoreCase) ||
                assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return browserDownloadUri;
            }
        }

        return fallbackUri;
    }

    private static string TryReadReleaseCommit(JsonElement releaseElement, string tagName)
    {
        string targetCommitish = ReadJsonString(releaseElement, "target_commitish");
        string commitFromTarget = ExtractCommitLikeText(targetCommitish);
        if (!string.IsNullOrWhiteSpace(commitFromTarget))
        {
            return commitFromTarget;
        }

        return ExtractCommitLikeText(tagName);
    }

    private static string ExtractCommitLikeText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        Match match = Regex.Match(value, @"(?i)(?<![0-9a-f])([0-9a-f]{7,40})(?![0-9a-f])");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static bool HasReleaseUpdate(string latestVersion, string releaseCommit, GitUpdateSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(releaseCommit))
        {
            string currentCommit = settings.CurrentCommit.Trim();
            return string.IsNullOrWhiteSpace(currentCommit) ||
                !IsSameCommit(releaseCommit, currentCommit);
        }

        return IsNewerVersion(latestVersion, settings.CurrentVersion);
    }

    private static bool IsNewerVersion(string latestVersion, string currentVersion)
    {
        string normalizedLatest = NormalizeVersionText(latestVersion);
        string normalizedCurrent = NormalizeVersionText(currentVersion);

        if (TryParseVersion(normalizedLatest, out Version? latest) &&
            TryParseVersion(normalizedCurrent, out Version? current) &&
            latest is not null &&
            current is not null)
        {
            return latest.CompareTo(current) > 0;
        }

        return !string.Equals(
            normalizedLatest,
            normalizedCurrent,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameCommit(string latestCommit, string currentCommit)
    {
        string normalizedLatestCommit = latestCommit.Trim();
        string normalizedCurrentCommit = currentCommit.Trim();
        return normalizedLatestCommit.StartsWith(normalizedCurrentCommit, StringComparison.OrdinalIgnoreCase) ||
            normalizedCurrentCommit.StartsWith(normalizedLatestCommit, StringComparison.OrdinalIgnoreCase);
    }

    private static string ShortenCommit(string commit)
    {
        string trimmedCommit = commit.Trim();
        return trimmedCommit.Length <= 7 ? trimmedCommit : trimmedCommit[..7];
    }

    private static string NormalizeVersionText(string versionText)
    {
        string normalized = versionText.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        int metadataIndex = normalized.IndexOfAny(['-', '+']);
        if (metadataIndex >= 0)
        {
            normalized = normalized[..metadataIndex];
        }

        return normalized.Trim();
    }

    private static bool TryParseVersion(string versionText, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(versionText))
        {
            return false;
        }

        string[] versionParts = versionText.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (versionParts.Length is < 1 or > 4)
        {
            return false;
        }

        List<string> paddedParts = [.. versionParts];
        while (paddedParts.Count < 3)
        {
            paddedParts.Add("0");
        }

        return Version.TryParse(string.Join('.', paddedParts), out version);
    }
}
