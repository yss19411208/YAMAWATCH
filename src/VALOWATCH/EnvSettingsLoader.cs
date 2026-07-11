using System.Text;

namespace VALOWATCH;

internal static class EnvSettingsLoader
{
    private const string EmbeddedEnvResourceName = "VALOWATCH.Embedded.env";

    public static bool HasConfig(AppPaths appPaths)
    {
        return File.Exists(appPaths.EnvPath) || HasEmbeddedEnv();
    }

    public static IReadOnlyDictionary<string, string> Load(AppPaths appPaths)
    {
        Dictionary<string, string> envValues = new(StringComparer.OrdinalIgnoreCase);

        string? embeddedEnvText = ReadEmbeddedEnvText();
        if (!string.IsNullOrWhiteSpace(embeddedEnvText))
        {
            ApplyEnvText(envValues, embeddedEnvText);
        }

        if (File.Exists(appPaths.EnvPath))
        {
            string fileEnvText = File.ReadAllText(appPaths.EnvPath, Encoding.UTF8);
            ApplyEnvText(envValues, fileEnvText);
        }

        return envValues;
    }

    private static bool HasEmbeddedEnv()
    {
        return typeof(EnvSettingsLoader).Assembly.GetManifestResourceInfo(EmbeddedEnvResourceName) is not null;
    }

    private static string? ReadEmbeddedEnvText()
    {
        using Stream? resourceStream = typeof(EnvSettingsLoader).Assembly.GetManifestResourceStream(EmbeddedEnvResourceName);
        if (resourceStream is null)
        {
            return null;
        }

        using StreamReader reader = new(resourceStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static void ApplyEnvText(IDictionary<string, string> envValues, string envText)
    {
        foreach (string rawLine in envText.Split(["\r\n", "\n"], StringSplitOptions.None))
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
}
