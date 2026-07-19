namespace VALOWATCH;

internal sealed class ScreenshotCommandStateStore
{
    private readonly string statePath;

    public ScreenshotCommandStateStore(AppPaths appPaths)
    {
        statePath = appPaths.ScreenshotCommandStatePath;
    }

    public bool Load(bool defaultValue)
    {
        try
        {
            if (!File.Exists(statePath))
            {
                return defaultValue;
            }

            string savedState = File.ReadAllText(statePath).Trim();
            if (bool.TryParse(savedState, out bool enabled))
            {
                return enabled;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return defaultValue;
    }

    public void Save(bool enabled)
    {
        string? stateDirectory = Path.GetDirectoryName(statePath);
        if (!string.IsNullOrWhiteSpace(stateDirectory))
        {
            Directory.CreateDirectory(stateDirectory);
        }

        string temporaryPath = $"{statePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, enabled ? bool.TrueString : bool.FalseString);
        File.Move(temporaryPath, statePath, overwrite: true);
    }
}
