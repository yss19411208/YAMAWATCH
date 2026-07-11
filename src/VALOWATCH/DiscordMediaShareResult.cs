namespace VALOWATCH;

public sealed record DiscordMediaShareResult(
    bool Attempted,
    bool Sent,
    string StatusText,
    string? SharedFilePath = null);
