using System.Reflection;

namespace VALOWATCH;

public sealed class GitUpdateSettingsStore
{
    private readonly AppPaths appPaths;

    public GitUpdateSettingsStore(AppPaths appPaths)
    {
        this.appPaths = appPaths;
        EnsureEnvExample();
    }

    public GitUpdateSettings Load()
    {
        IReadOnlyDictionary<string, string> envValues = File.Exists(appPaths.EnvPath)
            ? LoadEnvFile(appPaths.EnvPath)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        bool enabled = !TryGetBoolean(
            envValues,
            out bool configuredEnabled,
            "VALOWATCH_UPDATE_CHECK_ENABLED",
            "UPDATE_CHECK_ENABLED") || configuredEnabled;

        string repository = TryGetString(
            envValues,
            out string configuredRepository,
            "VALOWATCH_UPDATE_REPOSITORY",
            "GITHUB_REPOSITORY",
            "UPDATE_REPOSITORY")
            ? configuredRepository
            : string.Empty;

        string currentVersion = TryGetString(
            envValues,
            out string configuredCurrentVersion,
            "VALOWATCH_UPDATE_CURRENT_VERSION",
            "VALOWATCH_VERSION",
            "CURRENT_VERSION")
            ? configuredCurrentVersion
            : GetCurrentApplicationVersion();

        string gitHubToken = TryGetString(
            envValues,
            out string configuredGitHubToken,
            "VALOWATCH_GITHUB_TOKEN",
            "GITHUB_TOKEN")
            ? configuredGitHubToken
            : string.Empty;

        string branch = TryGetString(
            envValues,
            out string configuredBranch,
            "VALOWATCH_UPDATE_BRANCH",
            "UPDATE_BRANCH")
            ? configuredBranch
            : "main";

        string currentCommit = TryGetString(
            envValues,
            out string configuredCurrentCommit,
            "VALOWATCH_UPDATE_CURRENT_COMMIT",
            "CURRENT_COMMIT")
            ? configuredCurrentCommit
            : string.Empty;

        return new GitUpdateSettings(enabled, repository, currentVersion, gitHubToken, branch, currentCommit);
    }

    private void EnsureEnvExample()
    {
        Directory.CreateDirectory(appPaths.ConfigDirectory);
        string[] updateLines =
        [
            "VALOWATCH_UPDATE_CHECK_ENABLED=true",
            "VALOWATCH_UPDATE_REPOSITORY=yss19411208/YAMAWATCH",
            "VALOWATCH_UPDATE_CURRENT_VERSION=0.1.0",
            "VALOWATCH_UPDATE_BRANCH=main",
            "VALOWATCH_UPDATE_CURRENT_COMMIT=",
            "VALOWATCH_GITHUB_TOKEN="
        ];

        if (!File.Exists(appPaths.EnvExamplePath))
        {
            File.WriteAllLines(appPaths.EnvExamplePath, updateLines);
            return;
        }

        string envExampleText = File.ReadAllText(appPaths.EnvExamplePath);
        List<string> missingLines = [];
        foreach (string updateLine in updateLines)
        {
            string key = updateLine.Split('=', 2)[0];
            if (!envExampleText.Contains($"{key}=", StringComparison.OrdinalIgnoreCase))
            {
                missingLines.Add(updateLine);
            }
        }

        if (missingLines.Count == 0)
        {
            return;
        }

        using StreamWriter writer = File.AppendText(appPaths.EnvExamplePath);
        writer.WriteLine();
        foreach (string missingLine in missingLines)
        {
            writer.WriteLine(missingLine);
        }
    }

    private static string GetCurrentApplicationVersion()
    {
        Assembly assembly = typeof(GitUpdateSettingsStore).Assembly;
        AssemblyInformationalVersionAttribute? informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>();

        if (!string.IsNullOrWhiteSpace(informationalVersion?.InformationalVersion))
        {
            return informationalVersion.InformationalVersion;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private static IReadOnlyDictionary<string, string> LoadEnvFile(string envPath)
    {
        Dictionary<string, string> envValues = new(StringComparer.OrdinalIgnoreCase);

        foreach (string rawLine in File.ReadLines(envPath))
        {
            string trimmedLine = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.StartsWith('#'))
            {
                continue;
            }

            int separatorIndex = trimmedLine.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            string key = trimmedLine[..separatorIndex].Trim();
            string value = UnquoteValue(trimmedLine[(separatorIndex + 1)..].Trim());
            if (!string.IsNullOrWhiteSpace(key))
            {
                envValues[key] = value;
            }
        }

        return envValues;
    }

    private static string UnquoteValue(string value)
    {
        if (value.Length < 2)
        {
            return value;
        }

        char firstCharacter = value[0];
        char lastCharacter = value[^1];
        bool hasMatchingQuotes =
            firstCharacter == '"' && lastCharacter == '"' ||
            firstCharacter == '\'' && lastCharacter == '\'';

        return hasMatchingQuotes ? value[1..^1] : value;
    }

    private static bool TryGetString(
        IReadOnlyDictionary<string, string> envValues,
        out string value,
        params string[] keys)
    {
        foreach (string key in keys)
        {
            if (envValues.TryGetValue(key, out string? candidateValue) && !string.IsNullOrWhiteSpace(candidateValue))
            {
                value = candidateValue.Trim();
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetBoolean(
        IReadOnlyDictionary<string, string> envValues,
        out bool value,
        params string[] keys)
    {
        if (!TryGetString(envValues, out string rawValue, keys))
        {
            value = false;
            return false;
        }

        if (bool.TryParse(rawValue, out bool parsedBoolean))
        {
            value = parsedBoolean;
            return true;
        }

        if (string.Equals(rawValue, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rawValue, "yes", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rawValue, "on", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }

        if (string.Equals(rawValue, "0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rawValue, "no", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rawValue, "off", StringComparison.OrdinalIgnoreCase))
        {
            value = false;
            return true;
        }

        value = false;
        return false;
    }
}
