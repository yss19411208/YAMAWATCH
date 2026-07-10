namespace VALOWATCH;

public sealed class DiscordBotSettings
{
    public bool Enabled { get; set; }

    public string BotToken { get; set; } = string.Empty;

    public ulong GuildId { get; set; }

    public ulong VoiceChannelId { get; set; }

    public bool StreamPcAudio { get; set; } = true;

    public bool TryScreenShare { get; set; }
}
