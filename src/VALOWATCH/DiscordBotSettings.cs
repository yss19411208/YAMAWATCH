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

    public bool TryScreenShare { get; set; }
}
