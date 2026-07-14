namespace VALOWATCH;

public sealed class DiscordBotSettings
{
    public bool Enabled { get; set; }

    public string BotToken { get; set; } = string.Empty;

    public ulong GuildId { get; set; }

    public ulong VoiceChannelId { get; set; }

    public ulong TextChannelId { get; set; }

    public string ValorantOpenedMessage { get; set; } = "VALORANTを開きました";

    public bool StreamMicrophoneAudio { get; set; } = true;

    public string MicrophoneDeviceName { get; set; } = string.Empty;

    public float MicrophoneVolume { get; set; } = 0.85F;

    public float MicrophoneNoiseGate { get; set; }

    public bool StreamLineAudioWhenRunning { get; set; } = true;

    public string[] LineAudioProcessNames { get; set; } = ["LINE", "Line", "line"];

    public float LineAudioVolume { get; set; } = 0.45F;

    public bool StreamDiscordAudioWhenRunning { get; set; }

    public string[] DiscordAudioProcessNames { get; set; } = ["Discord", "DiscordCanary", "DiscordPTB"];

    public float DiscordAudioVolume { get; set; } = 0.45F;

    public bool DiscordAudioCommandEnabled { get; set; } = true;

    public bool TranscriptionEnabled { get; set; }

    public string TranscriptionEngine { get; set; } = "vosk";

    public string TranscriptionModelPath { get; set; } = string.Empty;

    public int TranscriptionChunkSeconds { get; set; } = 12;

    public float TranscriptionMinimumPeak { get; set; } = 0.006F;

}
