namespace VALOWATCH;

public sealed class AppPaths
{
    private AppPaths(
        string dataDirectory,
        string? configDirectory = null,
        string? envPath = null,
        string? envExamplePath = null)
    {
        DataDirectory = dataDirectory;
        ConfigDirectory = configDirectory ?? Path.Combine(dataDirectory, "config");
        UpdateCompletedNotificationPath = Path.Combine(dataDirectory, "update-completed.pending");
        DurableEnvPath = Path.Combine(dataDirectory, "config", "settings.protected");
        DiscordBotConfigPath = Path.Combine(ConfigDirectory, "discord_bot.json");
        DiscordBotSampleConfigPath = Path.Combine(ConfigDirectory, "discord_bot.sample.json");
        EnvPath = envPath ?? Path.Combine(ConfigDirectory, ".env");
        EnvExamplePath = envExamplePath ?? Path.Combine(ConfigDirectory, ".env.example");
    }

    public string DataDirectory { get; }

    public string ConfigDirectory { get; }

    public string UpdateCompletedNotificationPath { get; }

    public string DurableEnvPath { get; }

    public string DiscordBotConfigPath { get; }

    public string DiscordBotSampleConfigPath { get; }

    public string EnvPath { get; }

    public string EnvExamplePath { get; }

    public static AppPaths CreateDefault()
    {
        if (TryFindWorkspaceRoot(AppContext.BaseDirectory, out string workspaceRoot))
        {
            return CreateForWorkspaceRoot(workspaceRoot);
        }

        string documentsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(documentsDirectory))
        {
            string documentsWorkspaceRoot = Path.Combine(documentsDirectory, "VALOWATCH");
            if (IsWorkspaceRoot(documentsWorkspaceRoot))
            {
                return CreateForWorkspaceRoot(documentsWorkspaceRoot);
            }
        }

        string localAppDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string dataDirectory = string.IsNullOrWhiteSpace(localAppDataDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "data")
            : Path.Combine(localAppDataDirectory, "VALOWATCH");

        return new AppPaths(dataDirectory);
    }

    internal static AppPaths CreateForDataDirectory(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException("Diagnostic data directory is required.", nameof(dataDirectory));
        }

        return new AppPaths(Path.GetFullPath(dataDirectory));
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(DurableEnvPath) ?? DataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(EnvPath) ?? ConfigDirectory);
    }

    private static AppPaths CreateForWorkspaceRoot(string workspaceRoot)
    {
        string configDirectory = Path.Combine(workspaceRoot, "config");
        string installerDirectory = Path.Combine(workspaceRoot, "installer");
        return new AppPaths(
            Path.Combine(workspaceRoot, "data"),
            configDirectory,
            Path.Combine(installerDirectory, ".env"),
            Path.Combine(installerDirectory, ".env.example"));
    }

    private static bool TryFindWorkspaceRoot(string startDirectory, out string workspaceRoot)
    {
        DirectoryInfo? currentDirectory = new(startDirectory);
        while (currentDirectory is not null)
        {
            if (IsWorkspaceRoot(currentDirectory.FullName))
            {
                workspaceRoot = currentDirectory.FullName;
                return true;
            }

            currentDirectory = currentDirectory.Parent;
        }

        workspaceRoot = string.Empty;
        return false;
    }

    private static bool IsWorkspaceRoot(string directoryPath)
    {
        string solutionPath = Path.Combine(directoryPath, "VALOWATCH.slnx");
        string installerDirectory = Path.Combine(directoryPath, "installer");
        string sourceDirectory = Path.Combine(directoryPath, "src");
        string appDirectory = Path.Combine(directoryPath, "app");
        string directoryName = Path.GetFileName(directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        return File.Exists(solutionPath) ||
            (Directory.Exists(installerDirectory) && Directory.Exists(sourceDirectory)) ||
            (Directory.Exists(installerDirectory) && Directory.Exists(appDirectory)) ||
            (directoryName.Equals("VALOWATCH", StringComparison.OrdinalIgnoreCase) && Directory.Exists(installerDirectory));
    }
}
