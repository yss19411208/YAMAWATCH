namespace VALOWATCH;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using Mutex singleInstanceMutex = new(true, "Local\\VALOWATCH.SingleInstance", out bool ownsSingleInstance);
        if (!ownsSingleInstance)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        AppPaths appPaths = AppPaths.CreateDefault();
        appPaths.EnsureDirectories();
        bool disableDiscordAutomation = args.Any(argument =>
            string.Equals(argument, "--no-discord", StringComparison.OrdinalIgnoreCase));

        Application.Run(new MainForm(
            appPaths,
            new AppStateStore(appPaths),
            new LoopbackRecorder(),
            new GoogleDriveUploader(appPaths),
            new DiscordBotVoiceRelay(new DiscordBotSettingsStore(appPaths)),
            new GitUpdateChecker(new GitUpdateSettingsStore(appPaths)),
            new StartupService(),
            disableDiscordAutomation));

        GC.KeepAlive(singleInstanceMutex);
    }
}
