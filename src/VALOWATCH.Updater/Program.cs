using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VALOWATCH.Updater;

internal static class Program
{
    private const string Repository = "yss19411208/YAMAWATCH";
    private const string AppAssetName = "VALOWATCH_App.exe";
    private const string InstalledAppName = "VALOWATCH.exe";
    private const int MaximumAttempts = 5;
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30)
    ];

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        string installDirectory;
        try
        {
            installDirectory = ResolveInstallDirectory(args);
            ValidateInstallDirectory(installDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException or NotSupportedException)
        {
            WriteLog("Updater argument validation failed.", exception);
            return 1;
        }

        try
        {
            return await RunUpdateAsync(installDirectory).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            WriteLog("Dedicated updater failed.", exception);
            RestartInstalledAppIfPresent(installDirectory);
            return 1;
        }
    }

    private static async Task<int> RunUpdateAsync(string installDirectory)
    {
        using HttpClient httpClient = CreateHttpClient();
        ReleaseAppAsset appAsset = await ExecuteWithRetryAsync(
            "latest release lookup",
            cancellationToken => GetLatestAppAssetAsync(httpClient, cancellationToken)).ConfigureAwait(false);

        if (InstalledAppMatchesRelease(installDirectory, appAsset.ExpectedSha256, out string installedAppStatus))
        {
            WriteLog($"Dedicated update skipped because the installed app is already current. {installedAppStatus}");
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            RestartInstalledAppIfPresent(installDirectory);
            return 0;
        }

        string workspaceRoot = Directory.GetParent(installDirectory)?.FullName
            ?? throw new InvalidOperationException("VALOWATCH workspace root could not be resolved.");
        string updateDirectory = Path.Combine(workspaceRoot, "data", "updates", "dedicated");
        Directory.CreateDirectory(updateDirectory);
        string safeTag = SanitizeFileName(appAsset.TagName);
        string downloadedAppPath = Path.Combine(updateDirectory, $"VALOWATCH_App_{safeTag}.exe");

        await ExecuteWithRetryAsync(
            "app download",
            cancellationToken => DownloadAndValidateAppAsync(
                httpClient,
                appAsset,
                downloadedAppPath,
                cancellationToken)).ConfigureAwait(false);

        int updateExitCode = await LaunchAppSelfUpdateAsync(
            downloadedAppPath,
            installDirectory).ConfigureAwait(false);
        if (updateExitCode != 0)
        {
            WriteLog($"App self-update returned exit code {updateExitCode}.");
            RestartInstalledAppIfPresent(installDirectory);
            return 1;
        }

        WriteLog($"Dedicated update completed. Tag: {appAsset.TagName}. App: {downloadedAppPath}");
        return 0;
    }

    private static HttpClient CreateHttpClient()
    {
        HttpClient httpClient = new(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(30)
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("VALOWATCH-Updater/0.1.2");
        httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return httpClient;
    }

    private static async Task<ReleaseAppAsset> GetLatestAppAssetAsync(
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        Uri releaseUri = new($"https://api.github.com/repos/{Repository}/releases/latest");
        using HttpResponseMessage response = await httpClient
            .GetAsync(releaseUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using Stream responseStream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using JsonDocument document = await JsonDocument
            .ParseAsync(responseStream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        JsonElement root = document.RootElement;
        string tagName = ReadRequiredString(root, "tag_name");
        if (!root.TryGetProperty("assets", out JsonElement assetsElement) ||
            assetsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Latest release has no assets array.");
        }

        foreach (JsonElement assetElement in assetsElement.EnumerateArray())
        {
            string assetName = ReadOptionalString(assetElement, "name");
            if (!string.Equals(assetName, AppAssetName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string downloadUrl = ReadRequiredString(assetElement, "browser_download_url");
            string digest = NormalizeSha256Digest(ReadRequiredString(assetElement, "digest"));
            long expectedSize = assetElement.TryGetProperty("size", out JsonElement sizeElement) &&
                sizeElement.TryGetInt64(out long parsedSize)
                ? parsedSize
                : 0;
            if (expectedSize <= 0)
            {
                throw new InvalidOperationException("VALOWATCH_App.exe release size is missing.");
            }

            return new ReleaseAppAsset(
                tagName,
                new Uri(downloadUrl, UriKind.Absolute),
                digest,
                expectedSize);
        }

        throw new InvalidOperationException($"Latest release does not contain {AppAssetName}.");
    }

    private static async Task<bool> DownloadAndValidateAppAsync(
        HttpClient httpClient,
        ReleaseAppAsset appAsset,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        if (File.Exists(destinationPath) && ValidateDownloadedApp(destinationPath, appAsset, out string existingStatus))
        {
            WriteLog($"Reusing validated app. {existingStatus}");
            return true;
        }

        string partialPath = destinationPath + ".download";
        long existingLength = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (existingLength >= appAsset.ExpectedSize)
        {
            File.Delete(partialPath);
            existingLength = 0;
        }

        using HttpRequestMessage request = new(HttpMethod.Get, appAsset.DownloadUri);
        if (existingLength > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingLength, null);
        }

        using HttpResponseMessage response = await httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        bool appendResponse = existingLength > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        FileMode fileMode = appendResponse ? FileMode.Append : FileMode.Create;
        if (!appendResponse)
        {
            existingLength = 0;
        }

        await using (Stream sourceStream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false))
        await using (FileStream targetStream = new(
            partialPath,
            fileMode,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 131072,
            useAsync: true))
        {
            await sourceStream.CopyToAsync(targetStream, 131072, cancellationToken).ConfigureAwait(false);
            await targetStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        long downloadedLength = new FileInfo(partialPath).Length;
        WriteLog($"App download progress: {downloadedLength}/{appAsset.ExpectedSize} bytes.");
        if (downloadedLength != appAsset.ExpectedSize)
        {
            throw new IOException(
                $"App download is incomplete. Expected {appAsset.ExpectedSize}, actual {downloadedLength}.");
        }

        if (!ValidateDownloadedApp(partialPath, appAsset, out string validationStatus))
        {
            File.Delete(partialPath);
            throw new InvalidDataException(validationStatus);
        }

        File.Move(partialPath, destinationPath, overwrite: true);
        WriteLog(validationStatus);
        return true;
    }

    private static bool ValidateDownloadedApp(
        string filePath,
        ReleaseAppAsset appAsset,
        out string status)
    {
        FileInfo fileInfo = new(filePath);
        if (!fileInfo.Exists || fileInfo.Length != appAsset.ExpectedSize)
        {
            status = $"App size mismatch. Expected {appAsset.ExpectedSize}, actual {(fileInfo.Exists ? fileInfo.Length : 0)}.";
            return false;
        }

        using (FileStream peStream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (peStream.ReadByte() != 'M' || peStream.ReadByte() != 'Z')
            {
                status = "Downloaded app is not a Windows PE executable.";
                return false;
            }
        }

        string actualSha256;
        using (FileStream hashStream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            actualSha256 = Convert.ToHexString(SHA256.HashData(hashStream));
        }

        if (!string.Equals(actualSha256, appAsset.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            status = $"App SHA-256 mismatch. Expected {appAsset.ExpectedSha256}, actual {actualSha256}.";
            return false;
        }

        status = $"App PE and SHA-256 validation passed: {actualSha256}.";
        return true;
    }

    private static async Task<int> LaunchAppSelfUpdateAsync(
        string downloadedAppPath,
        string installDirectory)
    {
        ProcessStartInfo processStartInfo = new()
        {
            FileName = downloadedAppPath,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(downloadedAppPath),
            CreateNoWindow = true
        };
        processStartInfo.ArgumentList.Add("--silent");
        processStartInfo.ArgumentList.Add("--update");
        processStartInfo.ArgumentList.Add("--install-dir");
        processStartInfo.ArgumentList.Add(installDirectory);

        using Process updateProcess = Process.Start(processStartInfo)
            ?? throw new InvalidOperationException("Downloaded app self-update process could not be started.");
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2));
        try
        {
            await updateProcess.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                updateProcess.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            throw new TimeoutException("Downloaded app self-update did not finish within two minutes.");
        }

        return updateProcess.ExitCode;
    }

    private static async Task<T> ExecuteWithRetryAsync<T>(
        string operationName,
        Func<CancellationToken, Task<T>> operation)
    {
        Exception? lastException = null;
        for (int attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            using CancellationTokenSource attemptTimeout = new(TimeSpan.FromMinutes(30));
            try
            {
                return await operation(attemptTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsRetryable(exception) && attempt < MaximumAttempts)
            {
                lastException = exception;
                TimeSpan retryDelay = RetryDelays[attempt - 1];
                WriteLog(
                    $"{operationName} attempt {attempt}/{MaximumAttempts} failed. " +
                    $"Retrying in {retryDelay.TotalSeconds:0} seconds.",
                    exception);
                await Task.Delay(retryDelay).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            $"{operationName} failed after {MaximumAttempts} attempts.",
            lastException);
    }

    private static bool InstalledAppMatchesRelease(
        string installDirectory,
        string expectedSha256,
        out string status)
    {
        string installedAppPath = Path.Combine(installDirectory, InstalledAppName);
        if (!File.Exists(installedAppPath))
        {
            status = "Installed app is missing.";
            return false;
        }

        try
        {
            using FileStream appStream = new(
                installedAppPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            string installedSha256 = Convert.ToHexString(SHA256.HashData(appStream));
            bool matches = string.Equals(installedSha256, expectedSha256, StringComparison.OrdinalIgnoreCase);
            status = matches
                ? $"Installed SHA-256 matches release: {installedSha256}."
                : $"Installed SHA-256 differs from release. Installed: {installedSha256}. Expected: {expectedSha256}.";
            return matches;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            status = $"Installed app SHA-256 could not be read: {exception.Message}";
            return false;
        }
    }

    private static bool IsRetryable(Exception exception)
    {
        return exception is HttpRequestException or TaskCanceledException or IOException or JsonException;
    }

    private static string ResolveInstallDirectory(IReadOnlyList<string> args)
    {
        const string installDirectoryOption = "--install-dir";
        string optionPrefix = installDirectoryOption + "=";
        for (int argumentIndex = 0; argumentIndex < args.Count; argumentIndex++)
        {
            string argument = args[argumentIndex];
            if (argument.StartsWith(optionPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(argument[optionPrefix.Length..].Trim().Trim('"'));
            }

            if (string.Equals(argument, installDirectoryOption, StringComparison.OrdinalIgnoreCase) &&
                argumentIndex + 1 < args.Count)
            {
                return Path.GetFullPath(args[argumentIndex + 1].Trim().Trim('"'));
            }
        }

        string documentsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documentsDirectory))
        {
            throw new InvalidOperationException("Windows Documents directory could not be resolved.");
        }

        return Path.Combine(documentsDirectory, "VALOWATCH", "app");
    }

    private static void ValidateInstallDirectory(string installDirectory)
    {
        string fullPath = Path.GetFullPath(installDirectory);
        string rootPath = Path.GetPathRoot(fullPath) ?? string.Empty;
        if (string.Equals(
            fullPath.TrimEnd(Path.DirectorySeparatorChar),
            rootPath.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Updater cannot target a drive root.");
        }
    }

    private static void RestartInstalledAppIfPresent(string installDirectory)
    {
        try
        {
            string installedAppPath = Path.Combine(installDirectory, InstalledAppName);
            if (!File.Exists(installedAppPath))
            {
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = installedAppPath,
                UseShellExecute = true,
                WorkingDirectory = installDirectory,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            WriteLog("Existing VALOWATCH restarted after updater failure.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            WriteLog("Existing VALOWATCH could not be restarted after updater failure.", exception);
        }
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        string value = ReadOptionalString(element, propertyName);
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"GitHub response is missing {propertyName}.");
    }

    private static string ReadOptionalString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement propertyElement) &&
            propertyElement.ValueKind == JsonValueKind.String
            ? propertyElement.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string NormalizeSha256Digest(string digest)
    {
        const string prefix = "sha256:";
        string normalized = digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? digest[prefix.Length..]
            : digest;
        normalized = normalized.Trim();
        if (normalized.Length != 64 || !normalized.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("GitHub app asset SHA-256 digest is invalid.");
        }

        return normalized.ToUpperInvariant();
    }

    private static string SanitizeFileName(string value)
    {
        string sanitized = value.Trim();
        foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
        {
            sanitized = sanitized.Replace(invalidCharacter, '-');
        }

        return string.IsNullOrWhiteSpace(sanitized) ? "latest" : sanitized;
    }

    private static void WriteLog(string message, Exception? exception = null)
    {
        try
        {
            string logDirectory = Path.Combine(Path.GetTempPath(), "VALOWATCH");
            Directory.CreateDirectory(logDirectory);
            string exceptionText = exception is null ? string.Empty : $" Exception: {exception}";
            File.AppendAllText(
                Path.Combine(logDirectory, "dedicated-updater.log"),
                $"{DateTimeOffset.Now:O} {message}{exceptionText}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch (Exception logException) when (logException is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record ReleaseAppAsset(
        string TagName,
        Uri DownloadUri,
        string ExpectedSha256,
        long ExpectedSize);
}
