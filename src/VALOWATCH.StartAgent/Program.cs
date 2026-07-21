using Discord;
using Discord.WebSocket;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VALOWATCH.StartAgent;

internal static class Program
{
    private const string SingleInstanceMutexName = "Local\\VALOWATCH.StartAgent";
    private const string StartCommandName = "start";
    private const string AppCommandName = "app";
    private const string AgentFileName = "GITHUB.exe";
    private const string AgentProcessName = "GITHUB";
    private const string AppFileName = "VALOWATCH.exe";
    private const string AppProcessName = "VALOWATCH";
    private const int EmbedDescriptionLimit = 4096;
    private const int EmbedDescriptionSafetyMargin = 250;
    private static readonly TimeSpan DiscordHealthCheckInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DiscordDisconnectedRestartDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan NetworkChangeRestartDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan NetworkChangeMinimumUptime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SleepResumeRestartThreshold = TimeSpan.FromMinutes(2);
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

        DiscordSocketClient client = new(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds,
            AlwaysDownloadUsers = false,
            LogLevel = LogSeverity.Info
        });
        using CancellationTokenSource restartDiscordLoop = new();
        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
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
        client.Connected += () =>
        {
            WriteLog(resolvedPaths, "Start agent Discord gateway connected.");
            return Task.CompletedTask;
        };
        client.Disconnected += exception =>
        {
            WriteLog(resolvedPaths, "Start agent Discord gateway disconnected; health monitor will restart it if it does not recover.", exception);
            return Task.CompletedTask;
        };
        client.Ready += () => OnReadyAsync(client, settings, resolvedPaths);
        client.SlashCommandExecuted += command => OnSlashCommandExecutedAsync(command, settings, resolvedPaths);

        NetworkAvailabilityChangedEventHandler networkAvailabilityChangedHandler = (_, eventArgs) =>
        {
            WriteLog(resolvedPaths, $"Start agent network availability changed. IsAvailable: {eventArgs.IsAvailable}.");
            if (eventArgs.IsAvailable && DateTimeOffset.UtcNow - startedAtUtc >= NetworkChangeMinimumUptime)
            {
                RequestDiscordLoopRestartAfter(
                    resolvedPaths,
                    restartDiscordLoop,
                    NetworkChangeRestartDelay,
                    "network became available");
            }
        };
        NetworkAddressChangedEventHandler networkAddressChangedHandler = (_, _) =>
        {
            WriteLog(resolvedPaths, "Start agent network address changed.");
            if (DateTimeOffset.UtcNow - startedAtUtc >= NetworkChangeMinimumUptime)
            {
                RequestDiscordLoopRestartAfter(
                    resolvedPaths,
                    restartDiscordLoop,
                    NetworkChangeRestartDelay,
                    "network address changed");
            }
        };
        NetworkChange.NetworkAvailabilityChanged += networkAvailabilityChangedHandler;
        NetworkChange.NetworkAddressChanged += networkAddressChangedHandler;

        await client.LoginAsync(TokenType.Bot, settings.BotToken).ConfigureAwait(false);
        await client.StartAsync().ConfigureAwait(false);
        WriteLog(resolvedPaths, "VALOWATCH Start agent connected to Discord and is waiting for /start.");
        Task healthMonitorTask = MonitorDiscordHealthAsync(client, resolvedPaths, restartDiscordLoop);

        try
        {
            Task waitForRestartTask = Task.Delay(Timeout.InfiniteTimeSpan, restartDiscordLoop.Token);
            Task completedTask = await Task.WhenAny(waitForRestartTask, healthMonitorTask).ConfigureAwait(false);
            if (completedTask == healthMonitorTask)
            {
                await healthMonitorTask.ConfigureAwait(false);
            }
            else
            {
                await waitForRestartTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (restartDiscordLoop.IsCancellationRequested)
        {
            WriteLog(resolvedPaths, "Start agent Discord loop restart requested.");
        }
        finally
        {
            NetworkChange.NetworkAvailabilityChanged -= networkAvailabilityChangedHandler;
            NetworkChange.NetworkAddressChanged -= networkAddressChangedHandler;
            await StopDiscordClientQuietlyAsync(client, resolvedPaths).ConfigureAwait(false);
            client.Dispose();
            discordClient = null;
        }
    }

    private static async Task MonitorDiscordHealthAsync(
        DiscordSocketClient client,
        StartAgentPaths resolvedPaths,
        CancellationTokenSource restartDiscordLoop)
    {
        DateTimeOffset lastCheckAtUtc = DateTimeOffset.UtcNow;
        DateTimeOffset lastHealthyAtUtc = DateTimeOffset.UtcNow;
        ConnectionState? lastLoggedConnectionState = null;

        while (!restartDiscordLoop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(DiscordHealthCheckInterval, restartDiscordLoop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (restartDiscordLoop.IsCancellationRequested)
            {
                return;
            }

            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            TimeSpan checkGap = nowUtc - lastCheckAtUtc;
            lastCheckAtUtc = nowUtc;
            if (checkGap > SleepResumeRestartThreshold)
            {
                WriteLog(
                    resolvedPaths,
                    $"Start agent detected sleep/resume or timer stall; restarting Discord connection. GapSeconds: {checkGap.TotalSeconds:0}.");
                restartDiscordLoop.Cancel();
                return;
            }

            ConnectionState connectionState = client.ConnectionState;
            if (lastLoggedConnectionState != connectionState)
            {
                WriteLog(resolvedPaths, $"Start agent Discord connection state: {connectionState}.");
                lastLoggedConnectionState = connectionState;
            }

            if (connectionState == ConnectionState.Connected && client.CurrentUser is not null)
            {
                lastHealthyAtUtc = nowUtc;
                continue;
            }

            TimeSpan unhealthyDuration = nowUtc - lastHealthyAtUtc;
            if (unhealthyDuration >= DiscordDisconnectedRestartDelay)
            {
                WriteLog(
                    resolvedPaths,
                    $"Start agent Discord connection remained unhealthy; restarting loop. State: {connectionState}. UnhealthySeconds: {unhealthyDuration.TotalSeconds:0}.");
                restartDiscordLoop.Cancel();
                return;
            }
        }
    }

    private static void RequestDiscordLoopRestartAfter(
        StartAgentPaths resolvedPaths,
        CancellationTokenSource restartDiscordLoop,
        TimeSpan delay,
        string reason)
    {
        if (restartDiscordLoop.IsCancellationRequested)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay).ConfigureAwait(false);
                if (restartDiscordLoop.IsCancellationRequested)
                {
                    return;
                }

                WriteLog(resolvedPaths, $"Start agent Discord loop restart requested after delay. Reason: {reason}. DelaySeconds: {delay.TotalSeconds:0}.");
                restartDiscordLoop.Cancel();
            }
            catch (Exception exception) when (exception is TaskCanceledException or ObjectDisposedException)
            {
            }
        });
    }

    private static async Task StopDiscordClientQuietlyAsync(
        DiscordSocketClient client,
        StartAgentPaths resolvedPaths)
    {
        try
        {
            await client.StopAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or TaskCanceledException or Discord.Net.HttpException)
        {
            WriteLog(resolvedPaths, "Start agent Discord client stop failed during restart cleanup.", exception);
        }

        try
        {
            await client.LogoutAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or TaskCanceledException or Discord.Net.HttpException)
        {
            WriteLog(resolvedPaths, "Start agent Discord client logout failed during restart cleanup.", exception);
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

        await EnsureSlashCommandAsync(
                guild,
                commands,
                StartCommandName,
                "VALOWATCHを起動・復旧します",
                resolvedPaths)
            .ConfigureAwait(false);
        await EnsureSlashCommandAsync(
                guild,
                commands,
                AppCommandName,
                "VALOWATCHが見える実行中プログラムを表示します",
                resolvedPaths)
            .ConfigureAwait(false);

        StartStackResult result = EnsureValowatchStackRunning(resolvedPaths, "StartAgent ready self-heal");
        WriteLog(
            resolvedPaths,
            $"Start agent Discord ready; stack self-heal completed. GITHUB: {result.GitHubStatus}. VALOWATCH: {result.AppStatus}.");
    }

    private static async Task EnsureSlashCommandAsync(
        SocketGuild guild,
        IReadOnlyCollection<SocketApplicationCommand> commands,
        string commandName,
        string description,
        StartAgentPaths resolvedPaths)
    {
        SocketApplicationCommand? existingCommand = commands.FirstOrDefault(command =>
            string.Equals(command.Name, commandName, StringComparison.OrdinalIgnoreCase));
        if (existingCommand is not null)
        {
            if (string.Equals(existingCommand.Description, description, StringComparison.Ordinal))
            {
                WriteLog(resolvedPaths, $"Start agent slash command already exists: /{commandName}.");
                return;
            }

            await existingCommand.DeleteAsync().ConfigureAwait(false);
            WriteLog(resolvedPaths, $"Start agent slash command replaced: /{commandName}.");
        }

        SlashCommandBuilder commandBuilder = new SlashCommandBuilder()
            .WithName(commandName)
            .WithDescription(description)
            .WithContextTypes(InteractionContextType.Guild);
        await guild.CreateApplicationCommandAsync(commandBuilder.Build()).ConfigureAwait(false);
        WriteLog(resolvedPaths, $"Start agent slash command registered: /{commandName}.");
    }

    private static async Task OnSlashCommandExecutedAsync(
        SocketSlashCommand command,
        StartAgentSettings settings,
        StartAgentPaths resolvedPaths)
    {
        WriteLog(
            resolvedPaths,
            $"Start agent slash command received: /{command.Data.Name}. User: {command.User.Id}.");

        if (string.Equals(command.Data.Name, AppCommandName, StringComparison.OrdinalIgnoreCase))
        {
            await HandleAppCommandAsync(command, settings, resolvedPaths).ConfigureAwait(false);
            return;
        }

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

        StartStackResult result = EnsureValowatchStackRunning(resolvedPaths, $"Discord /start by {command.User.Id}");
        await command.RespondAsync(
                $"VALOWATCH 起動要求を受け付けました。\nGITHUB: {result.GitHubStatus}\nVALOWATCH: {result.AppStatus}",
                ephemeral: true)
            .ConfigureAwait(false);
    }

    private static async Task HandleAppCommandAsync(
        SocketSlashCommand command,
        StartAgentSettings settings,
        StartAgentPaths resolvedPaths)
    {
        if (command.User is not SocketGuildUser guildUser ||
            guildUser.Guild.Id != settings.GuildId)
        {
            await command.RespondAsync("このサーバーではVALOWATCHの実行アプリを確認できません。", ephemeral: true)
                .ConfigureAwait(false);
            return;
        }

        await command.DeferAsync(ephemeral: true).ConfigureAwait(false);
        Embed embed = BuildRunningProcessEmbed(resolvedPaths);
        await command.FollowupAsync(embed: embed, ephemeral: true).ConfigureAwait(false);
        WriteLog(resolvedPaths, $"Start agent /app responded to user {command.User.Id}.");
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

        try
        {
            Process.Start(processStartInfo);
            return "start requested";
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return $"start failed: {SummarizeException(exception)}";
        }
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

    private static Embed BuildRunningProcessEmbed(StartAgentPaths resolvedPaths)
    {
        SortedDictionary<string, int> processCounts = new(StringComparer.OrdinalIgnoreCase);
        int totalProcessCount = 0;
        int privacyFilteredProcessCount = 0;
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                totalProcessCount++;
                string processName;
                try
                {
                    processName = process.ProcessName.Trim();
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    privacyFilteredProcessCount++;
                    continue;
                }

                string displayName = NormalizeProcessName(processName);
                if (!IsUsefulProcessDisplayName(displayName))
                {
                    privacyFilteredProcessCount++;
                    continue;
                }

                processCounts.TryGetValue(displayName, out int existingCount);
                processCounts[displayName] = existingCount + 1;
            }
        }

        string description = BuildProcessListDescription(processCounts, out int omittedProcessNameCount);
        EmbedBuilder embedBuilder = new()
        {
            Title = "VALOWATCH 実行中プログラム",
            Description = description,
            Color = new Discord.Color(63, 185, 80),
            Timestamp = DateTimeOffset.Now
        };
        embedBuilder.AddField("対象", "タスクバー以外も含む実行中プログラム", inline: false);
        embedBuilder.AddField("件数", $"{processCounts.Count}種類 / {totalProcessCount}プロセス", inline: true);
        embedBuilder.AddField(
            "省略",
            omittedProcessNameCount == 0 && privacyFilteredProcessCount == 0
                ? "なし"
                : $"表示上限 {omittedProcessNameCount}件 / 内部系 {privacyFilteredProcessCount}件",
            inline: true);
        embedBuilder.AddField(
            "VALOWATCH",
            $"GITHUB: {ProcessStateText(AgentProcessName, resolvedPaths.GitHubAgentPath)}\n" +
            $"VALOWATCH: {ProcessStateText(AppProcessName, resolvedPaths.ValowatchAppPath)}",
            inline: false);
        embedBuilder.WithFooter("フルパス、ウィンドウ名、起動引数、PID、ユーザー名は送信していません");
        return embedBuilder.Build();
    }

    private static string ProcessStateText(string processName, string executablePath)
    {
        if (!File.Exists(executablePath))
        {
            return "missing";
        }

        return IsProcessRunningFromPath(processName, executablePath) ? "running" : "stopped";
    }

    private static string NormalizeProcessName(string processName)
    {
        string normalizedProcessName = NormalizeDisplayName(processName)
            .Replace("_", " ", StringComparison.Ordinal);
        const string shippingSuffix = "-Win64-Shipping";
        return normalizedProcessName.EndsWith(shippingSuffix, StringComparison.OrdinalIgnoreCase)
            ? normalizedProcessName[..^shippingSuffix.Length]
            : normalizedProcessName;
    }

    private static string NormalizeDisplayName(string displayName)
    {
        return string.Join(
            " ",
            displayName
                .Replace('\u00A0', ' ')
                .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool IsUsefulProcessDisplayName(string displayName)
    {
        string normalizedDisplayName = NormalizeDisplayName(displayName);
        if (normalizedDisplayName.Length == 0)
        {
            return false;
        }

        string[] internalProcessNames =
        [
            "AggregatorHost",
            "ApplicationFrameHost",
            "audiodg",
            "backgroundTaskHost",
            "conhost",
            "csrss",
            "ctfmon",
            "dllhost",
            "dwm",
            "fontdrvhost",
            "Idle",
            "LockApp",
            "lsass",
            "Memory Compression",
            "MoUsoCoreWorker",
            "Registry",
            "RuntimeBroker",
            "SearchApp",
            "SearchHost",
            "SearchIndexer",
            "Secure System",
            "SecurityHealthService",
            "services",
            "ShellExperienceHost",
            "sihost",
            "smss",
            "spoolsv",
            "StartMenuExperienceHost",
            "svchost",
            "System",
            "SystemSettingsBroker",
            "taskhostw",
            "TextInputHost",
            "unsecapp",
            "UserOOBEBroker",
            "wininit",
            "winlogon",
            "WmiPrvSE",
            "WUDFHost"
        ];
        return !internalProcessNames.Any(processName =>
            string.Equals(processName, normalizedDisplayName, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildProcessListDescription(
        IReadOnlyDictionary<string, int> processCounts,
        out int omittedProcessNameCount)
    {
        List<string> formattedProcessNames = processCounts
            .Select(pair => pair.Value <= 1 ? pair.Key : $"{pair.Key} ({pair.Value})")
            .ToList();
        omittedProcessNameCount = 0;

        while (formattedProcessNames.Count > 0)
        {
            string description = string.Join(Environment.NewLine, formattedProcessNames.Select(name => $"• {name}"));
            if (description.Length <= EmbedDescriptionLimit - EmbedDescriptionSafetyMargin)
            {
                return omittedProcessNameCount == 0
                    ? description
                    : $"{description}{Environment.NewLine}• ...ほか{omittedProcessNameCount}件";
            }

            formattedProcessNames.RemoveAt(formattedProcessNames.Count - 1);
            omittedProcessNameCount++;
        }

        return omittedProcessNameCount == 0
            ? "表示できる実行中プログラムはありません。"
            : $"表示できる実行中プログラムがありません。省略: {omittedProcessNameCount}件";
    }

    private static string SummarizeException(Exception exception)
    {
        List<string> exceptionParts = [];
        for (Exception? currentException = exception;
             currentException is not null && exceptionParts.Count < 2;
             currentException = currentException.InnerException)
        {
            string message = currentException.Message
                .Replace(Environment.NewLine, " ", StringComparison.Ordinal)
                .Trim();
            exceptionParts.Add($"{currentException.GetType().Name}: {message}");
        }

        return string.Join(" -> ", exceptionParts);
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
            Path.Combine(InstallDirectory, AgentFileName),
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
