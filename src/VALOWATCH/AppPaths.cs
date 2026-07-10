namespace VALOWATCH;

public sealed class AppPaths
{
    private AppPaths(string dataDirectory)
    {
        DataDirectory = dataDirectory;
        RecordingsDirectory = Path.Combine(dataDirectory, "recordings");
        ConfigDirectory = Path.Combine(dataDirectory, "config");
        GoogleTokenDirectory = Path.Combine(dataDirectory, "google-token");
        HistoryPath = Path.Combine(dataDirectory, "history.json");
        GoogleClientSecretPath = Path.Combine(ConfigDirectory, "google_client_secret.json");
        DiscordBotConfigPath = Path.Combine(ConfigDirectory, "discord_bot.json");
        DiscordBotSampleConfigPath = Path.Combine(ConfigDirectory, "discord_bot.sample.json");
        EnvPath = Path.Combine(ConfigDirectory, ".env");
        EnvExamplePath = Path.Combine(ConfigDirectory, ".env.example");
    }

    public string DataDirectory { get; }

    public string RecordingsDirectory { get; }

    public string ConfigDirectory { get; }

    public string GoogleTokenDirectory { get; }

    public string HistoryPath { get; }

    public string GoogleClientSecretPath { get; }

    public string DiscordBotConfigPath { get; }

    public string DiscordBotSampleConfigPath { get; }

    public string EnvPath { get; }

    public string EnvExamplePath { get; }

    public static AppPaths CreateDefault()
    {
        string localAppDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string dataDirectory = string.IsNullOrWhiteSpace(localAppDataDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "data")
            : Path.Combine(localAppDataDirectory, "VALOWATCH");

        return new AppPaths(dataDirectory);
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(RecordingsDirectory);
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(GoogleTokenDirectory);
    }
}
