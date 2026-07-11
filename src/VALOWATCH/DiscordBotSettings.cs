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

    public bool ShareMediaFiles { get; set; } = true;

    public bool ShareAudioAsMp3 { get; set; } = true;

    public bool ShareVideoMp4 { get; set; } = true;

    public long MediaShareMaxBytes { get; set; } = 24L * 1024L * 1024L;

    public int MediaShareAudioBitrateKbps { get; set; } = 128;

    public string MediaShareFfmpegPath { get; set; } = string.Empty;

    public bool TryScreenShare { get; set; }
}
