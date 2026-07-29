namespace VALOWATCH;

public enum DiscordVoiceJoinMode
{
    ActivityOnly,
    AlwaysWhilePcOpen
}

internal static class DiscordVoiceJoinModeNames
{
    public const string ActivityOnlyValue = "activity";
    public const string AlwaysWhilePcOpenValue = "always";

    public static DiscordVoiceJoinMode Parse(string? value, DiscordVoiceJoinMode defaultValue)
    {
        return TryParse(value, out DiscordVoiceJoinMode mode)
            ? mode
            : defaultValue;
    }

    public static bool TryParse(string? value, out DiscordVoiceJoinMode mode)
    {
        string normalizedValue = (value ?? string.Empty).Trim();
        if (normalizedValue.Equals(ActivityOnlyValue, StringComparison.OrdinalIgnoreCase) ||
            normalizedValue.Equals("valorant", StringComparison.OrdinalIgnoreCase) ||
            normalizedValue.Equals("valo", StringComparison.OrdinalIgnoreCase) ||
            normalizedValue.Equals("game", StringComparison.OrdinalIgnoreCase))
        {
            mode = DiscordVoiceJoinMode.ActivityOnly;
            return true;
        }

        if (normalizedValue.Equals(AlwaysWhilePcOpenValue, StringComparison.OrdinalIgnoreCase) ||
            normalizedValue.Equals("pc", StringComparison.OrdinalIgnoreCase) ||
            normalizedValue.Equals("startup", StringComparison.OrdinalIgnoreCase) ||
            normalizedValue.Equals("online", StringComparison.OrdinalIgnoreCase))
        {
            mode = DiscordVoiceJoinMode.AlwaysWhilePcOpen;
            return true;
        }

        mode = DiscordVoiceJoinMode.ActivityOnly;
        return false;
    }

    public static string ToValue(DiscordVoiceJoinMode mode)
    {
        return mode == DiscordVoiceJoinMode.AlwaysWhilePcOpen
            ? AlwaysWhilePcOpenValue
            : ActivityOnlyValue;
    }

    public static string ToDisplayText(DiscordVoiceJoinMode mode)
    {
        return mode == DiscordVoiceJoinMode.AlwaysWhilePcOpen
            ? "PCが開いていたらVCに入る"
            : "VALORANT/LINE中だけVCに入る";
    }
}
