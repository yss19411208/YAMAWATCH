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

    public bool HasConfig => EnvSettingsLoader.HasConfig(appPaths) || File.Exists(appPaths.DiscordBotConfigPath);

    public DiscordBotSettings? Load()
    {
        return Load(out _);
    }

    public DiscordBotSettings? Load(out string statusText)
    {
        EnsureSampleConfig();

        if (!HasConfig)
        {
            statusText = "Discord config missing";
            return null;
        }

        DiscordBotSettings settings = LoadJsonSettings() ?? new DiscordBotSettings();
        ApplyEnvSettings(settings);

        if (!settings.Enabled)
        {
            statusText = "Discord disabled";
            return null;
        }

        if (string.IsNullOrWhiteSpace(settings.BotToken))
        {
            statusText = "Discord token missing";
            return null;
        }

        if (settings.GuildId == 0)
        {
            statusText = "Discord guild missing";
            return null;
        }

        if (settings.VoiceChannelId == 0)
        {
            statusText = "Discord VC missing";
            return null;
        }

        statusText = "Discord config ready";
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
            TextChannelId = 0,
            ValorantOpenedMessage = "VALORANTを開きました",
            StreamMicrophoneAudio = true,
            MicrophoneDeviceName = string.Empty,
            MicrophoneVolume = 0.85F,
            MicrophoneNoiseGate = 0F,
            StreamLineAudioWhenRunning = true,
            LineAudioProcessNames = ["LINE", "Line", "line"],
            LineAudioVolume = 0.45F,
            ShareMediaFiles = true,
            ShareAudioAsMp3 = true,
            ShareVideoMp4 = true,
            MediaShareMaxBytes = 24L * 1024L * 1024L,
            MediaShareAudioBitrateKbps = 128,
            MediaShareFfmpegPath = string.Empty,
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
        DiscordBotSettings? settings = JsonSerializer.Deserialize<DiscordBotSettings>(serializedSettings, JsonOptions);
        if (settings is null)
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(serializedSettings);
        JsonElement rootElement = document.RootElement;
        if (!rootElement.TryGetProperty(nameof(DiscordBotSettings.StreamMicrophoneAudio), out _) &&
            rootElement.TryGetProperty("StreamPcAudio", out JsonElement legacyStreamPcAudioElement) &&
            legacyStreamPcAudioElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            settings.StreamMicrophoneAudio = legacyStreamPcAudioElement.GetBoolean();
        }

        return settings;
    }

    private void ApplyEnvSettings(DiscordBotSettings settings)
    {
        IReadOnlyDictionary<string, string> envValues = EnvSettingsLoader.Load(appPaths);

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

        if (TryGetUnsignedLong(envValues, out ulong textChannelId, "DISCORD_TEXT_CHANNEL_ID", "DISCORD_STATUS_CHANNEL_ID", "TEXT_CHANNEL_ID", "STATUS_CHANNEL_ID"))
        {
            settings.TextChannelId = textChannelId;
        }

        if (TryGetString(envValues, out string valorantOpenedMessage, "DISCORD_VALORANT_OPENED_MESSAGE", "VALOWATCH_VALORANT_OPENED_MESSAGE"))
        {
            settings.ValorantOpenedMessage = valorantOpenedMessage;
        }

        if (TryGetBoolean(
            envValues,
            out bool streamMicrophoneAudio,
            "DISCORD_STREAM_MIC_AUDIO",
            "DISCORD_STREAM_MICROPHONE_AUDIO",
            "STREAM_MIC_AUDIO",
            "STREAM_MICROPHONE_AUDIO",
            "DISCORD_STREAM_PC_AUDIO",
            "STREAM_PC_AUDIO"))
        {
            settings.StreamMicrophoneAudio = streamMicrophoneAudio;
        }

        if (TryGetString(envValues, out string microphoneDeviceName, "DISCORD_MIC_DEVICE_NAME", "DISCORD_MICROPHONE_DEVICE_NAME"))
        {
            settings.MicrophoneDeviceName = microphoneDeviceName;
        }

        if (TryGetSingle(envValues, out float microphoneVolume, "DISCORD_MIC_VOLUME", "DISCORD_MICROPHONE_VOLUME"))
        {
            settings.MicrophoneVolume = Math.Clamp(microphoneVolume, 0.05F, 1.0F);
        }

        if (TryGetSingle(envValues, out float microphoneNoiseGate, "DISCORD_MIC_NOISE_GATE", "DISCORD_MICROPHONE_NOISE_GATE"))
        {
            settings.MicrophoneNoiseGate = Math.Clamp(microphoneNoiseGate, 0.0F, 0.08F);
        }

        if (TryGetBoolean(envValues, out bool streamLineAudio, "DISCORD_STREAM_LINE_AUDIO", "DISCORD_STREAM_LINE_OUTPUT_AUDIO"))
        {
            settings.StreamLineAudioWhenRunning = streamLineAudio;
        }

        if (TryGetString(envValues, out string lineProcessNames, "DISCORD_LINE_PROCESS_NAMES", "LINE_PROCESS_NAMES"))
        {
            string[] parsedProcessNames = lineProcessNames
                .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(processName => !string.IsNullOrWhiteSpace(processName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (parsedProcessNames.Length > 0)
            {
                settings.LineAudioProcessNames = parsedProcessNames;
            }
        }

        if (TryGetSingle(envValues, out float lineAudioVolume, "DISCORD_LINE_AUDIO_VOLUME", "LINE_AUDIO_VOLUME"))
        {
            settings.LineAudioVolume = Math.Clamp(lineAudioVolume, 0.0F, 1.0F);
        }

        if (TryGetBoolean(envValues, out bool shareMediaFiles, "DISCORD_SHARE_MEDIA_FILES", "DISCORD_FILE_SHARE_ENABLED"))
        {
            settings.ShareMediaFiles = shareMediaFiles;
        }

        if (TryGetBoolean(envValues, out bool shareAudioAsMp3, "DISCORD_SHARE_AUDIO_MP3", "DISCORD_SHARE_MP3_AUDIO"))
        {
            settings.ShareAudioAsMp3 = shareAudioAsMp3;
        }

        if (TryGetBoolean(envValues, out bool shareVideoMp4, "DISCORD_SHARE_VIDEO_MP4", "DISCORD_SHARE_MP4_VIDEO"))
        {
            settings.ShareVideoMp4 = shareVideoMp4;
        }

        if (TryGetLong(envValues, out long mediaShareMaxBytes, "DISCORD_FILE_SHARE_MAX_BYTES", "DISCORD_MEDIA_SHARE_MAX_BYTES"))
        {
            settings.MediaShareMaxBytes = Math.Clamp(mediaShareMaxBytes, 1L * 1024L * 1024L, 24L * 1024L * 1024L);
        }

        if (TryGetInteger(envValues, out int mediaShareMaxMb, "DISCORD_FILE_SHARE_MAX_MB", "DISCORD_MEDIA_SHARE_MAX_MB"))
        {
            long maxBytesFromMegabytes = (long)Math.Clamp(mediaShareMaxMb, 1, 24) * 1024L * 1024L;
            settings.MediaShareMaxBytes = maxBytesFromMegabytes;
        }

        if (TryGetInteger(envValues, out int audioBitrateKbps, "DISCORD_SHARE_AUDIO_BITRATE_KBPS", "DISCORD_MP3_BITRATE_KBPS"))
        {
            settings.MediaShareAudioBitrateKbps = Math.Clamp(audioBitrateKbps, 64, 192);
        }

        if (TryGetString(envValues, out string mediaShareFfmpegPath, "VALOWATCH_FFMPEG_PATH", "FFMPEG_PATH"))
        {
            settings.MediaShareFfmpegPath = mediaShareFfmpegPath;
        }

        if (TryGetBoolean(envValues, out bool tryScreenShare, "DISCORD_TRY_SCREEN_SHARE", "TRY_SCREEN_SHARE"))
        {
            settings.TryScreenShare = tryScreenShare;
        }
    }

    private void EnsureEnvExample()
    {
        Directory.CreateDirectory(appPaths.ConfigDirectory);
        string[] sampleLines =
        [
            "DISCORD_BOT_ENABLED=false",
            "DISCORD_BOT_TOKEN=PASTE_BOT_TOKEN_HERE",
            "DISCORD_GUILD_ID=0",
            "DISCORD_VOICE_CHANNEL_ID=0",
            "DISCORD_TEXT_CHANNEL_ID=0",
            "DISCORD_STREAM_MIC_AUDIO=true",
            "DISCORD_MIC_DEVICE_NAME=",
            "DISCORD_MIC_VOLUME=0.85",
            "DISCORD_MIC_NOISE_GATE=0",
            "DISCORD_STREAM_LINE_AUDIO=true",
            "DISCORD_LINE_PROCESS_NAMES=LINE,Line,line",
            "DISCORD_LINE_AUDIO_VOLUME=0.45",
            "DISCORD_SHARE_MEDIA_FILES=true",
            "DISCORD_SHARE_AUDIO_MP3=true",
            "DISCORD_SHARE_VIDEO_MP4=true",
            "DISCORD_FILE_SHARE_MAX_MB=24",
            "DISCORD_SHARE_AUDIO_BITRATE_KBPS=128",
            "DISCORD_TRY_SCREEN_SHARE=false"
        ];

        if (!File.Exists(appPaths.EnvExamplePath))
        {
            File.WriteAllLines(appPaths.EnvExamplePath, sampleLines);
            return;
        }

        string envExampleText = File.ReadAllText(appPaths.EnvExamplePath);
        List<string> missingLines = [];
        foreach (string sampleLine in sampleLines)
        {
            string key = sampleLine.Split('=', 2)[0];
            if (!envExampleText.Contains($"{key}=", StringComparison.OrdinalIgnoreCase))
            {
                missingLines.Add(sampleLine);
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

    private static bool TryGetSingle(
        IReadOnlyDictionary<string, string> envValues,
        out float value,
        params string[] keys)
    {
        if (!TryGetString(envValues, out string rawValue, keys))
        {
            value = 0F;
            return false;
        }

        return float.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetInteger(
        IReadOnlyDictionary<string, string> envValues,
        out int value,
        params string[] keys)
    {
        if (!TryGetString(envValues, out string rawValue, keys))
        {
            value = 0;
            return false;
        }

        return int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetLong(
        IReadOnlyDictionary<string, string> envValues,
        out long value,
        params string[] keys)
    {
        if (!TryGetString(envValues, out string rawValue, keys))
        {
            value = 0;
            return false;
        }

        return long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
