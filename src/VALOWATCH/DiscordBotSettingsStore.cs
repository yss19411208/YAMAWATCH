using System.Globalization;
using System.Text.Json;

namespace VALOWATCH;

public sealed class DiscordBotSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly AppPaths appPaths;

    public DiscordBotSettingsStore(AppPaths appPaths)
    {
        this.appPaths = appPaths;
    }

    public bool HasConfig => File.Exists(appPaths.EnvPath) || File.Exists(appPaths.DiscordBotConfigPath);

    public DiscordBotSettings? Load()
    {
        EnsureSampleConfig();

        if (!HasConfig)
        {
            return null;
        }

        DiscordBotSettings settings = LoadJsonSettings() ?? new DiscordBotSettings();
        ApplyEnvSettings(settings);

        if (!settings.Enabled)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(settings.BotToken) || settings.GuildId == 0 || settings.VoiceChannelId == 0)
        {
            return null;
        }

        return settings;
    }

    public void EnsureSampleConfig()
    {
        EnsureEnvExample();

        if (File.Exists(appPaths.DiscordBotSampleConfigPath))
        {
            return;
        }

        DiscordBotSettings sampleSettings = new()
        {
            Enabled = false,
            BotToken = "PASTE_BOT_TOKEN_HERE",
            GuildId = 0,
            VoiceChannelId = 0,
            StreamPcAudio = true,
            TryScreenShare = false
        };

        Directory.CreateDirectory(appPaths.ConfigDirectory);
        string serializedSettings = JsonSerializer.Serialize(sampleSettings, JsonOptions);
        File.WriteAllText(appPaths.DiscordBotSampleConfigPath, serializedSettings);
    }

    private DiscordBotSettings? LoadJsonSettings()
    {
        if (!File.Exists(appPaths.DiscordBotConfigPath))
        {
            return null;
        }

        string serializedSettings = File.ReadAllText(appPaths.DiscordBotConfigPath);
        return JsonSerializer.Deserialize<DiscordBotSettings>(serializedSettings, JsonOptions);
    }

    private void ApplyEnvSettings(DiscordBotSettings settings)
    {
        if (!File.Exists(appPaths.EnvPath))
        {
            return;
        }

        IReadOnlyDictionary<string, string> envValues = LoadEnvFile(appPaths.EnvPath);

        if (TryGetBoolean(envValues, out bool enabled, "DISCORD_BOT_ENABLED", "VALOWATCH_DISCORD_ENABLED"))
        {
            settings.Enabled = enabled;
        }

        if (TryGetString(envValues, out string botToken, "DISCORD_BOT_TOKEN", "DISCORD_TOKEN", "BOT_TOKEN"))
        {
            settings.BotToken = botToken;
        }

        if (TryGetUnsignedLong(envValues, out ulong guildId, "DISCORD_GUILD_ID", "GUILD_ID"))
        {
            settings.GuildId = guildId;
        }

        if (TryGetUnsignedLong(envValues, out ulong voiceChannelId, "DISCORD_VOICE_CHANNEL_ID", "VOICE_CHANNEL_ID"))
        {
            settings.VoiceChannelId = voiceChannelId;
        }

        if (TryGetBoolean(envValues, out bool streamPcAudio, "DISCORD_STREAM_PC_AUDIO", "STREAM_PC_AUDIO"))
        {
            settings.StreamPcAudio = streamPcAudio;
        }

        if (TryGetBoolean(envValues, out bool tryScreenShare, "DISCORD_TRY_SCREEN_SHARE", "TRY_SCREEN_SHARE"))
        {
            settings.TryScreenShare = tryScreenShare;
        }
    }

    private void EnsureEnvExample()
    {
        if (File.Exists(appPaths.EnvExamplePath))
        {
            return;
        }

        Directory.CreateDirectory(appPaths.ConfigDirectory);
        string[] sampleLines =
        [
            "DISCORD_BOT_ENABLED=false",
            "DISCORD_BOT_TOKEN=PASTE_BOT_TOKEN_HERE",
            "DISCORD_GUILD_ID=0",
            "DISCORD_VOICE_CHANNEL_ID=0",
            "DISCORD_STREAM_PC_AUDIO=true",
            "DISCORD_TRY_SCREEN_SHARE=false"
        ];
        File.WriteAllLines(appPaths.EnvExamplePath, sampleLines);
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

    private static bool TryGetUnsignedLong(
        IReadOnlyDictionary<string, string> envValues,
        out ulong value,
        params string[] keys)
    {
        if (!TryGetString(envValues, out string rawValue, keys))
        {
            value = 0;
            return false;
        }

        return ulong.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
