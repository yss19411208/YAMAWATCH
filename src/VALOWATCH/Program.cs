using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VALOWATCH;

static class Program
{
    private const string SingleInstanceMutexName = "Local\\VALOWATCH.SingleInstance";

    [STAThread]
    static void Main(string[] args)
    {
        if (SelfUpdateInstaller.IsUpdateInvocation(args))
        {
            Environment.ExitCode = SelfUpdateInstaller.Run(args);
            return;
        }

        if (args.Any(argument => string.Equals(argument, "--keepalive-probe", StringComparison.OrdinalIgnoreCase)))
        {
            RunKeepAliveProbe();
            return;
        }

        if (args.Any(argument => string.Equals(argument, "--check-alt-t-input", StringComparison.OrdinalIgnoreCase)))
        {
            RunAltTHotKeyInputDiagnostic();
            return;
        }

        if (args.Any(argument => string.Equals(argument, "--check-update-schedule", StringComparison.OrdinalIgnoreCase)))
        {
            RunUpdateScheduleDiagnostic();
            return;
        }

        if (args.Any(argument => string.Equals(argument, "--check-durable-config", StringComparison.OrdinalIgnoreCase)))
        {
            RunDurableConfigDiagnostic(args);
            return;
        }

        if (args.Any(argument => string.Equals(argument, "--check-git-update", StringComparison.OrdinalIgnoreCase)))
        {
            RunGitUpdateDiagnostic(downloadInstaller: false);
            return;
        }

        if (args.Any(argument => string.Equals(argument, "--check-update-download", StringComparison.OrdinalIgnoreCase)))
        {
            RunGitUpdateDiagnostic(downloadInstaller: true);
            return;
        }

        if (args.Any(argument => string.Equals(argument, "--check-discord-voice-native", StringComparison.OrdinalIgnoreCase)))
        {
            RunDiscordVoiceNativeDiagnostic();
            return;
        }

        if (args.Any(argument => string.Equals(argument, "--check-microphone", StringComparison.OrdinalIgnoreCase)))
        {
            RunMicrophoneDiagnostic();
            return;
        }

        if (args.Any(argument => string.Equals(argument, "--check-line-loopback", StringComparison.OrdinalIgnoreCase)))
        {
            RunLineLoopbackDiagnostic();
            return;
        }

        if (args.Any(argument => string.Equals(argument, "--check-discord-audio-mix", StringComparison.OrdinalIgnoreCase)))
        {
            RunDiscordAudioMixDiagnostic();
            return;
        }

        if (args.Any(argument => string.Equals(argument, "--list-microphones", StringComparison.OrdinalIgnoreCase)))
        {
            RunMicrophoneListDiagnostic();
            return;
        }

        if (args.Any(argument =>
            string.Equals(argument, "--check-runtime-log-messages", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(argument, "--check-runtime-log-archive", StringComparison.OrdinalIgnoreCase)))
        {
            RunRuntimeLogMessageDiagnostic();
            return;
        }

        if (args.Any(argument => string.Equals(argument, "--check-update-identity", StringComparison.OrdinalIgnoreCase)))
        {
            RunUpdateIdentityDiagnostic(args);
            return;
        }

        using Mutex singleInstanceMutex = new(true, SingleInstanceMutexName, out bool ownsSingleInstance);
        if (!ownsSingleInstance)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        AppPaths appPaths = AppPaths.CreateDefault();
        appPaths.EnsureDirectories();
        DiscordBotSettingsStore discordBotSettingsStore = new(appPaths);
        bool disableDiscordAutomation = args.Any(argument =>
            string.Equals(argument, "--no-discord", StringComparison.OrdinalIgnoreCase));
        bool disableKeyStateFallback = args.Any(argument =>
            string.Equals(argument, "--disable-key-state-fallback", StringComparison.OrdinalIgnoreCase));

        Application.Run(new MainForm(
            appPaths,
            new DiscordBotVoiceRelay(discordBotSettingsStore, appPaths),
            disableDiscordAutomation,
            disableKeyStateFallback));

        GC.KeepAlive(singleInstanceMutex);
    }

    private static void RunKeepAliveProbe()
    {
        try
        {
            using Mutex existingInstanceMutex = Mutex.OpenExisting(SingleInstanceMutexName);
            return;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
        catch (UnauthorizedAccessException)
        {
            // An inaccessible mutex still proves another VALOWATCH instance owns it.
            return;
        }

        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("VALOWATCH executable path is unavailable.");
        ProcessStartInfo processStartInfo = new()
        {
            FileName = executablePath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath)
        };

        if (Process.Start(processStartInfo) is null)
        {
            throw new InvalidOperationException("VALOWATCH keepalive could not restart the application.");
        }
    }

    private static void RunRuntimeLogMessageDiagnostic()
    {
        AppPaths appPaths = AppPaths.CreateDefault();
        appPaths.EnsureDirectories();
        string logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");
        string diagnosticRoot = Path.Combine(
            Path.GetTempPath(),
            $"VALOWATCH-runtime-log-test-{Guid.NewGuid():N}");

        try
        {
            string dataLogsDirectory = Path.Combine(diagnosticRoot, "data-logs");
            string tempLogsDirectory = Path.Combine(diagnosticRoot, "temp-logs");
            string nestedDirectory = Path.Combine(tempLogsDirectory, "nested");
            Directory.CreateDirectory(dataLogsDirectory);
            Directory.CreateDirectory(nestedDirectory);

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            File.WriteAllLines(
                Path.Combine(dataLogsDirectory, "primary.log"),
                [
                    "normal runtime line",
                    "DISCORD_BOT_TOKEN=must-not-leak",
                    $"path={Path.Combine(userProfile, "Documents", "VALOWATCH")}"
                ]);
            File.WriteAllText(
                Path.Combine(nestedDirectory, "SECOND.LOG"),
                "nested log line " + new string('X', 4200));
            File.WriteAllText(Path.Combine(tempLogsDirectory, ".env"), "SECRET=must-not-be-included");

            string cursorPath = Path.Combine(diagnosticRoot, "runtime-log-cursors.json");
            IReadOnlyList<RuntimeLogFileDelta> initialDeltas = RuntimeLogMessageCollector.Collect(
                cursorPath,
                "diagnostic-version",
                (dataLogsDirectory, "data-logs"),
                (tempLogsDirectory, "temp-logs"));
            string initialText = string.Join(Environment.NewLine, initialDeltas.SelectMany(delta => delta.DiscordMessages));
            foreach (RuntimeLogFileDelta delta in initialDeltas)
            {
                RuntimeLogMessageCollector.Commit(cursorPath, delta.CursorKey, delta.CurrentLineCount);
            }

            File.AppendAllText(
                Path.Combine(dataLogsDirectory, "primary.log"),
                $"incremental runtime line{Environment.NewLine}");
            IReadOnlyList<RuntimeLogFileDelta> incrementalDeltas = RuntimeLogMessageCollector.Collect(
                cursorPath,
                "diagnostic-version",
                (dataLogsDirectory, "data-logs"),
                (tempLogsDirectory, "temp-logs"));
            string incrementalText = string.Join(
                Environment.NewLine,
                incrementalDeltas.SelectMany(delta => delta.DiscordMessages));
            string[] cursorKeys = initialDeltas.Select(delta => delta.CursorKey).ToArray();
            bool ready = cursorKeys.Contains("data-logs/primary.log", StringComparer.OrdinalIgnoreCase) &&
                cursorKeys.Contains("temp-logs/nested/SECOND.LOG", StringComparer.OrdinalIgnoreCase) &&
                !cursorKeys.Any(key => key.EndsWith(".env", StringComparison.OrdinalIgnoreCase)) &&
                initialDeltas.SelectMany(delta => delta.DiscordMessages).All(message =>
                    message.Length <= 2000 &&
                    message.StartsWith("```text", StringComparison.Ordinal) &&
                    message.EndsWith("```", StringComparison.Ordinal)) &&
                !initialText.Contains("must-not-leak", StringComparison.Ordinal) &&
                !initialText.Contains(userProfile, StringComparison.OrdinalIgnoreCase) &&
                initialText.Contains("%USERPROFILE%", StringComparison.Ordinal) &&
                initialText.Contains("nested log line", StringComparison.Ordinal) &&
                incrementalText.Contains("incremental runtime line", StringComparison.Ordinal) &&
                !incrementalText.Contains("normal runtime line", StringComparison.Ordinal);

            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath) ?? appPaths.DataDirectory);
            File.AppendAllText(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Runtime log message check: {(ready ? "ready" : "failed")}. " +
                $"Files: {string.Join(",", cursorKeys)}. InitialMessages: " +
                $"{initialDeltas.Sum(delta => delta.DiscordMessages.Count)}. " +
                $"IncrementalMessages: {incrementalDeltas.Sum(delta => delta.DiscordMessages.Count)}.{Environment.NewLine}");
            Environment.ExitCode = ready ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logFilePath) ?? appPaths.DataDirectory);
                File.AppendAllText(
                    logFilePath,
                    $"{DateTimeOffset.Now:O} [Diagnostics] Runtime log message check failed: {exception.Message}{Environment.NewLine}");
            }
            catch (Exception logException) when (logException is IOException or UnauthorizedAccessException)
            {
            }

            Environment.ExitCode = 1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(diagnosticRoot))
                {
                    Directory.Delete(diagnosticRoot, recursive: true);
                }
            }
            catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void RunUpdateIdentityDiagnostic(IReadOnlyList<string> args)
    {
        AppPaths appPaths = AppPaths.CreateDefault();
        appPaths.EnsureDirectories();
        string logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");

        try
        {
            const string expectedCommitPrefix = "--expected-current-commit=";
            string expectedCommit = args.FirstOrDefault(argument =>
                    argument.StartsWith(expectedCommitPrefix, StringComparison.OrdinalIgnoreCase))?
                [expectedCommitPrefix.Length..]
                .Trim()
                .Trim('"') ?? string.Empty;
            GitUpdateSettings settings = new GitUpdateSettingsStore(appPaths).Load();
            bool identityIsReady = !string.IsNullOrWhiteSpace(expectedCommit) &&
                settings.CurrentCommit.Equals(expectedCommit, StringComparison.OrdinalIgnoreCase) &&
                settings.CurrentVersion.Contains(expectedCommit, StringComparison.OrdinalIgnoreCase);

            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath) ?? appPaths.DataDirectory);
            File.AppendAllText(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Update identity check: " +
                $"{(identityIsReady ? "ready" : "failed")}. CurrentVersion: {settings.CurrentVersion}. " +
                $"CurrentCommit: {settings.CurrentCommit}. ExpectedCommit: {expectedCommit}.{Environment.NewLine}");
            Environment.ExitCode = identityIsReady ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            TryWriteDiagnosticFailure(logFilePath, "Update identity check", exception);
            Environment.ExitCode = 1;
        }
    }

    private static void RunAltTHotKeyInputDiagnostic()
    {
        AppPaths appPaths = AppPaths.CreateDefault();
        appPaths.EnsureDirectories();
        string logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");

        ApplicationConfiguration.Initialize();
        using Form rawInputTarget = new()
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            Size = new Size(1, 1),
            Location = new Point(-32000, -32000)
        };
        IntPtr targetHandle = rawInputTarget.Handle;
        RawInputDevice[] registerDevices =
        [
            new RawInputDevice
            {
                UsagePage = NativeMethods.HidUsagePageGeneric,
                Usage = NativeMethods.HidUsageGenericKeyboard,
                Flags = NativeMethods.RidevInputSink,
                TargetWindow = targetHandle
            }
        ];
        bool rawInputRegistered = NativeMethods.RegisterRawInputDevices(
            registerDevices,
            (uint)registerDevices.Length,
            (uint)Marshal.SizeOf<RawInputDevice>());
        int registrationError = rawInputRegistered ? 0 : Marshal.GetLastWin32Error();

        try
        {
            AltTHotKeyStateMachine stateMachine = new();
            bool tWithoutAltIgnored = !stateMachine.Process(
                NativeMethods.VirtualKeyT,
                keyDown: true,
                keyUp: false,
                altIsCurrentlyDown: false);
            stateMachine.Process(
                NativeMethods.VirtualKeyT,
                keyDown: false,
                keyUp: true,
                altIsCurrentlyDown: false);
            stateMachine.Process(
                NativeMethods.VirtualKeyMenu,
                keyDown: true,
                keyUp: false,
                altIsCurrentlyDown: true);
            bool firstChordTriggered = stateMachine.Process(
                NativeMethods.VirtualKeyT,
                keyDown: true,
                keyUp: false,
                altIsCurrentlyDown: true);
            bool repeatIgnored = !stateMachine.Process(
                NativeMethods.VirtualKeyT,
                keyDown: true,
                keyUp: false,
                altIsCurrentlyDown: true);
            stateMachine.Process(
                NativeMethods.VirtualKeyT,
                keyDown: false,
                keyUp: true,
                altIsCurrentlyDown: true);
            bool secondChordTriggered = stateMachine.Process(
                NativeMethods.VirtualKeyT,
                keyDown: true,
                keyUp: false,
                altIsCurrentlyDown: true);

            AltTHotKeyStateMachine startupStateMachine = new();
            bool alreadyHeldAltTriggered = startupStateMachine.Process(
                NativeMethods.VirtualKeyT,
                keyDown: true,
                keyUp: false,
                altIsCurrentlyDown: true);
            using AsyncKeyStateAltTHotKeyMonitor keyStateMonitor = new();
            keyStateMonitor.Start();
            Thread.Sleep(150);
            bool keyStateMonitorStarted = keyStateMonitor.IsResponsive && keyStateMonitor.HeartbeatCount > 0;
            bool diagnosticPassed = rawInputRegistered &&
                tWithoutAltIgnored &&
                firstChordTriggered &&
                repeatIgnored &&
                secondChordTriggered &&
                alreadyHeldAltTriggered &&
                keyStateMonitorStarted;

            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath) ?? appPaths.DataDirectory);
            File.AppendAllText(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Alt+T input check: {(diagnosticPassed ? "ready" : "failed")}. " +
                $"RawInputRegistered: {rawInputRegistered}. RegistrationError: {registrationError}. " +
                $"TWithoutAltIgnored: {tWithoutAltIgnored}. FirstChord: {firstChordTriggered}. " +
                $"RepeatIgnored: {repeatIgnored}. SecondChord: {secondChordTriggered}. " +
                $"AlreadyHeldAlt: {alreadyHeldAltTriggered}. KeyStateMonitorStarted: {keyStateMonitorStarted}. " +
                $"KeyStateHeartbeat: {keyStateMonitor.HeartbeatCount}.{Environment.NewLine}");
            Environment.ExitCode = diagnosticPassed ? 0 : 1;
        }
        finally
        {
            if (rawInputRegistered)
            {
                RawInputDevice[] removeDevices =
                [
                    new RawInputDevice
                    {
                        UsagePage = NativeMethods.HidUsagePageGeneric,
                        Usage = NativeMethods.HidUsageGenericKeyboard,
                        Flags = NativeMethods.RidevRemove,
                        TargetWindow = IntPtr.Zero
                    }
                ];
                NativeMethods.RegisterRawInputDevices(
                    removeDevices,
                    (uint)removeDevices.Length,
                    (uint)Marshal.SizeOf<RawInputDevice>());
            }
        }
    }

    private static void RunUpdateScheduleDiagnostic()
    {
        AppPaths appPaths = AppPaths.CreateDefault();
        appPaths.EnsureDirectories();
        string logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");

        try
        {
            DateTimeOffset startAtUtc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            GitUpdateSchedule schedule = new(GitUpdateSchedule.DefaultInterval);
            bool initialCheckIsDue = schedule.IsDue(startAtUtc, force: false);
            schedule.MarkCompleted(startAtUtc);
            bool earlyCheckIsBlocked = !schedule.IsDue(startAtUtc.AddMinutes(4).AddSeconds(59), force: false);
            bool fiveMinuteCheckIsDue = schedule.IsDue(startAtUtc.AddMinutes(5), force: false);
            bool forcedCheckIsDue = schedule.IsDue(startAtUtc.AddSeconds(1), force: true);
            schedule.Reset();
            bool resetCheckIsDue = schedule.IsDue(startAtUtc, force: false);
            const string diagnosticCommit = "c9e5ec18b1afb440e1f758119f929c2146071c44";
            bool informationalCommitDetected = string.Equals(
                GitUpdateSettingsStore.ExtractCommitFromInformationalVersion($"0.1.2+{diagnosticCommit}"),
                diagnosticCommit,
                StringComparison.OrdinalIgnoreCase);
            bool invalidInformationalCommitIgnored = string.IsNullOrEmpty(
                GitUpdateSettingsStore.ExtractCommitFromInformationalVersion("0.1.2+not-a-commit"));
            bool scheduleIsReady = initialCheckIsDue &&
                earlyCheckIsBlocked &&
                fiveMinuteCheckIsDue &&
                forcedCheckIsDue &&
                resetCheckIsDue &&
                informationalCommitDetected &&
                invalidInformationalCommitIgnored;

            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath) ?? AppContext.BaseDirectory);
            File.AppendAllText(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Update schedule check: {(scheduleIsReady ? "ready" : "failed")}. " +
                $"IntervalMinutes: {GitUpdateSchedule.DefaultInterval.TotalMinutes:0}. " +
                $"InitialDue: {initialCheckIsDue}. EarlyBlocked: {earlyCheckIsBlocked}. " +
                $"FiveMinuteDue: {fiveMinuteCheckIsDue}. ForcedDue: {forcedCheckIsDue}. ResetDue: {resetCheckIsDue}. " +
                $"InformationalCommitDetected: {informationalCommitDetected}. " +
                $"InvalidInformationalCommitIgnored: {invalidInformationalCommitIgnored}." +
                Environment.NewLine);
            Environment.ExitCode = scheduleIsReady ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            TryWriteDiagnosticFailure(logFilePath, "Update schedule check", exception);
            Environment.ExitCode = 1;
        }
    }

    private static void RunDurableConfigDiagnostic(string[] args)
    {
        AppPaths appPaths = CreateDiagnosticAppPaths(args);
        appPaths.EnsureDirectories();
        DiscordBotSettings? settings = new DiscordBotSettingsStore(appPaths).Load(out string statusText);
        bool durableConfigIsReady = File.Exists(appPaths.DurableEnvPath) && settings is not null;
        Environment.ExitCode = durableConfigIsReady ? 0 : 1;

        string logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath) ?? AppContext.BaseDirectory);
            File.AppendAllText(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Durable config check: " +
                $"{(durableConfigIsReady ? "ready" : "failed")}. Status: {statusText}. " +
                $"ProtectedFilePresent: {File.Exists(appPaths.DurableEnvPath)}.{Environment.NewLine}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static AppPaths CreateDiagnosticAppPaths(IEnumerable<string> args)
    {
        const string dataDirectoryPrefix = "--diagnostic-data-directory=";
        string? dataDirectoryArgument = args.FirstOrDefault(argument =>
            argument.StartsWith(dataDirectoryPrefix, StringComparison.OrdinalIgnoreCase));
        if (dataDirectoryArgument is null)
        {
            return AppPaths.CreateDefault();
        }

        string dataDirectory = dataDirectoryArgument[dataDirectoryPrefix.Length..].Trim().Trim('"');
        return AppPaths.CreateForDataDirectory(dataDirectory);
    }

    private static void RunGitUpdateDiagnostic(bool downloadInstaller)
    {
        AppPaths appPaths = AppPaths.CreateDefault();
        appPaths.EnsureDirectories();
        string logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");

        try
        {
            GitUpdateSettingsStore settingsStore = new(appPaths);
            GitUpdateCheckResult updateResult = new GitUpdateChecker(settingsStore)
                .CheckLatestReleaseAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            bool checkIsReady = updateResult.Status is GitUpdateCheckStatus.UpToDate or GitUpdateCheckStatus.UpdateAvailable;
            string downloadStatus = "not requested";

            if (downloadInstaller)
            {
                GitAutoUpdateResult downloadResult = new GitAutoUpdater(settingsStore, appPaths)
                    .DownloadAndValidateInstallerAsync(updateResult, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                downloadStatus = $"{downloadResult.Status}: {downloadResult.Message}";
                checkIsReady &= downloadResult.InstallerReady;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath) ?? AppContext.BaseDirectory);
            File.AppendAllText(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Git update {(downloadInstaller ? "download" : "check")}: " +
                $"{(checkIsReady ? "ready" : "failed")}. Status: {updateResult.Status}. " +
                $"Current: {updateResult.CurrentVersion}. Latest: {updateResult.LatestVersion}. " +
                $"InstallerAsset: {updateResult.DownloadUri}. DigestAvailable: {!string.IsNullOrWhiteSpace(updateResult.ExpectedSha256)}. " +
                $"DownloadValidation: {downloadStatus}.{Environment.NewLine}");
            Environment.ExitCode = checkIsReady ? 0 : 1;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            TryWriteDiagnosticFailure(logFilePath, downloadInstaller ? "Git update download" : "Git update check", exception);
            Environment.ExitCode = 1;
        }
    }

    private static void TryWriteDiagnosticFailure(string logFilePath, string diagnosticName, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath) ?? AppContext.BaseDirectory);
            File.AppendAllText(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] {diagnosticName} failed: {exception.Message}{Environment.NewLine}");
        }
        catch (Exception logException) when (logException is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void RunDiscordVoiceNativeDiagnostic()
    {
        AppPaths appPaths = AppPaths.CreateDefault();
        appPaths.EnsureDirectories();
        string logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");

        try
        {
            bool isReady = DiscordBotVoiceRelay.TryEnsureVoiceNativeDependencies(out string statusText);
            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath) ?? AppContext.BaseDirectory);
            File.AppendAllText(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Discord voice native check: {statusText}{Environment.NewLine}");
            Environment.ExitCode = isReady ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logFilePath) ?? AppContext.BaseDirectory);
                File.AppendAllText(
                    logFilePath,
                    $"{DateTimeOffset.Now:O} [Diagnostics] Discord voice native check failed: {exception.Message}{Environment.NewLine}");
            }
            catch (Exception logException) when (logException is IOException or UnauthorizedAccessException)
            {
            }

            Environment.ExitCode = 1;
        }
    }

    private static void RunMicrophoneDiagnostic()
    {
        AppPaths appPaths = AppPaths.CreateDefault();
        appPaths.EnsureDirectories();
        string logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");

        try
        {
            DiscordBotSettings? settings = new DiscordBotSettingsStore(appPaths).Load();
            string preferredMicrophoneDeviceName = settings?.MicrophoneDeviceName ?? string.Empty;
            float microphoneVolume = settings?.MicrophoneVolume ?? 0.85F;
            float microphoneNoiseGate = settings?.MicrophoneNoiseGate ?? 0F;
            MMDevice defaultMicrophoneDevice = DiscordBotVoiceRelay.GetDefaultMicrophoneDevice(preferredMicrophoneDeviceName);
            using WasapiCapture microphoneCapture = new(defaultMicrophoneDevice, useEventSync: false, audioBufferMillisecondsLength: 50);
            BufferedWaveProvider bufferedWaveProvider = new(microphoneCapture.WaveFormat)
            {
                BufferDuration = TimeSpan.FromMilliseconds(1600),
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };
            IWaveProvider discordPcmProvider = DiscordBotVoiceRelay.CreateDiscordPcmProvider(
                bufferedWaveProvider,
                microphoneVolume,
                microphoneNoiseGate);
            int capturedCallbackCount = 0;
            long capturedByteCount = 0;
            float capturedPeak = 0F;
            microphoneCapture.DataAvailable += (_, eventArgs) =>
            {
                if (eventArgs.BytesRecorded > 0)
                {
                    bufferedWaveProvider.AddSamples(eventArgs.Buffer, 0, eventArgs.BytesRecorded);
                    capturedCallbackCount++;
                    capturedByteCount += eventArgs.BytesRecorded;
                    capturedPeak = Math.Max(
                        capturedPeak,
                        DiscordBotVoiceRelay.CalculateAudioPeak(
                            microphoneCapture.WaveFormat,
                            eventArgs.Buffer,
                            0,
                            eventArgs.BytesRecorded));
                }
            };

            microphoneCapture.StartRecording();
            DateTime startupBufferDeadline = DateTime.UtcNow.AddSeconds(2);
            while (bufferedWaveProvider.BufferedDuration < TimeSpan.FromMilliseconds(260) &&
                DateTime.UtcNow < startupBufferDeadline)
            {
                Thread.Sleep(10);
            }

            byte[] testFrameBuffer = new byte[3840];
            int outputFrameCount = 0;
            int lastBytesRead = 0;
            float discordFramePeak = 0F;
            int silenceFrameCount = 0;
            int shortFrameCount = 0;
            DateTime diagnosticEndTime = DateTime.UtcNow.AddMilliseconds(1500);
            while (DateTime.UtcNow < diagnosticEndTime)
            {
                lastBytesRead = discordPcmProvider.Read(testFrameBuffer, 0, testFrameBuffer.Length);
                if (lastBytesRead <= 0)
                {
                    silenceFrameCount++;
                }
                else if (lastBytesRead < testFrameBuffer.Length)
                {
                    shortFrameCount++;
                }

                discordFramePeak = Math.Max(
                    discordFramePeak,
                    DiscordBotVoiceRelay.CalculateAudioPeak(
                        discordPcmProvider.WaveFormat,
                        testFrameBuffer,
                        0,
                        lastBytesRead));
                outputFrameCount++;
                Thread.Sleep(20);
            }

            microphoneCapture.StopRecording();

            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath) ?? AppContext.BaseDirectory);
            File.AppendAllText(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Microphone check: ready. Device: {defaultMicrophoneDevice.FriendlyName}. " +
                $"Preferred: {preferredMicrophoneDeviceName}. Source format: {microphoneCapture.WaveFormat}. " +
                $"Discord format: {discordPcmProvider.WaveFormat}. Captured callbacks: {capturedCallbackCount}. " +
                $"Captured bytes: {capturedByteCount}. Captured peak: {capturedPeak:0.0000}. " +
                $"Output frames: {outputFrameCount}. Last frame bytes: {lastBytesRead}. " +
                $"Silence frames: {silenceFrameCount}. Short frames: {shortFrameCount}. Output peak: {discordFramePeak:0.0000}. " +
                $"Volume: {microphoneVolume:0.00}. Noise gate: {microphoneNoiseGate:0.000}{Environment.NewLine}");
            Environment.ExitCode = 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or COMException)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logFilePath) ?? AppContext.BaseDirectory);
                File.AppendAllText(
                    logFilePath,
                    $"{DateTimeOffset.Now:O} [Diagnostics] Microphone check failed: {exception.Message}{Environment.NewLine}");
            }
            catch (Exception logException) when (logException is IOException or UnauthorizedAccessException)
            {
            }

            Environment.ExitCode = 1;
        }
    }

    private static void RunMicrophoneListDiagnostic()
    {
        AppPaths appPaths = AppPaths.CreateDefault();
        appPaths.EnsureDirectories();
        string logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");

        try
        {
            DiscordBotSettings? settings = new DiscordBotSettingsStore(appPaths).Load();
            string preferredMicrophoneDeviceName = settings?.MicrophoneDeviceName ?? string.Empty;
            IReadOnlyList<string> deviceNames = DiscordBotVoiceRelay.ListActiveMicrophoneDevices();
            IReadOnlyList<DiscordBotVoiceRelay.MicrophoneDeviceCandidate> orderedCandidates =
                DiscordBotVoiceRelay.ListMicrophoneDeviceCandidates(preferredMicrophoneDeviceName);
            string selectedDeviceName = orderedCandidates[0].Name;
            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath) ?? AppContext.BaseDirectory);
            File.AppendAllText(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Microphone devices: preferred=\"{preferredMicrophoneDeviceName}\"; " +
                $"selected=\"{selectedDeviceName}\"; candidates=[{string.Join(" | ", orderedCandidates.Select(candidate => candidate.Name))}]; " +
                $"active=[{string.Join(" | ", deviceNames)}]{Environment.NewLine}");
            Environment.ExitCode = deviceNames.Count > 0 ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or COMException)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logFilePath) ?? AppContext.BaseDirectory);
                File.AppendAllText(
                    logFilePath,
                    $"{DateTimeOffset.Now:O} [Diagnostics] Microphone device list failed: {exception.Message}{Environment.NewLine}");
            }
            catch (Exception logException) when (logException is IOException or UnauthorizedAccessException)
            {
            }

            Environment.ExitCode = 1;
        }
    }

    private static void RunLineLoopbackDiagnostic()
    {
        AppPaths appPaths = AppPaths.CreateDefault();
        appPaths.EnsureDirectories();
        string logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");

        try
        {
            DiscordBotSettings? settings = new DiscordBotSettingsStore(appPaths).Load();
            string[] processNames = settings?.LineAudioProcessNames.Length > 0
                ? settings.LineAudioProcessNames
                : ["LINE", "Line", "line"];
            string matchingLineProcesses = DescribeMatchingProcesses(processNames);
            int callbackCount = 0;
            long byteCount = 0;
            using ProcessLoopbackCapture capture = new(Environment.ProcessId);
            capture.DataAvailable += (_, eventArgs) =>
            {
                callbackCount++;
                byteCount += eventArgs.BytesRecorded;
            };

            capture.StartRecording();
            Thread.Sleep(700);
            capture.StopRecording();

            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath) ?? AppContext.BaseDirectory);
            File.AppendAllText(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] LINE process loopback check: ready. " +
                $"TestTargetPid: {Environment.ProcessId}. Format: {capture.WaveFormat}. " +
                $"Callbacks: {callbackCount}. Bytes: {byteCount}. " +
                $"ConfiguredProcessNames: {string.Join(",", processNames)}. MatchingProcesses: {matchingLineProcesses}{Environment.NewLine}");
            Environment.ExitCode = 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidCastException or TimeoutException or COMException)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logFilePath) ?? AppContext.BaseDirectory);
                File.AppendAllText(
                    logFilePath,
                    $"{DateTimeOffset.Now:O} [Diagnostics] LINE process loopback check failed: {exception.Message}{Environment.NewLine}");
            }
            catch (Exception logException) when (logException is IOException or UnauthorizedAccessException)
            {
            }

            Environment.ExitCode = 1;
        }
    }

    private static void RunDiscordAudioMixDiagnostic()
    {
        AppPaths appPaths = AppPaths.CreateDefault();
        appPaths.EnsureDirectories();
        string logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");

        try
        {
            byte[] micOnlyFrameBuffer = new byte[3840];
            IWaveProvider micOnlyProvider = DiscordBotVoiceRelay.CreateDiscordPcmProvider(
                new DiagnosticToneWaveProvider(440F, 0.18F),
                0.85F,
                0F);
            int micOnlyBytesRead = micOnlyProvider.Read(micOnlyFrameBuffer, 0, micOnlyFrameBuffer.Length);
            float micOnlyPeak = DiscordBotVoiceRelay.CalculateAudioPeak(
                micOnlyProvider.WaveFormat,
                micOnlyFrameBuffer,
                0,
                micOnlyBytesRead);

            byte[] mixedFrameBuffer = new byte[3840];
            IWaveProvider mixedProvider = DiscordBotVoiceRelay.CreateDiscordPcmProvider(
                new DiagnosticToneWaveProvider(440F, 0.18F),
                0.85F,
                0F,
                new DiagnosticToneWaveProvider(880F, 0.18F),
                0.45F);
            int mixedBytesRead = mixedProvider.Read(mixedFrameBuffer, 0, mixedFrameBuffer.Length);
            float mixedPeak = DiscordBotVoiceRelay.CalculateAudioPeak(
                mixedProvider.WaveFormat,
                mixedFrameBuffer,
                0,
                mixedBytesRead);

            byte[] quietVoiceFrameBuffer = new byte[3840];
            IWaveProvider quietVoiceProvider = DiscordBotVoiceRelay.CreateDiscordPcmProvider(
                new DiagnosticToneWaveProvider(440F, 0.008F),
                0.85F,
                0F);
            float quietVoicePeak = 0F;
            int quietVoiceBytesRead = 0;
            for (int frameIndex = 0; frameIndex < 12; frameIndex++)
            {
                quietVoiceBytesRead = quietVoiceProvider.Read(
                    quietVoiceFrameBuffer,
                    0,
                    quietVoiceFrameBuffer.Length);
                quietVoicePeak = Math.Max(
                    quietVoicePeak,
                    DiscordBotVoiceRelay.CalculateAudioPeak(
                        quietVoiceProvider.WaveFormat,
                        quietVoiceFrameBuffer,
                        0,
                        quietVoiceBytesRead));
            }

            byte[] loudVoiceFrameBuffer = new byte[3840];
            IWaveProvider loudVoiceProvider = DiscordBotVoiceRelay.CreateDiscordPcmProvider(
                new DiagnosticToneWaveProvider(440F, 0.75F),
                1F,
                0F);
            int loudVoiceBytesRead = loudVoiceProvider.Read(
                loudVoiceFrameBuffer,
                0,
                loudVoiceFrameBuffer.Length);
            float loudVoicePeak = DiscordBotVoiceRelay.CalculateAudioPeak(
                loudVoiceProvider.WaveFormat,
                loudVoiceFrameBuffer,
                0,
                loudVoiceBytesRead);

            byte[] lowNoiseFrameBuffer = new byte[3840];
            IWaveProvider lowNoiseProvider = DiscordBotVoiceRelay.CreateDiscordPcmProvider(
                new DiagnosticToneWaveProvider(440F, 0.0005F),
                0.85F,
                0F);
            float lowNoisePeak = 0F;
            int lowNoiseBytesRead = 0;
            for (int frameIndex = 0; frameIndex < 12; frameIndex++)
            {
                lowNoiseBytesRead = lowNoiseProvider.Read(
                    lowNoiseFrameBuffer,
                    0,
                    lowNoiseFrameBuffer.Length);
                lowNoisePeak = Math.Max(
                    lowNoisePeak,
                    DiscordBotVoiceRelay.CalculateAudioPeak(
                        lowNoiseProvider.WaveFormat,
                        lowNoiseFrameBuffer,
                        0,
                        lowNoiseBytesRead));
            }

            DateTimeOffset watchdogNow = new(2026, 1, 1, 0, 0, 10, TimeSpan.Zero);
            bool watchdogAllowsHealthyFrames = !DiscordBotVoiceRelay.ShouldRecoverStalledDiscordFrames(
                relayIsRunning: true,
                watchdogNow,
                watchdogNow.AddMilliseconds(-4999));
            bool watchdogRecoversStalledFrames = DiscordBotVoiceRelay.ShouldRecoverStalledDiscordFrames(
                relayIsRunning: true,
                watchdogNow,
                watchdogNow.AddSeconds(-5));
            bool watchdogIgnoresStoppedRelay = !DiscordBotVoiceRelay.ShouldRecoverStalledDiscordFrames(
                relayIsRunning: false,
                watchdogNow,
                watchdogNow.AddSeconds(-30));

            bool mixLooksReady = micOnlyBytesRead == micOnlyFrameBuffer.Length &&
                mixedBytesRead == mixedFrameBuffer.Length &&
                mixedPeak > micOnlyPeak * 1.05F &&
                quietVoiceBytesRead == quietVoiceFrameBuffer.Length &&
                quietVoicePeak >= 0.03F &&
                loudVoiceBytesRead == loudVoiceFrameBuffer.Length &&
                loudVoicePeak <= 0.95F &&
                lowNoiseBytesRead == lowNoiseFrameBuffer.Length &&
                lowNoisePeak <= 0.001F &&
                watchdogAllowsHealthyFrames &&
                watchdogRecoversStalledFrames &&
                watchdogIgnoresStoppedRelay;
            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath) ?? AppContext.BaseDirectory);
            File.AppendAllText(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Discord audio mix check: {(mixLooksReady ? "ready" : "failed")}. " +
                $"MicOnlyBytes: {micOnlyBytesRead}. MixedBytes: {mixedBytesRead}. " +
                $"MicOnlyPeak: {micOnlyPeak:0.0000}. MixedPeak: {mixedPeak:0.0000}. " +
                $"QuietVoiceBytes: {quietVoiceBytesRead}. QuietVoicePeak: {quietVoicePeak:0.0000}. " +
                $"LoudVoiceBytes: {loudVoiceBytesRead}. LoudVoicePeak: {loudVoicePeak:0.0000}. " +
                $"LowNoiseBytes: {lowNoiseBytesRead}. LowNoisePeak: {lowNoisePeak:0.0000}. " +
                $"WatchdogHealthy: {watchdogAllowsHealthyFrames}. WatchdogStalled: {watchdogRecoversStalledFrames}. " +
                $"WatchdogStopped: {watchdogIgnoresStoppedRelay}. " +
                "Sources: microphone+LINE. OutputPlayback: unchanged; no render device opened by this diagnostic." +
                $"{Environment.NewLine}");
            Environment.ExitCode = mixLooksReady ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logFilePath) ?? AppContext.BaseDirectory);
                File.AppendAllText(
                    logFilePath,
                    $"{DateTimeOffset.Now:O} [Diagnostics] Discord audio mix check failed: {exception.Message}{Environment.NewLine}");
            }
            catch (Exception logException) when (logException is IOException or UnauthorizedAccessException)
            {
            }

            Environment.ExitCode = 1;
        }
    }

    private static string DescribeMatchingProcesses(IEnumerable<string> processNames)
    {
        List<string> processDescriptions = [];
        foreach (string rawProcessName in processNames)
        {
            string processName = Path.GetFileNameWithoutExtension(rawProcessName.Trim());
            if (string.IsNullOrWhiteSpace(processName))
            {
                continue;
            }

            Process[] matchingProcesses;
            try
            {
                matchingProcesses = Process.GetProcessesByName(processName);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                continue;
            }

            foreach (Process process in matchingProcesses)
            {
                using (process)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            processDescriptions.Add($"{process.ProcessName}:{process.Id}");
                        }
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                    }
                }
            }
        }

        return processDescriptions.Count == 0 ? "none" : string.Join(",", processDescriptions);
    }

    private sealed class DiagnosticToneWaveProvider : IWaveProvider
    {
        private const int SampleRate = 48000;
        private readonly float amplitude;
        private readonly double phaseStep;
        private double phase;

        public DiagnosticToneWaveProvider(float frequencyHz, float amplitude)
        {
            if (frequencyHz <= 0F)
            {
                throw new ArgumentOutOfRangeException(nameof(frequencyHz), "Frequency must be positive.");
            }

            this.amplitude = Math.Clamp(amplitude, 0.0F, 0.95F);
            phaseStep = (Math.PI * 2.0 * frequencyHz) / SampleRate;
        }

        public WaveFormat WaveFormat { get; } = new(SampleRate, 16, 1);

        public int Read(byte[] buffer, int offset, int count)
        {
            if (offset < 0 || count <= 0 || offset >= buffer.Length)
            {
                return 0;
            }

            int writableBytes = Math.Min(count, buffer.Length - offset);
            int alignedBytes = writableBytes - (writableBytes % 2);
            int endOffset = offset + alignedBytes;

            for (int byteOffset = offset; byteOffset < endOffset; byteOffset += 2)
            {
                short sampleValue = (short)(Math.Sin(phase) * amplitude * short.MaxValue);
                buffer[byteOffset] = (byte)(sampleValue & 0xFF);
                buffer[byteOffset + 1] = (byte)((sampleValue >> 8) & 0xFF);
                phase += phaseStep;
                if (phase >= Math.PI * 2.0)
                {
                    phase -= Math.PI * 2.0;
                }
            }

            if (alignedBytes < writableBytes)
            {
                buffer[offset + alignedBytes] = 0;
            }

            return writableBytes;
        }
    }
}
