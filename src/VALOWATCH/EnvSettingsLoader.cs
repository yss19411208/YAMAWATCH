using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VALOWATCH;

internal static class EnvSettingsLoader
{
    private const string EmbeddedEnvResourceName = "VALOWATCH.Embedded.env";
    private static readonly byte[] DurableEnvEntropy = Encoding.UTF8.GetBytes("VALOWATCH.EnvSettings.v1");

    public static bool HasConfig(AppPaths appPaths)
    {
        return File.Exists(appPaths.DurableEnvPath) || File.Exists(appPaths.EnvPath) || HasEmbeddedEnv();
    }

    public static IReadOnlyDictionary<string, string> Load(AppPaths appPaths)
    {
        Dictionary<string, string> envValues = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> freshEnvValues = new(StringComparer.OrdinalIgnoreCase);

        IReadOnlyDictionary<string, string>? durableEnvValues = ReadDurableEnvValues(appPaths);
        if (durableEnvValues is not null)
        {
            ApplyEnvValues(envValues, durableEnvValues);
        }

        string? embeddedEnvText = ReadEmbeddedEnvText();
        if (!string.IsNullOrWhiteSpace(embeddedEnvText))
        {
            ApplyEnvText(envValues, embeddedEnvText);
            ApplyEnvText(freshEnvValues, embeddedEnvText);
        }

        if (File.Exists(appPaths.EnvPath))
        {
            string fileEnvText = File.ReadAllText(appPaths.EnvPath, Encoding.UTF8);
            ApplyEnvText(envValues, fileEnvText);
            ApplyEnvText(freshEnvValues, fileEnvText);
        }

        if (HasUsableDiscordConfig(freshEnvValues) ||
            HasUsableOpenAiConfig(freshEnvValues) && HasUsableDiscordConfig(envValues) ||
            !File.Exists(appPaths.DurableEnvPath) && HasUsableDiscordConfig(envValues))
        {
            PersistDurableEnv(appPaths, envValues);
        }

        return envValues;
    }

    private static IReadOnlyDictionary<string, string>? ReadDurableEnvValues(AppPaths appPaths)
    {
        if (!File.Exists(appPaths.DurableEnvPath))
        {
            return null;
        }

        try
        {
            byte[] protectedBytes = File.ReadAllBytes(appPaths.DurableEnvPath);
            byte[] plainBytes = ProtectedData.Unprotect(
                protectedBytes,
                DurableEnvEntropy,
                DataProtectionScope.CurrentUser);
            Dictionary<string, string>? durableEnvValues = JsonSerializer.Deserialize<Dictionary<string, string>>(
                plainBytes);
            return durableEnvValues is null
                ? null
                : new Dictionary<string, string>(durableEnvValues, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            return null;
        }
    }

    private static void PersistDurableEnv(
        AppPaths appPaths,
        IReadOnlyDictionary<string, string> envValues)
    {
        string temporaryPath = appPaths.DurableEnvPath + $".{Environment.ProcessId}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(appPaths.DurableEnvPath) ?? appPaths.DataDirectory);
            byte[] plainBytes = JsonSerializer.SerializeToUtf8Bytes(envValues);
            byte[] protectedBytes = ProtectedData.Protect(
                plainBytes,
                DurableEnvEntropy,
                DataProtectionScope.CurrentUser);
            File.WriteAllBytes(temporaryPath, protectedBytes);
            File.Move(temporaryPath, appPaths.DurableEnvPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static bool HasUsableDiscordConfig(IReadOnlyDictionary<string, string> envValues)
    {
        bool hasToken = TryGetNonPlaceholderValue(
            envValues,
            out _,
            "DISCORD_BOT_TOKEN",
            "DISCORD_TOKEN",
            "BOT_TOKEN");
        bool hasGuild = TryGetPositiveUnsignedLong(envValues, "DISCORD_GUILD_ID", "GUILD_ID");
        bool hasVoiceChannel = TryGetPositiveUnsignedLong(
            envValues,
            "DISCORD_VOICE_CHANNEL_ID",
            "VOICE_CHANNEL_ID");
        return hasToken && hasGuild && hasVoiceChannel;
    }

    private static bool HasUsableOpenAiConfig(IReadOnlyDictionary<string, string> envValues)
    {
        return TryGetNonPlaceholderValue(
            envValues,
            out _,
            "OPENAI_API_KEY",
            "VALOWATCH_OPENAI_API_KEY");
    }

    private static bool TryGetNonPlaceholderValue(
        IReadOnlyDictionary<string, string> envValues,
        out string value,
        params string[] keys)
    {
        foreach (string key in keys)
        {
            if (!envValues.TryGetValue(key, out string? candidateValue))
            {
                continue;
            }

            string trimmedValue = candidateValue.Trim();
            if (!string.IsNullOrWhiteSpace(trimmedValue) &&
                !trimmedValue.Contains("PASTE_", StringComparison.OrdinalIgnoreCase) &&
                !trimmedValue.Contains("YOUR_", StringComparison.OrdinalIgnoreCase))
            {
                value = trimmedValue;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetPositiveUnsignedLong(
        IReadOnlyDictionary<string, string> envValues,
        params string[] keys)
    {
        return TryGetNonPlaceholderValue(envValues, out string rawValue, keys) &&
            ulong.TryParse(rawValue, out ulong parsedValue) &&
            parsedValue > 0;
    }

    private static void TryDeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool HasEmbeddedEnv()
    {
        return typeof(EnvSettingsLoader).Assembly.GetManifestResourceInfo(EmbeddedEnvResourceName) is not null;
    }

    private static void ApplyEnvValues(
        IDictionary<string, string> envValues,
        IReadOnlyDictionary<string, string> sourceValues)
    {
        foreach ((string key, string value) in sourceValues)
        {
            envValues[key] = value;
        }
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
