namespace VALOWATCH;

internal sealed class DiscordVoiceJoinModeStore
{
    private readonly string statePath;

    public DiscordVoiceJoinModeStore(AppPaths appPaths)
    {
        statePath = appPaths.DiscordVoiceJoinModeStatePath;
    }

    public DiscordVoiceJoinMode Load(DiscordVoiceJoinMode defaultValue)
    {
        try
        {
            if (!File.Exists(statePath))
            {
                return defaultValue;
            }

            string savedState = File.ReadAllText(statePath).Trim();
            return DiscordVoiceJoinModeNames.Parse(savedState, defaultValue);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return defaultValue;
    }

    public void Save(DiscordVoiceJoinMode mode)
    {
        string? stateDirectory = Path.GetDirectoryName(statePath);
        if (!string.IsNullOrWhiteSpace(stateDirectory))
        {
            Directory.CreateDirectory(stateDirectory);
        }

        string temporaryPath = $"{statePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, DiscordVoiceJoinModeNames.ToValue(mode));
        File.Move(temporaryPath, statePath, overwrite: true);
    }
}
