using Discord;
using Discord.WebSocket;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VALOWATCH.StartAgent;

internal static class Program
{
    private const string SingleInstanceMutexName = "Local\\VALOWATCH.StartAgent";
    private const string StartCommandName = "start";
    private const string AgentFileName = "GITHUB.exe";
    private const string AgentProcessName = "GITHUB";
    private const string AppFileName = "VALOWATCH.exe";
    private const string AppProcessName = "VALOWATCH";
    private static readonly byte[] DurableEnvEntropy = Encoding.UTF8.GetBytes("VALOWATCH.EnvSettings.v1");

    private static DiscordSocketClient? discordClient;
    private static StartAgentPaths? paths;

    public static async Task<int> Main(string[] args)
    {
        paths = StartAgentPaths.Resolve(args);
        paths.EnsureDirectories();

        if (args.Any(argument => string.Equals(argument, "--check-start-agent", StringComparison.OrdinalIgnoreCase)))
        {
            return RunDiagnostic(paths);
        }

        using Mutex singleInstanceMutex = new(true, SingleInstanceMutexName, out bool ownsSingleInstance);
        if (!ownsSingleInstance)
        {
            return 0;
        }

        while (true)
        {
            try
            {
                await RunDiscordLoopAsync(paths).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Discord.Net.HttpException or HttpRequestException or TaskCanceledException)
            {
                WriteLog(paths, "VALOWATCH Start agent loop failed; retrying.", exception);
            }

            await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            GC.KeepAlive(singleInstanceMutex);
        }
    }

    private static int RunDiagnostic(StartAgentPaths resolvedPaths)
    {
        try
        {
            StartAgentSettings settings = StartAgentSettings.Load(resolvedPaths);
            bool ready = File.Exists(resolvedPaths.GitHubAgentPath) &&
                File.Exists(resolvedPaths.ValowatchAppPath) &&
                settings.IsUsable;
            WriteLog(
                resolvedPaths,
                $"Start agent diagnostic: {(ready ? "ready" : "failed")}. " +
                $"WorkspaceRoot: {resolvedPaths.WorkspaceRoot}. InstallDirectory: {resolvedPaths.InstallDirectory}. " +
                $"GITHUB: {File.Exists(resolvedPaths.GitHubAgentPath)}. App: {File.Exists(resolvedPaths.ValowatchAppPath)}. " +
                $"DiscordConfig: {settings.StatusText}.");
            return ready ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            WriteLog(resolvedPaths, "Start agent diagnostic failed.", exception);
            return 1;
        }
    }

    private static async Task RunDiscordLoopAsync(StartAgentPaths resolvedPaths)
    {
        StartAgentSettings settings = StartAgentSettings.Load(resolvedPaths);
        if (!settings.IsUsable)
        {
            WriteLog(resolvedPaths, $"Start agent Discord config is not usable: {settings.StatusText}");
            await Task.Delay(TimeSpan.FromMinutes(1)).ConfigureAwait(false);
            return;
        }

        EnsureValowatchStackRunning(resolvedPaths, "start agent startup");

        DiscordSocketClient client = new(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds,
            AlwaysDownloadUsers = false,
            LogLevel = LogSeverity.Info
        });
        discordClient = client;
        client.Log += message =>
        {
            WriteLog(
                resolvedPaths,
                message.Exception is null
                    ? $"Discord.Net {message.Severity}: {message.Source}: {message.Message}"
                    : $"Discord.Net {message.Severity}: {message.Source}: {message.Message}",
                message.Exception);
            return Task.CompletedTask;
        };
        client.Ready += () => OnReadyAsync(client, settings, resolvedPaths);
        client.SlashCommandExecuted += command => OnSlashCommandExecutedAsync(command, settings, resolvedPaths);

        await client.LoginAsync(TokenType.Bot, settings.BotToken).ConfigureAwait(false);
        await client.StartAsync().ConfigureAwait(false);
        WriteLog(resolvedPaths, "VALOWATCH Start agent connected to Discord.");

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        }
        finally
        {
            await client.StopAsync().ConfigureAwait(false);
            await client.LogoutAsync().ConfigureAwait(false);
            client.Dispose();
            discordClient = null;
        }
    }

    private static async Task OnReadyAsync(
        DiscordSocketClient client,
        StartAgentSettings settings,
        StartAgentPaths resolvedPaths)
    {
        SocketGuild? guild = client.GetGuild(settings.GuildId);
        if (guild is null)
        {
            WriteLog(resolvedPaths, $"Start agent guild was not found: {settings.GuildId}.");
            return;
        }

        IReadOnlyCollection<SocketApplicationCommand> commands = await guild
            .GetApplicationCommandsAsync()
            .ConfigureAwait(false);
        bool commandExists = commands.Any(command =>
            string.Equals(command.Name, StartCommandName, StringComparison.OrdinalIgnoreCase));
        if (!commandExists)
        {
            SlashCommandBuilder commandBuilder = new SlashCommandBuilder()
                .WithName(StartCommandName)
                .WithDescription("VALOWATCHの監視と本体を起動します");
            await guild.CreateApplicationCommandAsync(commandBuilder.Build()).ConfigureAwait(false);
            WriteLog(resolvedPaths, "Start agent slash command registered: /start.");
        }

        EnsureValowatchStackRunning(resolvedPaths, "Discord ready");
    }

    private static async Task OnSlashCommandExecutedAsync(
        SocketSlashCommand command,
        StartAgentSettings settings,
        StartAgentPaths resolvedPaths)
    {
        if (!string.Equals(command.Data.Name, StartCommandName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (command.User is not SocketGuildUser guildUser ||
            guildUser.Guild.Id != settings.GuildId)
        {
            await command.RespondAsync("このサーバーではVALOWATCHを起動できません。", ephemeral: true)
                .ConfigureAwait(false);
            return;
        }

        if (!guildUser.GuildPermissions.Administrator && !guildUser.GuildPermissions.ManageGuild)
        {
            await command.RespondAsync("VALOWATCHを起動するにはサーバー管理権限が必要です。", ephemeral: true)
                .ConfigureAwait(false);
            return;
        }

        StartStackResult result = EnsureValowatchStackRunning(resolvedPaths, $"Discord /start by {command.User.Id}");
        await command.RespondAsync(
                $"VALOWATCH 起動要求を受け付けました。\nGITHUB: {result.GitHubStatus}\nVALOWATCH: {result.AppStatus}",
                ephemeral: true)
            .ConfigureAwait(false);
    }

    private static StartStackResult EnsureValowatchStackRunning(StartAgentPaths resolvedPaths, string reason)
    {
        string gitHubStatus = EnsureProcessRunning(
            AgentProcessName,
            resolvedPaths.GitHubAgentPath,
            resolvedPaths.WorkspaceRoot,
            [
                "--watch",
                "--install-dir",
                resolvedPaths.InstallDirectory
            ]);
        string appStatus = EnsureProcessRunning(
            AppProcessName,
            resolvedPaths.ValowatchAppPath,
            resolvedPaths.InstallDirectory,
            []);
        WriteLog(resolvedPaths, $"Start request processed. Reason: {reason}. GITHUB: {gitHubStatus}. VALOWATCH: {appStatus}.");
        return new StartStackResult(gitHubStatus, appStatus);
    }

    private static string EnsureProcessRunning(
        string processName,
        string executablePath,
        string workingDirectory,
        IReadOnlyList<string> arguments)
    {
        if (!File.Exists(executablePath))
        {
            return "missing";
        }

        if (IsProcessRunningFromPath(processName, executablePath))
        {
            return "already running";
        }

        ProcessStartInfo processStartInfo = new()
        {
            FileName = executablePath,
            UseShellExecute = true,
            WorkingDirectory = workingDirectory,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (string argument in arguments)
        {
            processStartInfo.ArgumentList.Add(argument);
        }

        Process.Start(processStartInfo);
        return "start requested";
    }

    private static bool IsProcessRunningFromPath(string processName, string expectedPath)
    {
        string normalizedExpectedPath = Path.GetFullPath(expectedPath);
        foreach (Process process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    string? processPath = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(processPath) &&
                        Path.GetFullPath(processPath).Equals(normalizedExpectedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                {
                }
            }
        }

        return false;
    }

    private static void WriteLog(StartAgentPaths resolvedPaths, string message, Exception? exception = null)
    {
        try
        {
            string exceptionText = exception is null ? string.Empty : $" Exception: {exception}";
            File.AppendAllText(
                resolvedPaths.LogPath,
                $"{DateTimeOffset.Now:O} [StartAgent] {message}{exceptionText}{Environment.NewLine}");
        }
        catch (Exception logException) when (logException is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record StartStackResult(string GitHubStatus, string AppStatus);

    private sealed class StartAgentSettings
    {
        public string BotToken { get; private init; } = string.Empty;

        public ulong GuildId { get; private init; }

        public string StatusText { get; private init; } = "missing";

        public bool IsUsable => !string.IsNullOrWhiteSpace(BotToken) && GuildId > 0;

        public static StartAgentSettings Load(StartAgentPaths resolvedPaths)
        {
            Dictionary<string, string> envValues = new(StringComparer.OrdinalIgnoreCase);
            ApplyEnvValues(envValues, ReadDurableEnvValues(resolvedPaths));
            foreach (string envPath in resolvedPaths.EnvPaths)
            {
                if (File.Exists(envPath))
                {
                    ApplyEnvText(envValues, File.ReadAllText(envPath, Encoding.UTF8));
                }
            }

            string botToken = ReadString(envValues, "DISCORD_BOT_TOKEN", "DISCORD_TOKEN", "BOT_TOKEN");
            ulong guildId = ReadUInt64(envValues, "DISCORD_GUILD_ID", "GUILD_ID");
            string statusText = !string.IsNullOrWhiteSpace(botToken) && guildId > 0
                ? "ready"
                : $"missing token:{string.IsNullOrWhiteSpace(botToken)} guild:{guildId == 0}";

            return new StartAgentSettings
            {
                BotToken = botToken,
                GuildId = guildId,
                StatusText = statusText
            };
        }

        private static IReadOnlyDictionary<string, string> ReadDurableEnvValues(StartAgentPaths resolvedPaths)
        {
            if (!File.Exists(resolvedPaths.DurableEnvPath))
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            try
            {
                byte[] protectedBytes = File.ReadAllBytes(resolvedPaths.DurableEnvPath);
                byte[] plainBytes = ProtectedData.Unprotect(
                    protectedBytes,
                    DurableEnvEntropy,
                    DataProtectionScope.CurrentUser);
                Dictionary<string, string>? values = JsonSerializer.Deserialize<Dictionary<string, string>>(plainBytes);
                return values is null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static string ReadString(IReadOnlyDictionary<string, string> envValues, params string[] keys)
        {
            foreach (string key in keys)
            {
                if (envValues.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
                {
                    string trimmedValue = value.Trim();
                    if (!trimmedValue.Contains("PASTE_", StringComparison.OrdinalIgnoreCase) &&
                        !trimmedValue.Contains("YOUR_", StringComparison.OrdinalIgnoreCase))
                    {
                        return trimmedValue;
                    }
                }
            }

            return string.Empty;
        }

        private static ulong ReadUInt64(IReadOnlyDictionary<string, string> envValues, params string[] keys)
        {
            string value = ReadString(envValues, keys);
            return ulong.TryParse(value, out ulong parsedValue) ? parsedValue : 0;
        }

        private static void ApplyEnvValues(IDictionary<string, string> targetValues, IReadOnlyDictionary<string, string> sourceValues)
        {
            foreach ((string key, string value) in sourceValues)
            {
                targetValues[key] = value;
            }
        }

        private static void ApplyEnvText(IDictionary<string, string> envValues, string envText)
        {
            foreach (string rawLine in envText.Split(["\r\n", "\n"], StringSplitOptions.None))
            {
                string trimmedLine = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.StartsWith('#'))
                {
                    continue;
                }

                int separatorIndex = trimmedLine.IndexOf('=');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                string key = trimmedLine[..separatorIndex].Trim();
                string value = UnquoteValue(trimmedLine[(separatorIndex + 1)..].Trim());
                if (!string.IsNullOrWhiteSpace(key))
                {
                    envValues[key] = value;
                }
            }
        }

        private static string UnquoteValue(string value)
        {
            if (value.Length < 2)
            {
                return value;
            }

            char firstCharacter = value[0];
            char lastCharacter = value[^1];
            bool hasMatchingQuotes =
                firstCharacter == '"' && lastCharacter == '"' ||
                firstCharacter == '\'' && lastCharacter == '\'';
            return hasMatchingQuotes ? value[1..^1] : value;
        }
    }

    private sealed class StartAgentPaths
    {
        public string WorkspaceRoot { get; private init; } = string.Empty;

        public string InstallDirectory { get; private init; } = string.Empty;

        public string GitHubAgentPath => ResolveFirstExistingPath(
            Path.Combine(WorkspaceRoot, AgentFileName),
            Path.Combine(WorkspaceRoot, "github", AgentFileName));

        public string ValowatchAppPath => Path.Combine(InstallDirectory, AppFileName);

        public string DataDirectory => Path.Combine(WorkspaceRoot, "data");

        public string DurableEnvPath => Path.Combine(DataDirectory, "config", "settings.protected");

        public string LogPath => Path.Combine(DataDirectory, "logs", "start-agent.log");

        public IReadOnlyList<string> EnvPaths =>
        [
            Path.Combine(WorkspaceRoot, "installer", ".env"),
            Path.Combine(WorkspaceRoot, "config", ".env"),
            Path.Combine(DataDirectory, "config", ".env")
        ];

        public static StartAgentPaths Resolve(IReadOnlyList<string> args)
        {
            string workspaceRoot = ReadOption(args, "--workspace-root");
            if (string.IsNullOrWhiteSpace(workspaceRoot))
            {
                workspaceRoot = ResolveWorkspaceRoot(AppContext.BaseDirectory);
            }

            workspaceRoot = NormalizeDirectory(workspaceRoot);
            string installDirectory = ReadOption(args, "--install-dir");
            if (string.IsNullOrWhiteSpace(installDirectory))
            {
                installDirectory = ResolveInstallDirectory(workspaceRoot);
            }

            return new StartAgentPaths
            {
                WorkspaceRoot = workspaceRoot,
                InstallDirectory = NormalizeDirectory(installDirectory)
            };
        }

        public void EnsureDirectories()
        {
            Directory.CreateDirectory(Path.Combine(DataDirectory, "logs"));
            Directory.CreateDirectory(Path.Combine(DataDirectory, "config"));
        }

        private static string ResolveWorkspaceRoot(string startDirectory)
        {
            DirectoryInfo? currentDirectory = new(startDirectory);
            while (currentDirectory is not null)
            {
                if (LooksLikeWorkspaceRoot(currentDirectory.FullName))
                {
                    return currentDirectory.FullName;
                }

                currentDirectory = currentDirectory.Parent;
            }

            string documentsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return string.IsNullOrWhiteSpace(documentsDirectory)
                ? Path.Combine(AppContext.BaseDirectory, "VALOWATCH")
                : Path.Combine(documentsDirectory, "VALOWATCH");
        }

        private static string ResolveInstallDirectory(string workspaceRoot)
        {
            string standardInstallDirectory = Path.Combine(workspaceRoot, "app");
            if (File.Exists(Path.Combine(standardInstallDirectory, AppFileName)))
            {
                return standardInstallDirectory;
            }

            string sourceInstallDirectory = Path.Combine(workspaceRoot, "data", "installed", "VALOWATCH", "app");
            if (File.Exists(Path.Combine(sourceInstallDirectory, AppFileName)))
            {
                return sourceInstallDirectory;
            }

            return standardInstallDirectory;
        }

        private static string ResolveFirstExistingPath(params string[] candidatePaths)
        {
            return candidatePaths.FirstOrDefault(File.Exists) ?? candidatePaths[0];
        }

        private static bool LooksLikeWorkspaceRoot(string directoryPath)
        {
            return File.Exists(Path.Combine(directoryPath, AgentFileName)) ||
                Directory.Exists(Path.Combine(directoryPath, "installer")) ||
                File.Exists(Path.Combine(directoryPath, "VALOWATCH.slnx"));
        }

        private static string ReadOption(IReadOnlyList<string> args, string optionName)
        {
            string optionPrefix = optionName + "=";
            for (int argumentIndex = 0; argumentIndex < args.Count; argumentIndex++)
            {
                string argument = args[argumentIndex];
                if (argument.StartsWith(optionPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return argument[optionPrefix.Length..].Trim().Trim('"');
                }

                if (string.Equals(argument, optionName, StringComparison.OrdinalIgnoreCase) &&
                    argumentIndex + 1 < args.Count)
                {
                    return args[argumentIndex + 1].Trim().Trim('"');
                }
            }

            return string.Empty;
        }

        private static string NormalizeDirectory(string directoryPath)
        {
            return Path.GetFullPath(directoryPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
