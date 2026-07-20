using System.Buffers.Binary;
using Discord;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Color = System.Drawing.Color;

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

        if (args.Any(argument => string.Equals(argument, "--check-discord-retry-policy", StringComparison.OrdinalIgnoreCase)))
        {
            RunDiscordRetryPolicyDiagnostic();
            return;
        }

        if (args.Any(argument => string.Equals(argument, "--check-self-update-rollback", StringComparison.OrdinalIgnoreCase)))
        {
            RunSelfUpdateRollbackDiagnostic();
            return;
        }

        if (args.Any(argument => string.Equals(argument, "--check-embedded-agent-resources", StringComparison.OrdinalIgnoreCase)))
        {
            RunEmbeddedAgentResourceDiagnostic();
            return;
        }

        if (args.Any(argument => string.Equals(argument, "--check-embedded-agent-repair", StringComparison.OrdinalIgnoreCase)))
        {
            RunEmbeddedAgentRepairDiagnostic();
            return;
        }

        if (args.Any(argument => string.Equals(argument, "--check-embedded-agent-existing-skip", StringComparison.OrdinalIgnoreCase)))
        {
            RunEmbeddedAgentExistingTargetSkipDiagnostic();
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

        if (args.Any(argument =>
            string.Equals(argument, "--check-transcription-local", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(argument, "--check-transcription-wav", StringComparison.OrdinalIgnoreCase)))
        {
            RunOfflineTranscriptionDiagnostic();
            return;
        }

        if (args.Any(argument => string.Equals(argument, "--check-running-app-snapshot", StringComparison.OrdinalIgnoreCase)))
        {
            RunRunningApplicationSnapshotDiagnostic();
            return;
        }

        if (args.Any(argument => string.Equals(argument, "--check-running-process-snapshot", StringComparison.OrdinalIgnoreCase)))
        {
            RunRunningProcessSnapshotDiagnostic();
            return;
        }

        if (args.Any(argument => string.Equals(argument, "--check-line-voice-trigger", StringComparison.OrdinalIgnoreCase)))
        {
            RunLineVoiceTriggerDiagnostic();
            return;
        }

        if (args.Any(argument => string.Equals(argument, "--check-discord-voice-context", StringComparison.OrdinalIgnoreCase)))
        {
            RunDiscordVoiceContextDiagnostic();
            return;
        }

        if (args.Any(argument => string.Equals(argument, "--check-discord-voice-state-filter", StringComparison.OrdinalIgnoreCase)))
        {
            RunDiscordVoiceStateFilterDiagnostic();
            return;
        }

        if (args.Any(argument => string.Equals(argument, "--check-watch-agent-supervisor", StringComparison.OrdinalIgnoreCase)))
        {
            RunWatchAgentSupervisorDiagnostic();
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

        if (args.Any(argument => string.Equals(argument, "--check-self-diagnostics-command", StringComparison.OrdinalIgnoreCase)))
        {
            RunSelfDiagnosticsCommandDiagnostic();
            return;
        }

        if (args.Any(argument => string.Equals(argument, "--check-screenshot-capture", StringComparison.OrdinalIgnoreCase)))
        {
            RunScreenshotCaptureDiagnostic();
            return;
        }

        if (args.Any(argument => string.Equals(argument, "--check-screenshot-command", StringComparison.OrdinalIgnoreCase)))
        {
            RunScreenshotCommandDiagnostic();
            return;
        }

        if (args.Any(argument => string.Equals(argument, "--check-stream-command", StringComparison.OrdinalIgnoreCase)))
        {
            RunStreamCommandDiagnostic();
            return;
        }

        if (args.Any(argument => string.Equals(argument, "--check-stream-server", StringComparison.OrdinalIgnoreCase)))
        {
            RunStreamServerDiagnostic();
            return;
        }

        if (args.Any(argument => string.Equals(argument, "--check-stream-60fps", StringComparison.OrdinalIgnoreCase)))
        {
            RunStream60FpsDiagnostic();
            return;
        }

        if (args.Any(argument => string.Equals(argument, "--check-stream-h264-fmp4", StringComparison.OrdinalIgnoreCase)))
        {
            RunStreamH264Fmp4Diagnostic();
            return;
        }

        if (args.Any(argument => string.Equals(argument, "--check-stream-smooth-live", StringComparison.OrdinalIgnoreCase)))
        {
            RunStreamSmoothLiveDiagnostic(args);
            return;
        }

        if (args.Any(argument => string.Equals(argument, "--check-stream-h264-hls", StringComparison.OrdinalIgnoreCase)))
        {
            RunStreamH264HlsDiagnostic();
            return;
        }

        if (args.Any(argument => string.Equals(argument, "--check-ffmpeg-tool", StringComparison.OrdinalIgnoreCase)))
        {
            RunFfmpegToolDiagnostic();
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
            List<string> primaryLogLines = [];
            for (int lineIndex = 1; lineIndex <= 130; lineIndex++)
            {
                primaryLogLines.Add($"old local line {lineIndex}");
            }

            primaryLogLines.AddRange(
                [
                    "normal runtime line",
                    "DISCORD_BOT_TOKEN=must-not-leak",
                    $"path={Path.Combine(userProfile, "Documents", "VALOWATCH")}",
                    $"2026-07-12T00:00:00+09:00 [Discord] Discord startup failed. Path: {Path.Combine(userProfile, "Documents", "VALOWATCH")}",
                    "2026-07-12T00:00:00+09:00 [Discord] Requested Discord notification sent. Message: already mirrored",
                    "Device: notification continuation must not mirror",
                    "CapturedPeak: 0.5123",
                    "2026-07-12T00:00:01+09:00 [Discord] Audio stats. CapturedCallbacks: 1. WrittenFrames: 1.",
                    "2026-07-12T00:00:02+09:00 [Overlay] Dedicated key-state monitor health. Responsive: True.",
                    "2026-07-12T00:00:03+09:00 [Discord] Discord.Net Warning: Gateway: WebSocket connection was closed",
                    "   at Discord.ConnectionManager.ConnectAsync()",
                    "2026-07-12T00:00:03+09:00 [Discord] Discord.Net Warning: Dave decrypt stream 123: Failed to decrypt audio packet for 123: DecryptionFailure",
                    "2026-07-12T00:00:03+09:00 [Discord] Discord.Net Warning: Dave encrypt stream: Failed to encrypt dave audio: MissingKeyRatchet",
                    "2026-07-12T00:00:03+09:00 GITHUB agent release lookup attempt 1/5 failed. Retrying in 2 seconds. Exception: HttpRequestException: api.github.com",
                    " ---> System.Net.Sockets.SocketException (11001): そのようなホストは不明です。",
                    "2026-07-12T00:00:04+09:00 GITHUB agent is already current. SHA-256 matches release: 1234.",
                    "2026-07-12T00:00:05+09:00 VALOWATCH Start agent maintenance was skipped; app update will continue. Exception: UnauthorizedAccessException: Access to the path 'VALOWATCH_Start.exe.download' is denied.",
                    "2026-07-12T00:00:06+09:00 Embedded agent resource repair is attempting extraction because the installed agent is missing or unreadable.",
                    "2026-07-12T00:00:07+09:00 Embedded agent resource could not be installed: UpdateAgent/GITHUB.exe Exception: IOException: remained unreadable after validation attempts.",
                    "2026-07-12T00:00:08+09:00 LocalAppData update directory could not be used; falling back to Temp. Exception: UnauthorizedAccessException: denied."
                ]);
            File.WriteAllLines(Path.Combine(dataLogsDirectory, "primary.log"), primaryLogLines);
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
            string initialText = FlattenRuntimeLogEmbeds(initialDeltas.SelectMany(delta => delta.DiscordEmbeds));
            foreach (RuntimeLogFileDelta delta in initialDeltas)
            {
                RuntimeLogMessageCollector.Commit(cursorPath, delta.CursorKey, delta.CurrentLineCount);
            }

            File.AppendAllText(
                Path.Combine(dataLogsDirectory, "primary.log"),
                $"incremental runtime line{Environment.NewLine}" +
                $"incremental important failure line{Environment.NewLine}");
            IReadOnlyList<RuntimeLogFileDelta> incrementalDeltas = RuntimeLogMessageCollector.Collect(
                cursorPath,
                "diagnostic-version",
                (dataLogsDirectory, "data-logs"),
                (tempLogsDirectory, "temp-logs"));
            string incrementalText = string.Join(
                Environment.NewLine,
                incrementalDeltas.SelectMany(delta => delta.DiscordEmbeds).Select(DescribeRuntimeLogEmbed));
            string[] cursorKeys = initialDeltas.Select(delta => delta.CursorKey).ToArray();
            string[] initialMessageLines = initialText.Split(
                [Environment.NewLine],
                StringSplitOptions.None);
            List<string> failedChecks = [];
            AddDiagnosticCheck(
                cursorKeys.Contains("data-logs/primary.log", StringComparer.OrdinalIgnoreCase),
                "primary cursor missing",
                failedChecks);
            AddDiagnosticCheck(
                cursorKeys.Contains("temp-logs/nested/SECOND.LOG", StringComparer.OrdinalIgnoreCase),
                "nested cursor missing",
                failedChecks);
            AddDiagnosticCheck(
                !cursorKeys.Any(key => key.EndsWith(".env", StringComparison.OrdinalIgnoreCase)),
                "env file was included",
                failedChecks);
            AddDiagnosticCheck(
                initialDeltas.SelectMany(delta => delta.DiscordEmbeds).All(embed =>
                    string.Equals(embed.Title, "VALOWATCH ログ", StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(embed.Description) &&
                    embed.Description.Length <= 4096 &&
                    !embed.Description.Contains("```", StringComparison.Ordinal)),
                "discord embed framing failed",
                failedChecks);
            AddDiagnosticCheck(
                !initialText.Contains("first Discord log sync skipped older", StringComparison.Ordinal),
                "initial backlog skip notice was mirrored",
                failedChecks);
            AddDiagnosticCheck(
                !initialMessageLines.Any(line => string.Equals(
                    line,
                    "1: old local line 1",
                    StringComparison.Ordinal)),
                "old first line was mirrored",
                failedChecks);
            AddDiagnosticCheck(
                !initialText.Contains("normal runtime line", StringComparison.Ordinal),
                "normal line mirrored",
                failedChecks);
            AddDiagnosticCheck(
                initialText.Contains("Discord startup failed", StringComparison.Ordinal),
                "important failure line missing",
                failedChecks);
            AddDiagnosticCheck(
                !initialText.Contains("must-not-leak", StringComparison.Ordinal),
                "secret leaked",
                failedChecks);
            AddDiagnosticCheck(
                !initialText.Contains(userProfile, StringComparison.OrdinalIgnoreCase),
                "profile path leaked",
                failedChecks);
            AddDiagnosticCheck(
                initialText.Contains("%USERPROFILE%", StringComparison.Ordinal),
                "profile path was not redacted",
                failedChecks);
            AddDiagnosticCheck(
                !initialText.Contains("Requested Discord notification sent", StringComparison.Ordinal),
                "self notification log mirrored",
                failedChecks);
            AddDiagnosticCheck(
                !initialText.Contains("Device: notification continuation", StringComparison.Ordinal) &&
                    !initialText.Contains("CapturedPeak: 0.5123", StringComparison.Ordinal),
                "notification continuation mirrored",
                failedChecks);
            AddDiagnosticCheck(
                !initialText.Contains("Audio stats", StringComparison.Ordinal),
                "audio stats mirrored",
                failedChecks);
            AddDiagnosticCheck(
                !initialText.Contains("Dedicated key-state monitor health", StringComparison.Ordinal),
                "overlay heartbeat mirrored",
                failedChecks);
            AddDiagnosticCheck(
                !initialText.Contains("WebSocket connection was closed", StringComparison.Ordinal),
                "websocket reconnect stack mirrored",
                failedChecks);
            AddDiagnosticCheck(
                !initialText.Contains("Dave decrypt", StringComparison.Ordinal) &&
                    !initialText.Contains("Dave encrypt", StringComparison.Ordinal) &&
                    !initialText.Contains("DecryptionFailure", StringComparison.Ordinal) &&
                    !initialText.Contains("MissingKeyRatchet", StringComparison.Ordinal),
                "dave transition warning mirrored",
                failedChecks);
            AddDiagnosticCheck(
                !initialText.Contains("api.github.com", StringComparison.OrdinalIgnoreCase) &&
                    !initialText.Contains("そのようなホストは不明です", StringComparison.Ordinal),
                "github dns retry stack mirrored",
                failedChecks);
            AddDiagnosticCheck(
                !initialText.Contains("GITHUB agent is already current", StringComparison.Ordinal),
                "routine updater success mirrored",
                failedChecks);
            AddDiagnosticCheck(
                !initialText.Contains("Start agent maintenance was skipped", StringComparison.Ordinal) &&
                    !initialText.Contains("Embedded agent resource repair", StringComparison.Ordinal) &&
                    !initialText.Contains("Embedded agent resource could not be installed", StringComparison.Ordinal) &&
                    !initialText.Contains("LocalAppData update directory could not be used", StringComparison.Ordinal),
                "routine agent maintenance warning mirrored",
                failedChecks);
            AddDiagnosticCheck(
                incrementalText.Contains("incremental important failure line", StringComparison.Ordinal),
                "incremental failure line missing",
                failedChecks);
            AddDiagnosticCheck(
                !incrementalText.Contains("incremental runtime line", StringComparison.Ordinal),
                "incremental routine line mirrored",
                failedChecks);

            bool ready = failedChecks.Count == 0;

            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath) ?? appPaths.DataDirectory);
            File.AppendAllText(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Runtime log message check: {(ready ? "ready" : "failed")}. " +
                $"Files: {string.Join(",", cursorKeys)}. InitialMessages: " +
                $"{initialDeltas.Sum(delta => delta.DiscordEmbeds.Count)}. " +
                $"IncrementalMessages: {incrementalDeltas.Sum(delta => delta.DiscordEmbeds.Count)}. " +
                $"Failures: {(ready ? "none" : string.Join("; ", failedChecks))}.{Environment.NewLine}");
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

    private static void RunSelfDiagnosticsCommandDiagnostic()
    {
        AppPaths appPaths = AppPaths.CreateDefault();
        appPaths.EnsureDirectories();
        string logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");

        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(5));
            IReadOnlyList<Embed> embeds = ValowatchSelfDiagnostics
                .BuildDiscordEmbedsAsync(appPaths, includeUpdateDownload: false, timeout.Token)
                .GetAwaiter()
                .GetResult();
            bool ready = embeds.Count >= 3 &&
                embeds.Any(embed => embed.Title?.Contains("自己診断", StringComparison.Ordinal) == true) &&
                embeds.Any(embed => string.Equals(embed.Title, "VALOWATCH フォルダー状況", StringComparison.Ordinal)) &&
                embeds.Any(embed => string.Equals(embed.Title, "VALOWATCH Root直下", StringComparison.Ordinal)) &&
                embeds.All(embed =>
                    string.IsNullOrEmpty(embed.Description) ||
                    embed.Description.Length <= 4096);

            AppendDiagnosticLogLine(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Self diagnostics slash command check: " +
                $"{(ready ? "ready" : "failed")}. Embeds: {embeds.Count}.");
            Environment.ExitCode = ready ? 0 : 1;
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            TryWriteDiagnosticFailure(logFilePath, "Self diagnostics slash command check", exception);
            Environment.ExitCode = 1;
        }
    }

    private static void RunScreenshotCaptureDiagnostic()
    {
        AppPaths appPaths = AppPaths.CreateDefault();
        appPaths.EnsureDirectories();
        string logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");
        string screenshotPath = string.Empty;

        try
        {
            FullScreenScreenshotResult screenshot = FullScreenScreenshotCapture.CaptureToJpeg(appPaths.ScreenshotTempDirectory);
            screenshotPath = screenshot.FilePath;
            bool fileLooksValid = File.Exists(screenshot.FilePath) && screenshot.FileBytes > 0;

            AppendDiagnosticLogLine(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Screenshot capture check: " +
                $"{(fileLooksValid ? "ready" : "failed")}. " +
                $"Size: {screenshot.Width}x{screenshot.Height}. Screens: {screenshot.ScreenCount}. " +
                $"Bytes: {screenshot.FileBytes}. TemporaryFileDeletedAfterCheck: true.");
            Environment.ExitCode = fileLooksValid ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException or ExternalException or System.ComponentModel.Win32Exception)
        {
            TryWriteDiagnosticFailure(logFilePath, "Screenshot capture check", exception);
            Environment.ExitCode = 1;
        }
        finally
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(screenshotPath) && File.Exists(screenshotPath))
                {
                    File.Delete(screenshotPath);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                TryWriteDiagnosticFailure(logFilePath, "Screenshot capture cleanup", exception);
            }
        }
    }

    private static void RunScreenshotCommandDiagnostic()
    {
        AppPaths appPaths = AppPaths.CreateDefault();
        appPaths.EnsureDirectories();
        string logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");

        try
        {
            object builtCommand = DiscordBotVoiceRelay
                .BuildScreenshotSlashCommandBuilder()
                .Build();
            bool ready = builtCommand is not null;
            AppendDiagnosticLogLine(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Screenshot slash command check: " +
                $"{(ready ? "ready" : "failed")}. Subcommands: on,off,now. DefaultState: off.");
            Environment.ExitCode = ready ? 0 : 1;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            TryWriteDiagnosticFailure(logFilePath, "Screenshot slash command check", exception);
            Environment.ExitCode = 1;
        }
    }

    private static void RunStreamCommandDiagnostic()
    {
        AppPaths appPaths = AppPaths.CreateDefault();
        appPaths.EnsureDirectories();
        string logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");

        try
        {
            object builtCommand = DiscordBotVoiceRelay
                .BuildStreamSlashCommandBuilder()
                .Build();
            bool ready = builtCommand is not null;
            AppendDiagnosticLogLine(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Stream slash command check: " +
                $"{(ready ? "ready" : "failed")}. Subcommands: on,off,status. Targets: full,valorant. " +
                $"Methods: {ScreenStreamMethodNames.H264Fmp4},{ScreenStreamMethodNames.H264Hls},{ScreenStreamMethodNames.Mjpeg}. " +
                $"Options: method,fps,quality,width. MaxFPS: {ScreenStreamingServer.MaximumFramesPerSecond}.");
            Environment.ExitCode = ready ? 0 : 1;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            TryWriteDiagnosticFailure(logFilePath, "Stream slash command check", exception);
            Environment.ExitCode = 1;
        }
    }

    private static void RunStreamServerDiagnostic()
    {
        AppPaths appPaths = AppPaths.CreateDefault();
        appPaths.EnsureDirectories();
        string logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");
        ScreenStreamingServer? server = null;

        try
        {
            List<string> serverMessages = [];
            ScreenStreamOptions options = ScreenStreamOptions.Create(
                ScreenCaptureTarget.FullScreen,
                15,
                65,
                960,
                ScreenStreamMethod.Mjpeg);
            server = ScreenStreamingServer.Start(
                options,
                (message, exception) =>
                {
                    string exceptionText = exception is null ? string.Empty : $" Exception: {exception.Message}";
                    serverMessages.Add($"{message}{exceptionText}");
                });

            using HttpClient httpClient = new()
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
            string pageUrl = $"{server.LocalOrigin}/{server.PublicPath}";
            string frameUrl = $"{pageUrl}/frame.jpg";
            string pageHtml = httpClient.GetStringAsync(pageUrl).GetAwaiter().GetResult();
            byte[] frameBytes = httpClient.GetByteArrayAsync(frameUrl).GetAwaiter().GetResult();
            bool frameLooksLikeJpeg = frameBytes.Length > 1024 &&
                frameBytes[0] == 0xFF &&
                frameBytes[1] == 0xD8;
            bool ready = pageHtml.Contains("VALOWATCH stream", StringComparison.OrdinalIgnoreCase) &&
                frameLooksLikeJpeg;

            AppendDiagnosticLogLine(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Stream server check: " +
                $"{(ready ? "ready" : "failed")}. PageBytes: {Encoding.UTF8.GetByteCount(pageHtml)}. " +
                $"FrameBytes: {frameBytes.Length}. FrameJpeg: {frameLooksLikeJpeg}. " +
                $"Messages: {string.Join(" | ", serverMessages.TakeLast(4))}.");
            Environment.ExitCode = ready ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException or HttpRequestException or TaskCanceledException or ExternalException or System.ComponentModel.Win32Exception)
        {
            TryWriteDiagnosticFailure(logFilePath, "Stream server check", exception);
            Environment.ExitCode = 1;
        }
        finally
        {
            server?.Dispose();
        }
    }

    private static void RunStream60FpsDiagnostic()
    {
        AppPaths appPaths = AppPaths.CreateDefault();
        appPaths.EnsureDirectories();
        string logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");
        ScreenStreamingServer? server = null;

        try
        {
            const int diagnosticFramesPerSecond = 60;
            ScreenStreamOptions options = ScreenStreamOptions.Create(
                ScreenCaptureTarget.FullScreen,
                diagnosticFramesPerSecond,
                ScreenStreamingServer.MinimumJpegQuality,
                ScreenStreamingServer.MinimumMaxWidth,
                ScreenStreamMethod.Mjpeg);
            string ffmpegPath = FfmpegToolProvider
                .ResolveFfmpegPathAsync(
                    appPaths,
                    (message, exception) =>
                    {
                        string exceptionText = exception is null ? string.Empty : $" Exception: {exception.Message}";
                        AppendDiagnosticLogLine(logFilePath, $"{DateTimeOffset.Now:O} [Diagnostics] FFmpeg setup: {message}{exceptionText}");
                    },
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            List<string> serverMessages = [];
            server = ScreenStreamingServer.Start(
                options,
                ffmpegPath,
                (message, exception) =>
                {
                    string exceptionText = exception is null ? string.Empty : $" Exception: {exception.Message}";
                    serverMessages.Add($"{message}{exceptionText}");
                });

            using HttpClient httpClient = new()
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
            string pageUrl = $"{server.LocalOrigin}/{server.PublicPath}";
            string streamUrl = $"{pageUrl}/stream.mjpg";
            string pageHtml = httpClient.GetStringAsync(pageUrl).GetAwaiter().GetResult();

            using CancellationTokenSource readTimeout = new(TimeSpan.FromSeconds(5));
            using HttpResponseMessage response = httpClient
                .GetAsync(streamUrl, HttpCompletionOption.ResponseHeadersRead, readTimeout.Token)
                .GetAwaiter()
                .GetResult();
            response.EnsureSuccessStatusCode();

            using Stream responseStream = response.Content
                .ReadAsStreamAsync(readTimeout.Token)
                .GetAwaiter()
                .GetResult();
            byte[] streamBuffer = new byte[16 * 1024 * 1024];
            int totalReadBytes = 0;
            int jpegStartMarkers = 0;
            int jpegStartMarkersAtSampleStart = 0;
            Stopwatch? sampleStopwatch = null;
            Stopwatch readStopwatch = Stopwatch.StartNew();
            while (readStopwatch.Elapsed < TimeSpan.FromSeconds(6) &&
                   totalReadBytes < streamBuffer.Length)
            {
                int remainingBytes = streamBuffer.Length - totalReadBytes;
                int readByteCount = responseStream
                    .ReadAsync(
                        streamBuffer.AsMemory(totalReadBytes, Math.Min(remainingBytes, 65536)),
                        readTimeout.Token)
                    .GetAwaiter()
                    .GetResult();
                if (readByteCount == 0)
                {
                    break;
                }

                totalReadBytes += readByteCount;
                jpegStartMarkers = CountJpegStartMarkers(streamBuffer.AsSpan(0, totalReadBytes));
                if (sampleStopwatch is null && jpegStartMarkers > 0)
                {
                    sampleStopwatch = Stopwatch.StartNew();
                    jpegStartMarkersAtSampleStart = jpegStartMarkers;
                }

                if (sampleStopwatch?.Elapsed >= TimeSpan.FromSeconds(2))
                {
                    break;
                }
            }

            double sampleSeconds = Math.Max(0.001D, sampleStopwatch?.Elapsed.TotalSeconds ?? readStopwatch.Elapsed.TotalSeconds);
            int sampledJpegStartMarkers = sampleStopwatch is null
                ? jpegStartMarkers
                : Math.Max(0, jpegStartMarkers - jpegStartMarkersAtSampleStart);
            double observedMjpegMarkerRate = sampledJpegStartMarkers / sampleSeconds;
            bool contentTypeLooksLikeMjpeg = response.Content.Headers.ContentType?.MediaType
                ?.Equals("multipart/x-mixed-replace", StringComparison.OrdinalIgnoreCase) == true;
            bool ready = pageHtml.Contains("VALOWATCH stream", StringComparison.OrdinalIgnoreCase) &&
                contentTypeLooksLikeMjpeg &&
                jpegStartMarkers > 0 &&
                options.FramesPerSecond == diagnosticFramesPerSecond;

            AppendDiagnosticLogLine(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Stream 60fps check: " +
                $"{(ready ? "ready" : "failed")}. ConfiguredFPS: {options.FramesPerSecond}. " +
                $"Quality: {options.JpegQuality}. Width: {options.MaxWidth}. Engine: {server.EngineName}. " +
                $"MjpegContentType: {contentTypeLooksLikeMjpeg}. " +
                $"ReadBytes: {totalReadBytes}. JpegMarkers: {jpegStartMarkers}. SampledJpegMarkers: {sampledJpegStartMarkers}. " +
                $"ObservedMarkerRate: {observedMjpegMarkerRate.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}. " +
                $"Messages: {string.Join(" | ", serverMessages.TakeLast(4))}.");
            Environment.ExitCode = ready ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException or HttpRequestException or TaskCanceledException or ExternalException or System.ComponentModel.Win32Exception)
        {
            TryWriteDiagnosticFailure(logFilePath, "Stream 60fps check", exception);
            Environment.ExitCode = 1;
        }
        finally
        {
            server?.Dispose();
        }
    }

    private static void RunStreamH264Fmp4Diagnostic()
    {
        AppPaths appPaths = AppPaths.CreateDefault();
        appPaths.EnsureDirectories();
        string logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");
        ScreenStreamingServer? server = null;
        List<string> serverMessages = [];

        try
        {
            ScreenStreamOptions options = ScreenStreamOptions.Create(
                ScreenCaptureTarget.FullScreen,
                60,
                90,
                1280,
                ScreenStreamMethod.H264Fmp4);
            string ffmpegPath = ResolveFfmpegForDiagnostic(appPaths, logFilePath);
            server = ScreenStreamingServer.Start(
                options,
                ffmpegPath,
                Path.Combine(appPaths.DataDirectory, "streaming"),
                (message, exception) =>
                {
                    string exceptionText = exception is null ? string.Empty : $" Exception: {exception.Message}";
                    serverMessages.Add($"{message}{exceptionText}");
                });

            using HttpClient httpClient = new()
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            string pageUrl = $"{server.LocalOrigin}/{server.PublicPath}";
            string streamUrl = $"{pageUrl}/stream.mp4";
            string liveInitUrl = $"{pageUrl}/live/init.mp4";
            string liveFragmentUrl = $"{pageUrl}/live/fragment/0.m4s";
            string liveWebSocketPath = $"/{server.PublicPath}/live/ws";
            string pageHtml = httpClient.GetStringAsync(pageUrl).GetAwaiter().GetResult();
            using CancellationTokenSource readTimeout = new(TimeSpan.FromSeconds(18));
            using HttpResponseMessage response = httpClient
                .GetAsync(streamUrl, HttpCompletionOption.ResponseHeadersRead, readTimeout.Token)
                .GetAwaiter()
                .GetResult();
            response.EnsureSuccessStatusCode();

            byte[] streamBytes = ReadSomeResponseBytes(response, minimumBytes: 1024 * 128, maximumBytes: 1024 * 1024, readTimeout.Token);
            using HttpResponseMessage liveInitResponse = httpClient
                .GetAsync(liveInitUrl, HttpCompletionOption.ResponseHeadersRead, readTimeout.Token)
                .GetAwaiter()
                .GetResult();
            liveInitResponse.EnsureSuccessStatusCode();
            byte[] liveInitBytes = liveInitResponse.Content.ReadAsByteArrayAsync(readTimeout.Token).GetAwaiter().GetResult();
            using HttpResponseMessage liveFragmentResponse = httpClient
                .GetAsync(liveFragmentUrl, HttpCompletionOption.ResponseHeadersRead, readTimeout.Token)
                .GetAwaiter()
                .GetResult();
            liveFragmentResponse.EnsureSuccessStatusCode();
            byte[] liveFragmentBytes = liveFragmentResponse.Content.ReadAsByteArrayAsync(readTimeout.Token).GetAwaiter().GetResult();
            long firstLiveFragmentSequence = -1L;
            if (liveFragmentResponse.Headers.TryGetValues("X-VALOWATCH-Sequence", out IEnumerable<string>? firstSequenceValues))
            {
                _ = long.TryParse(
                    firstSequenceValues.FirstOrDefault(),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out firstLiveFragmentSequence);
            }

            string nextLiveFragmentUrl = $"{pageUrl}/live/fragment/{(firstLiveFragmentSequence + 1L).ToString(System.Globalization.CultureInfo.InvariantCulture)}.m4s";
            using HttpResponseMessage nextLiveFragmentResponse = httpClient
                .GetAsync(nextLiveFragmentUrl, HttpCompletionOption.ResponseHeadersRead, readTimeout.Token)
                .GetAwaiter()
                .GetResult();
            nextLiveFragmentResponse.EnsureSuccessStatusCode();
            byte[] nextLiveFragmentBytes = nextLiveFragmentResponse.Content.ReadAsByteArrayAsync(readTimeout.Token).GetAwaiter().GetResult();
            Fmp4WebSocketDiagnosticResult webSocketResult = ReadFmp4WebSocketDiagnosticFrames(
                server.LocalOrigin,
                liveWebSocketPath,
                readTimeout.Token);
            bool contentTypeLooksLikeMp4 = response.Content.Headers.ContentType?.MediaType
                ?.Equals("video/mp4", StringComparison.OrdinalIgnoreCase) == true;
            bool liveInitContentTypeLooksLikeMp4 = liveInitResponse.Content.Headers.ContentType?.MediaType
                ?.Equals("video/mp4", StringComparison.OrdinalIgnoreCase) == true;
            bool liveFragmentContentTypeLooksLikeSegment = liveFragmentResponse.Content.Headers.ContentType?.MediaType
                ?.Equals("video/iso.segment", StringComparison.OrdinalIgnoreCase) == true;
            bool nextLiveFragmentContentTypeLooksLikeSegment = nextLiveFragmentResponse.Content.Headers.ContentType?.MediaType
                ?.Equals("video/iso.segment", StringComparison.OrdinalIgnoreCase) == true;
            long nextLiveFragmentSequence = -1L;
            bool nextLiveFragmentHasSequenceHeader =
                nextLiveFragmentResponse.Headers.TryGetValues("X-VALOWATCH-Sequence", out IEnumerable<string>? nextSequenceValues) &&
                long.TryParse(
                    nextSequenceValues.FirstOrDefault(),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out nextLiveFragmentSequence);
            bool liveFragmentHasSequenceHeader = firstLiveFragmentSequence >= 0L;
            bool liveFragmentSequenceAdvances = nextLiveFragmentHasSequenceHeader &&
                nextLiveFragmentSequence >= firstLiveFragmentSequence + 1L;
            bool streamLooksLikeFragmentedMp4 =
                ContainsAsciiToken(streamBytes, "ftyp") &&
                (ContainsAsciiToken(streamBytes, "moov") || ContainsAsciiToken(streamBytes, "moof") || ContainsAsciiToken(streamBytes, "mdat"));
            bool liveInitLooksLikeInitializationSegment =
                ContainsAsciiToken(liveInitBytes, "ftyp") &&
                ContainsAsciiToken(liveInitBytes, "moov");
            bool liveFragmentLooksLikeMediaSegment =
                ContainsAsciiToken(liveFragmentBytes, "moof") &&
                ContainsAsciiToken(liveFragmentBytes, "mdat");
            bool nextLiveFragmentLooksLikeMediaSegment =
                ContainsAsciiToken(nextLiveFragmentBytes, "moof") &&
                ContainsAsciiToken(nextLiveFragmentBytes, "mdat");
            bool pageHasVideoControls = pageHtml.Contains(" controls", StringComparison.OrdinalIgnoreCase);
            string targetLatencySecondsText = ScreenStreamingServer.H264Fmp4TargetLatencySeconds
                .ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            string minimumSmoothLatencySecondsText = ScreenStreamingServer.H264Fmp4MinimumSmoothLatencySeconds
                .ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            string maximumLatencySecondsText = ScreenStreamingServer.H264Fmp4MaximumLatencySeconds
                .ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            string restartLatencySecondsText = ScreenStreamingServer.H264Fmp4RestartLatencySeconds
                .ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            string latencyCheckIntervalMillisecondsText = ScreenStreamingServer.H264Fmp4LatencyCheckIntervalMilliseconds
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            string seekCooldownMillisecondsText = ScreenStreamingServer.H264Fmp4SeekCooldownMilliseconds
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            string reconnectStallMillisecondsText = ScreenStreamingServer.H264Fmp4ReconnectStallMilliseconds
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            string maximumAppendQueueLengthText = ScreenStreamingServer.H264Fmp4MaximumAppendQueueLength
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            bool pageHasSmoothLiveController =
                pageHtml.Contains("keepFmp4LatencyLow", StringComparison.OrdinalIgnoreCase) &&
                pageHtml.Contains("smooth-live", StringComparison.OrdinalIgnoreCase) &&
                pageHtml.Contains("valowatchStreamMetrics", StringComparison.OrdinalIgnoreCase) &&
                pageHtml.Contains($"targetLatencySeconds = {targetLatencySecondsText}", StringComparison.OrdinalIgnoreCase) &&
                pageHtml.Contains($"minimumSmoothLatencySeconds = {minimumSmoothLatencySecondsText}", StringComparison.OrdinalIgnoreCase) &&
                pageHtml.Contains($"maximumLatencySeconds = {maximumLatencySecondsText}", StringComparison.OrdinalIgnoreCase) &&
                pageHtml.Contains($"restartLatencySeconds = {restartLatencySecondsText}", StringComparison.OrdinalIgnoreCase) &&
                pageHtml.Contains($"latencyCheckMilliseconds = {latencyCheckIntervalMillisecondsText}", StringComparison.OrdinalIgnoreCase) &&
                pageHtml.Contains($"seekCooldownMilliseconds = {seekCooldownMillisecondsText}", StringComparison.OrdinalIgnoreCase) &&
                pageHtml.Contains($"reconnectStallMilliseconds = {reconnectStallMillisecondsText}", StringComparison.OrdinalIgnoreCase) &&
                pageHtml.Contains($"maximumAppendQueueLength = {maximumAppendQueueLengthText}", StringComparison.OrdinalIgnoreCase) &&
                pageHtml.Contains("hasFmp4SmoothStartupBuffer", StringComparison.OrdinalIgnoreCase) &&
                pageHtml.Contains("queueOverflowCount", StringComparison.OrdinalIgnoreCase) &&
                pageHtml.Contains("reconnectFmp4Stream", StringComparison.OrdinalIgnoreCase);
            bool pageHasVisibilityRecovery =
                pageHtml.Contains("visibilitychange", StringComparison.OrdinalIgnoreCase) &&
                pageHtml.Contains("suspendFmp4ForHiddenPage", StringComparison.OrdinalIgnoreCase) &&
                pageHtml.Contains("resumeFmp4FromHiddenPage", StringComparison.OrdinalIgnoreCase) &&
                pageHtml.Contains("hiddenStreamSuspended", StringComparison.OrdinalIgnoreCase);
            bool pageHasMseLiveController =
                pageHtml.Contains("MediaSource", StringComparison.OrdinalIgnoreCase) &&
                pageHtml.Contains("live/init.mp4", StringComparison.OrdinalIgnoreCase) &&
                pageHtml.Contains("live/fragment/", StringComparison.OrdinalIgnoreCase) &&
                pageHtml.Contains("live/ws", StringComparison.OrdinalIgnoreCase) &&
                pageHtml.Contains("WebSocket", StringComparison.OrdinalIgnoreCase) &&
                pageHtml.Contains("startWebSocketFmp4Live", StringComparison.OrdinalIgnoreCase) &&
                pageHtml.Contains("queueFmp4Bytes", StringComparison.OrdinalIgnoreCase) &&
                pageHtml.Contains("X-VALOWATCH-Sequence", StringComparison.OrdinalIgnoreCase);
            bool fmp4SmoothLiveConfigurationReady =
                ScreenStreamingServer.H264Fmp4TargetLatencySeconds >= 1.7D &&
                ScreenStreamingServer.H264Fmp4TargetLatencySeconds <= 2.6D &&
                ScreenStreamingServer.H264Fmp4MinimumSmoothLatencySeconds >= 1.4D &&
                ScreenStreamingServer.H264Fmp4MinimumSmoothLatencySeconds < ScreenStreamingServer.H264Fmp4TargetLatencySeconds &&
                ScreenStreamingServer.H264Fmp4CatchUpLatencySeconds > ScreenStreamingServer.H264Fmp4TargetLatencySeconds &&
                ScreenStreamingServer.H264Fmp4MaximumLatencySeconds <= 3.0D &&
                ScreenStreamingServer.H264Fmp4RestartLatencySeconds <= 4.5D &&
                ScreenStreamingServer.H264Fmp4FragmentDurationMicroseconds >= 450000 &&
                ScreenStreamingServer.H264Fmp4FragmentDurationMicroseconds <= 550000 &&
                ScreenStreamingServer.H264Fmp4InitialFragmentCount >= 4 &&
                ScreenStreamingServer.H264Fmp4RetainedFragmentCount >= 30 &&
                ScreenStreamingServer.H264Fmp4MaximumAppendQueueLength >= 12 &&
                ScreenStreamingServer.H264KeyframeIntervalSeconds >= 0.45D &&
                ScreenStreamingServer.H264KeyframeIntervalSeconds <= 0.55D;
            bool fmp4FragmentDurationConfigured = serverMessages.Any(message =>
                message.Contains($"FragmentDurationUs: {ScreenStreamingServer.H264Fmp4FragmentDurationMicroseconds}", StringComparison.OrdinalIgnoreCase));
            bool fmp4KeyframeIntervalConfigured = serverMessages.Any(message =>
                message.Contains("GopFrames:", StringComparison.OrdinalIgnoreCase));
            bool ready = pageHtml.Contains("VALOWATCH stream", StringComparison.OrdinalIgnoreCase) &&
                pageHtml.Contains("stream.mp4", StringComparison.OrdinalIgnoreCase) &&
                !pageHasVideoControls &&
                pageHasSmoothLiveController &&
                pageHasVisibilityRecovery &&
                pageHasMseLiveController &&
                fmp4SmoothLiveConfigurationReady &&
                fmp4FragmentDurationConfigured &&
                fmp4KeyframeIntervalConfigured &&
                contentTypeLooksLikeMp4 &&
                streamLooksLikeFragmentedMp4 &&
                liveInitContentTypeLooksLikeMp4 &&
                liveInitLooksLikeInitializationSegment &&
                liveFragmentContentTypeLooksLikeSegment &&
                liveFragmentHasSequenceHeader &&
                liveFragmentLooksLikeMediaSegment &&
                nextLiveFragmentContentTypeLooksLikeSegment &&
                liveFragmentSequenceAdvances &&
                nextLiveFragmentLooksLikeMediaSegment &&
                webSocketResult.Accepted &&
                webSocketResult.InitLooksLikeInitializationSegment &&
                webSocketResult.FragmentLooksLikeMediaSegment &&
                options.FramesPerSecond >= 60;

            AppendDiagnosticLogLine(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Stream h264-fmp4 check: " +
                $"{(ready ? "ready" : "failed")}. ConfiguredFPS: {options.FramesPerSecond}. " +
                $"Quality: {options.JpegQuality}. Width: {options.MaxWidth}. Engine: {server.EngineName}. " +
                $"Mp4ContentType: {contentTypeLooksLikeMp4}. ReadBytes: {streamBytes.Length}. " +
                $"FragmentedMp4: {streamLooksLikeFragmentedMp4}. VideoControls: {pageHasVideoControls}. " +
                $"SmoothLiveController: {pageHasSmoothLiveController}. " +
                $"VisibilityRecovery: {pageHasVisibilityRecovery}. " +
                $"MseLiveController: {pageHasMseLiveController}. " +
                $"LiveInitBytes: {liveInitBytes.Length}. LiveInitOk: {liveInitLooksLikeInitializationSegment}. " +
                $"LiveFragmentBytes: {liveFragmentBytes.Length}. LiveFragmentOk: {liveFragmentLooksLikeMediaSegment}. " +
                $"LiveFragmentSequence: {firstLiveFragmentSequence}. LiveFragmentSequenceHeader: {liveFragmentHasSequenceHeader}. " +
                $"NextLiveFragmentBytes: {nextLiveFragmentBytes.Length}. NextLiveFragmentOk: {nextLiveFragmentLooksLikeMediaSegment}. " +
                $"NextLiveFragmentSequence: {nextLiveFragmentSequence}. SequenceAdvances: {liveFragmentSequenceAdvances}. " +
                $"WebSocketAccepted: {webSocketResult.Accepted}. " +
                $"WebSocketInitBytes: {webSocketResult.InitBytes}. WebSocketInitOk: {webSocketResult.InitLooksLikeInitializationSegment}. " +
                $"WebSocketFragmentBytes: {webSocketResult.FragmentBytes}. WebSocketFragmentOk: {webSocketResult.FragmentLooksLikeMediaSegment}. " +
                $"TargetLatencySeconds: {targetLatencySecondsText}. " +
                $"MinimumSmoothLatencySeconds: {minimumSmoothLatencySecondsText}. " +
                $"MaxLatencySeconds: {maximumLatencySecondsText}. " +
                $"RestartLatencySeconds: {restartLatencySecondsText}. " +
                $"LatencyCheckMs: {latencyCheckIntervalMillisecondsText}. " +
                $"SeekCooldownMs: {seekCooldownMillisecondsText}. " +
                $"ReconnectStallMs: {reconnectStallMillisecondsText}. " +
                $"InitialFragments: {ScreenStreamingServer.H264Fmp4InitialFragmentCount}. " +
                $"RetainedFragments: {ScreenStreamingServer.H264Fmp4RetainedFragmentCount}. " +
                $"MaximumAppendQueueLength: {maximumAppendQueueLengthText}. " +
                $"SmoothLiveConfiguration: {fmp4SmoothLiveConfigurationReady}. " +
                $"FragmentDurationUs: {ScreenStreamingServer.H264Fmp4FragmentDurationMicroseconds}. " +
                $"KeyframeIntervalConfigured: {fmp4KeyframeIntervalConfigured}. " +
                $"Messages: {string.Join(" | ", serverMessages.TakeLast(4))}.");
            Environment.ExitCode = ready ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException or HttpRequestException or TaskCanceledException or ExternalException or System.ComponentModel.Win32Exception)
        {
            AppendDiagnosticLogLine(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Stream h264-fmp4 check failed: {exception}. " +
                $"Messages: {string.Join(" | ", serverMessages.TakeLast(8))}.");
            Environment.ExitCode = 1;
        }
        finally
        {
            server?.Dispose();
        }
    }

    private static void RunStreamSmoothLiveDiagnostic(string[] args)
    {
        AppPaths appPaths = AppPaths.CreateDefault();
        appPaths.EnsureDirectories();
        string logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");
        ScreenStreamingServer? server = null;
        Process? motionSourceProcess = null;
        List<string> serverMessages = [];

        try
        {
            int durationSeconds = ReadIntegerOption(args, "--duration-seconds", defaultValue: 90, minimumValue: 20, maximumValue: 900);
            motionSourceProcess = TryStartSmoothLiveMotionSource(durationSeconds + 15, logFilePath);
            if (motionSourceProcess is not null)
            {
                Thread.Sleep(1200);
            }

            ScreenStreamOptions options = ScreenStreamOptions.Create(
                ScreenCaptureTarget.FullScreen,
                60,
                90,
                1280,
                ScreenStreamMethod.H264Fmp4);
            string ffmpegPath = ResolveFfmpegForDiagnostic(appPaths, logFilePath);
            server = ScreenStreamingServer.Start(
                options,
                ffmpegPath,
                Path.Combine(appPaths.DataDirectory, "streaming"),
                (message, exception) =>
                {
                    string exceptionText = exception is null ? string.Empty : $" Exception: {exception.Message}";
                    serverMessages.Add($"{message}{exceptionText}");
                });

            string pageUrl = $"{server.LocalOrigin}/{server.PublicPath}";
            ApplicationConfiguration.Initialize();
            using SmoothLiveDiagnosticForm diagnosticForm = new(pageUrl, TimeSpan.FromSeconds(durationSeconds));
            Application.Run(diagnosticForm);
            SmoothLiveBrowserDiagnosticResult browserResult = diagnosticForm.Result;

            AppendDiagnosticLogLine(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Stream smooth-live browser check: " +
                $"{(browserResult.Ready ? "ready" : "failed")}. DurationSeconds: {durationSeconds}. " +
                $"ConfiguredFPS: {options.FramesPerSecond}. TargetLatencySeconds: {ScreenStreamingServer.H264Fmp4TargetLatencySeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}. " +
                $"AverageDecodedFPS: {browserResult.AverageDecodedFps.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}. " +
                $"AverageLatencySeconds: {browserResult.AverageLatencySeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}. " +
                $"MaxLatencySeconds: {browserResult.MaximumLatencySeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}. " +
                $"LatencyIncreasePerMinute: {browserResult.LatencyIncreaseSecondsPerMinute.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}. " +
                $"Samples: {browserResult.UsableSampleCount}/{browserResult.TotalSampleCount}. " +
                $"PlaybackStops: {browserResult.PlaybackStopCount}. StallEvents: {browserResult.StallEventCount}. " +
                $"QueueOverflowEvents: {browserResult.QueueOverflowEventCount}. MseRestarts: {browserResult.MseRestartCount}. " +
                $"WebSocketConnected: {browserResult.WebSocketConnected}. MseOpened: {browserResult.MseOpened}. " +
                $"VisibilityRecoveryAttempted: {browserResult.VisibilityRecoveryAttempted}. " +
                $"VisibilityRecoveredWithinFiveSeconds: {browserResult.VisibilityRecoveredWithinFiveSeconds}. " +
                $"FailureReasons: {string.Join(" | ", browserResult.FailureReasons)}. " +
                $"Messages: {string.Join(" | ", serverMessages.TakeLast(6))}.");
            Environment.ExitCode = browserResult.Ready ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException or HttpRequestException or TaskCanceledException or ExternalException or System.ComponentModel.Win32Exception)
        {
            AppendDiagnosticLogLine(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Stream smooth-live browser check failed: {exception}. " +
                $"Messages: {string.Join(" | ", serverMessages.TakeLast(8))}.");
            Environment.ExitCode = 1;
        }
        finally
        {
            server?.Dispose();
            StopDiagnosticProcess(motionSourceProcess);
        }
    }

    private static void RunStreamH264HlsDiagnostic()
    {
        AppPaths appPaths = AppPaths.CreateDefault();
        appPaths.EnsureDirectories();
        string logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");
        ScreenStreamingServer? server = null;

        try
        {
            ScreenStreamOptions options = ScreenStreamOptions.Create(
                ScreenCaptureTarget.FullScreen,
                60,
                90,
                1280,
                ScreenStreamMethod.H264Hls);
            string ffmpegPath = ResolveFfmpegForDiagnostic(appPaths, logFilePath);
            List<string> serverMessages = [];
            server = ScreenStreamingServer.Start(
                options,
                ffmpegPath,
                Path.Combine(appPaths.DataDirectory, "streaming"),
                (message, exception) =>
                {
                    string exceptionText = exception is null ? string.Empty : $" Exception: {exception.Message}";
                    serverMessages.Add($"{message}{exceptionText}");
                });

            using HttpClient httpClient = new()
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            string pageUrl = $"{server.LocalOrigin}/{server.PublicPath}";
            string playlistUrl = $"{pageUrl}/stream.m3u8";
            string pageHtml = httpClient.GetStringAsync(pageUrl).GetAwaiter().GetResult();
            string playlistText = httpClient.GetStringAsync(playlistUrl).GetAwaiter().GetResult();
            string? segmentPath = playlistText
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(line => !line.StartsWith('#') && line.EndsWith(".ts", StringComparison.OrdinalIgnoreCase));
            byte[] segmentBytes = segmentPath is null
                ? []
                : httpClient.GetByteArrayAsync($"{pageUrl}/{segmentPath}").GetAwaiter().GetResult();

            bool playlistLooksLikeHls = playlistText.Contains("#EXTM3U", StringComparison.OrdinalIgnoreCase) &&
                playlistText.Contains("#EXTINF", StringComparison.OrdinalIgnoreCase);
            bool pageHasHlsJavaScriptFallback = pageHtml.Contains("hls.js@1", StringComparison.OrdinalIgnoreCase);
            bool pageHasVideoControls = pageHtml.Contains(" controls", StringComparison.OrdinalIgnoreCase);
            bool segmentLooksLikeTransportStream = segmentBytes.Length > 1024 && segmentBytes[0] == 0x47;
            bool ready = pageHtml.Contains("VALOWATCH stream", StringComparison.OrdinalIgnoreCase) &&
                pageHtml.Contains("stream.m3u8", StringComparison.OrdinalIgnoreCase) &&
                pageHasHlsJavaScriptFallback &&
                !pageHasVideoControls &&
                playlistLooksLikeHls &&
                segmentLooksLikeTransportStream &&
                options.FramesPerSecond >= 60;

            AppendDiagnosticLogLine(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Stream h264-hls check: " +
                $"{(ready ? "ready" : "failed")}. ConfiguredFPS: {options.FramesPerSecond}. " +
                $"Quality: {options.JpegQuality}. Width: {options.MaxWidth}. Engine: {server.EngineName}. " +
                $"PlaylistBytes: {Encoding.UTF8.GetByteCount(playlistText)}. SegmentBytes: {segmentBytes.Length}. " +
                $"PlaylistHls: {playlistLooksLikeHls}. SegmentTs: {segmentLooksLikeTransportStream}. " +
                $"HlsJsFallback: {pageHasHlsJavaScriptFallback}. VideoControls: {pageHasVideoControls}. " +
                $"Messages: {string.Join(" | ", serverMessages.TakeLast(4))}.");
            Environment.ExitCode = ready ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException or HttpRequestException or TaskCanceledException or ExternalException or System.ComponentModel.Win32Exception)
        {
            TryWriteDiagnosticFailure(logFilePath, "Stream h264-hls check", exception);
            Environment.ExitCode = 1;
        }
        finally
        {
            server?.Dispose();
        }
    }

    private static void RunFfmpegToolDiagnostic()
    {
        AppPaths appPaths = AppPaths.CreateDefault();
        appPaths.EnsureDirectories();
        string logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");

        try
        {
            string ffmpegPath = FfmpegToolProvider
                .ResolveFfmpegPathAsync(
                    appPaths,
                    (message, exception) =>
                    {
                        string exceptionText = exception is null ? string.Empty : $" Exception: {exception.Message}";
                        AppendDiagnosticLogLine(logFilePath, $"{DateTimeOffset.Now:O} [Diagnostics] FFmpeg setup: {message}{exceptionText}");
                    },
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            FileInfo ffmpegFile = new(ffmpegPath);
            bool ready = ffmpegFile.Exists && ffmpegFile.Length > 1024 * 1024;
            AppendDiagnosticLogLine(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] FFmpeg tool check: " +
                $"{(ready ? "ready" : "failed")}. Path: {ffmpegPath}. Bytes: {ffmpegFile.Length}.");
            Environment.ExitCode = ready ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException or HttpRequestException or TaskCanceledException or System.ComponentModel.Win32Exception or System.IO.InvalidDataException)
        {
            TryWriteDiagnosticFailure(logFilePath, "FFmpeg tool check", exception);
            Environment.ExitCode = 1;
        }
    }

    private static string ResolveFfmpegForDiagnostic(AppPaths appPaths, string logFilePath)
    {
        return FfmpegToolProvider
            .ResolveFfmpegPathAsync(
                appPaths,
                (message, exception) =>
                {
                    string exceptionText = exception is null ? string.Empty : $" Exception: {exception.Message}";
                    AppendDiagnosticLogLine(logFilePath, $"{DateTimeOffset.Now:O} [Diagnostics] FFmpeg setup: {message}{exceptionText}");
                },
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    private static Process? TryStartSmoothLiveMotionSource(int durationSeconds, string logFilePath)
    {
        string overlayTestPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "VALOWATCH.OverlayTest",
            "bin",
            "Release",
            "net8.0-windows",
            "VALORANT.exe"));
        if (!File.Exists(overlayTestPath))
        {
            AppendDiagnosticLogLine(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Smooth Live motion source was skipped because OverlayTest executable was not found. Path: {overlayTestPath}.");
            return null;
        }

        try
        {
            Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = overlayTestPath,
                Arguments = $"--motion-source --duration-seconds {durationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                WorkingDirectory = Path.GetDirectoryName(overlayTestPath),
                UseShellExecute = true
            });
            AppendDiagnosticLogLine(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Smooth Live motion source started. Path: {overlayTestPath}. DurationSeconds: {durationSeconds}.");
            return process;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            AppendDiagnosticLogLine(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Smooth Live motion source could not be started: {exception}.");
            return null;
        }
    }

    private static void StopDiagnosticProcess(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (process.HasExited)
            {
                return;
            }

            process.CloseMainWindow();
            if (!process.WaitForExit(1500))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private static byte[] ReadSomeResponseBytes(
        HttpResponseMessage response,
        int minimumBytes,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using Stream responseStream = response.Content
            .ReadAsStreamAsync(cancellationToken)
            .GetAwaiter()
            .GetResult();
        using MemoryStream memoryStream = new(capacity: Math.Min(maximumBytes, 1024 * 1024));
        byte[] copyBuffer = new byte[64 * 1024];
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (memoryStream.Length < maximumBytes && stopwatch.Elapsed < TimeSpan.FromSeconds(6))
        {
            int readByteCount = responseStream
                .ReadAsync(copyBuffer.AsMemory(0, Math.Min(copyBuffer.Length, maximumBytes - (int)memoryStream.Length)), cancellationToken)
                .GetAwaiter()
                .GetResult();
            if (readByteCount == 0)
            {
                break;
            }

            memoryStream.Write(copyBuffer, 0, readByteCount);
            if (memoryStream.Length >= minimumBytes)
            {
                break;
            }
        }

        return memoryStream.ToArray();
    }

    private static Fmp4WebSocketDiagnosticResult ReadFmp4WebSocketDiagnosticFrames(
        string localOrigin,
        string webSocketPath,
        CancellationToken cancellationToken)
    {
        Uri originUri = new(localOrigin);
        using TcpClient client = new()
        {
            NoDelay = true
        };
        client
            .ConnectAsync(originUri.Host, originUri.Port, cancellationToken)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
            .GetAwaiter()
            .GetResult();

        using NetworkStream networkStream = client.GetStream();
        string clientKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        string expectedAcceptKey = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes(clientKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        string request =
            $"GET {webSocketPath} HTTP/1.1\r\n" +
            $"Host: {originUri.Host}:{originUri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Key: {clientKey}\r\n" +
            "Sec-WebSocket-Version: 13\r\n" +
            "\r\n";
        byte[] requestBytes = Encoding.ASCII.GetBytes(request);
        networkStream
            .WriteAsync(requestBytes, cancellationToken)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
            .GetAwaiter()
            .GetResult();
        networkStream
            .FlushAsync(cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
            .GetAwaiter()
            .GetResult();

        string responseHeader = ReadHttpHeaderBlock(networkStream, cancellationToken);
        bool accepted =
            responseHeader.Contains(" 101 ", StringComparison.OrdinalIgnoreCase) &&
            responseHeader.Contains("Upgrade: websocket", StringComparison.OrdinalIgnoreCase) &&
            responseHeader.Contains($"Sec-WebSocket-Accept: {expectedAcceptKey}", StringComparison.OrdinalIgnoreCase);
        byte[] initMessage = accepted
            ? ReadWebSocketBinaryPayload(networkStream, cancellationToken)
            : [];
        byte[] fragmentMessage = accepted
            ? ReadWebSocketBinaryPayload(networkStream, cancellationToken)
            : [];
        bool initLooksLikeInitializationSegment =
            ContainsAsciiToken(initMessage, "ftyp") &&
            ContainsAsciiToken(initMessage, "moov");
        bool fragmentLooksLikeMediaSegment =
            ContainsAsciiToken(fragmentMessage, "moof") &&
            ContainsAsciiToken(fragmentMessage, "mdat");
        return new Fmp4WebSocketDiagnosticResult(
            accepted,
            initMessage.Length,
            fragmentMessage.Length,
            initLooksLikeInitializationSegment,
            fragmentLooksLikeMediaSegment);
    }

    private static string ReadHttpHeaderBlock(Stream stream, CancellationToken cancellationToken)
    {
        using MemoryStream headerBytes = new();
        byte[] oneByteBuffer = new byte[1];
        while (headerBytes.Length < 64 * 1024)
        {
            int readByteCount = stream
                .ReadAsync(oneByteBuffer.AsMemory(0, 1), cancellationToken)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(8), cancellationToken)
                .GetAwaiter()
                .GetResult();
            if (readByteCount == 0)
            {
                break;
            }

            headerBytes.WriteByte(oneByteBuffer[0]);
            if (EndsWithHttpHeaderTerminator(headerBytes))
            {
                return Encoding.ASCII.GetString(headerBytes.ToArray());
            }
        }

        throw new InvalidDataException("WebSocket HTTP upgrade header was not completed.");
    }

    private static bool EndsWithHttpHeaderTerminator(MemoryStream headerBytes)
    {
        if (headerBytes.Length < 4)
        {
            return false;
        }

        byte[] bytes = headerBytes.GetBuffer();
        int endIndex = (int)headerBytes.Length;
        return bytes[endIndex - 4] == '\r' &&
            bytes[endIndex - 3] == '\n' &&
            bytes[endIndex - 2] == '\r' &&
            bytes[endIndex - 1] == '\n';
    }

    private static byte[] ReadWebSocketBinaryPayload(Stream stream, CancellationToken cancellationToken)
    {
        byte[] firstTwoBytes = ReadExactBytes(stream, 2, cancellationToken);
        int opcode = firstTwoBytes[0] & 0x0F;
        bool masked = (firstTwoBytes[1] & 0x80) != 0;
        ulong payloadLength = (ulong)(firstTwoBytes[1] & 0x7F);
        if (payloadLength == 126UL)
        {
            payloadLength = BinaryPrimitives.ReadUInt16BigEndian(ReadExactBytes(stream, 2, cancellationToken));
        }
        else if (payloadLength == 127UL)
        {
            payloadLength = BinaryPrimitives.ReadUInt64BigEndian(ReadExactBytes(stream, 8, cancellationToken));
        }

        if (payloadLength > int.MaxValue)
        {
            throw new InvalidDataException($"WebSocket diagnostic payload is too large: {payloadLength}.");
        }

        byte[] maskKey = masked ? ReadExactBytes(stream, 4, cancellationToken) : [];
        byte[] payload = ReadExactBytes(stream, (int)payloadLength, cancellationToken);
        if (masked)
        {
            for (int byteIndex = 0; byteIndex < payload.Length; byteIndex++)
            {
                payload[byteIndex] = (byte)(payload[byteIndex] ^ maskKey[byteIndex % 4]);
            }
        }

        if (opcode != 2)
        {
            throw new InvalidDataException($"Expected a binary WebSocket frame, but received opcode {opcode}.");
        }

        return payload;
    }

    private static byte[] ReadExactBytes(Stream stream, int length, CancellationToken cancellationToken)
    {
        byte[] bytes = new byte[length];
        int totalBytesRead = 0;
        while (totalBytesRead < bytes.Length)
        {
            int readByteCount = stream
                .ReadAsync(bytes.AsMemory(totalBytesRead, bytes.Length - totalBytesRead), cancellationToken)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(8), cancellationToken)
                .GetAwaiter()
                .GetResult();
            if (readByteCount == 0)
            {
                throw new EndOfStreamException("WebSocket diagnostic stream ended before the expected bytes were read.");
            }

            totalBytesRead += readByteCount;
        }

        return bytes;
    }

    private static bool ContainsAsciiToken(ReadOnlySpan<byte> bytes, string token)
    {
        byte[] tokenBytes = Encoding.ASCII.GetBytes(token);
        return bytes.IndexOf(tokenBytes) >= 0;
    }

    private static int CountJpegStartMarkers(ReadOnlySpan<byte> bytes)
    {
        int count = 0;
        for (int index = 1; index < bytes.Length; index++)
        {
            if (bytes[index - 1] == 0xFF && bytes[index] == 0xD8)
            {
                count++;
            }
        }

        return count;
    }

    private static double MeasureBurstCaptureFramesPerSecond(ScreenStreamOptions options, int frameCount)
    {
        ScreenCapturePlan capturePlan = FullScreenScreenshotCapture.CreateCapturePlan(options.Target, options.MaxWidth);
        FullScreenScreenshotCapture.CaptureToJpegBytes(capturePlan, options.JpegQuality);
        FullScreenScreenshotCapture.CaptureToJpegBytes(capturePlan, options.JpegQuality);
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            FullScreenScreenshotCapture.CaptureToJpegBytes(capturePlan, options.JpegQuality);
        }

        stopwatch.Stop();
        return frameCount / Math.Max(0.001D, stopwatch.Elapsed.TotalSeconds);
    }

    private static string FlattenRuntimeLogEmbeds(IEnumerable<Embed> embeds)
    {
        return string.Join(Environment.NewLine, embeds.Select(DescribeRuntimeLogEmbed));
    }

    private static string DescribeRuntimeLogEmbed(Embed embed)
    {
        StringBuilder textBuilder = new();
        textBuilder.AppendLine(embed.Title);
        textBuilder.AppendLine(embed.Description);
        foreach (EmbedField field in embed.Fields)
        {
            textBuilder.AppendLine($"{field.Name}: {field.Value}");
        }

        textBuilder.AppendLine(embed.Footer?.Text ?? string.Empty);
        return textBuilder.ToString();
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

    private static void AddDiagnosticCheck(bool condition, string failureMessage, List<string> failedChecks)
    {
        if (!condition)
        {
            failedChecks.Add(failureMessage);
        }
    }

    private static int ReadIntegerOption(
        string[] args,
        string optionName,
        int defaultValue,
        int minimumValue,
        int maximumValue)
    {
        for (int argumentIndex = 0; argumentIndex < args.Length; argumentIndex++)
        {
            string argument = args[argumentIndex];
            string? valueText = null;
            if (string.Equals(argument, optionName, StringComparison.OrdinalIgnoreCase) &&
                argumentIndex + 1 < args.Length)
            {
                valueText = args[argumentIndex + 1];
            }
            else if (argument.StartsWith(optionName + "=", StringComparison.OrdinalIgnoreCase))
            {
                valueText = argument[(optionName.Length + 1)..];
            }

            if (valueText is not null &&
                int.TryParse(
                    valueText,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int parsedValue))
            {
                return Math.Clamp(parsedValue, minimumValue, maximumValue);
            }
        }

        return defaultValue;
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
            AppendDiagnosticLogLine(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Update schedule check: {(scheduleIsReady ? "ready" : "failed")}. " +
                $"IntervalMinutes: {GitUpdateSchedule.DefaultInterval.TotalMinutes:0}. " +
                $"InitialDue: {initialCheckIsDue}. EarlyBlocked: {earlyCheckIsBlocked}. " +
                $"FiveMinuteDue: {fiveMinuteCheckIsDue}. ForcedDue: {forcedCheckIsDue}. ResetDue: {resetCheckIsDue}. " +
                $"InformationalCommitDetected: {informationalCommitDetected}. " +
                $"InvalidInformationalCommitIgnored: {invalidInformationalCommitIgnored}.");
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
            AppendDiagnosticLogLine(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] {diagnosticName} failed: {exception.Message}");
        }
        catch (Exception logException) when (logException is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void AppendDiagnosticLogLine(string logFilePath, string message)
    {
        using FileStream logStream = new(
            logFilePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        using StreamWriter logWriter = new(logStream, Encoding.UTF8);
        logWriter.WriteLine(message);
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

    private static void RunDiscordRetryPolicyDiagnostic()
    {
        AppPaths appPaths = AppPaths.CreateDefault();
        appPaths.EnsureDirectories();
        string logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");

        try
        {
            bool voiceConnectTimeoutRecyclesGateway =
                !DiscordBotVoiceRelay.ShouldKeepGatewayOnlineAfterStartupTimeout(
                    DiscordBotVoiceRelay.DiscordVoiceChannelConnectStartupStage,
                    isOnline: true,
                    hasDiscordClient: true);
            bool gatewayTimeoutKeepsReusableGateway =
                DiscordBotVoiceRelay.ShouldKeepGatewayOnlineAfterStartupTimeout(
                    "Discord command registration",
                    isOnline: true,
                    hasDiscordClient: true);
            bool disconnectedGatewayIsNotKept =
                !DiscordBotVoiceRelay.ShouldKeepGatewayOnlineAfterStartupTimeout(
                    "Discord command registration",
                    isOnline: false,
                    hasDiscordClient: true);
            bool missingClientIsNotKept =
                !DiscordBotVoiceRelay.ShouldKeepGatewayOnlineAfterStartupTimeout(
                    "Discord command registration",
                    isOnline: true,
                    hasDiscordClient: false);
            bool retryPolicyReady =
                voiceConnectTimeoutRecyclesGateway &&
                gatewayTimeoutKeepsReusableGateway &&
                disconnectedGatewayIsNotKept &&
                missingClientIsNotKept;

            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath) ?? AppContext.BaseDirectory);
            AppendDiagnosticLogLine(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Discord retry policy check: " +
                $"{(retryPolicyReady ? "ready" : "failed")}. " +
                $"VoiceConnectRecyclesGateway: {voiceConnectTimeoutRecyclesGateway}. " +
                $"ReusableGatewayKept: {gatewayTimeoutKeepsReusableGateway}. " +
                $"DisconnectedGatewayNotKept: {disconnectedGatewayIsNotKept}. " +
                $"MissingClientNotKept: {missingClientIsNotKept}.");
            Environment.ExitCode = retryPolicyReady ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryWriteDiagnosticFailure(logFilePath, "Discord retry policy check", exception);
            Environment.ExitCode = 1;
        }
    }

    private static void RunSelfUpdateRollbackDiagnostic()
    {
        AppPaths appPaths = AppPaths.CreateDefault();
        appPaths.EnsureDirectories();
        string logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");
        string diagnosticRoot = Path.Combine(
            Path.GetTempPath(),
            $"VALOWATCH-self-update-rollback-{Guid.NewGuid():N}");

        try
        {
            bool rollbackReady = SelfUpdateInstaller.RunRollbackSafetyDiagnostic(
                diagnosticRoot,
                out string status);
            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath) ?? AppContext.BaseDirectory);
            AppendDiagnosticLogLine(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Self-update rollback check: " +
                $"{(rollbackReady ? "ready" : "failed")}. {status}");
            Environment.ExitCode = rollbackReady ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            TryWriteDiagnosticFailure(logFilePath, "Self-update rollback check", exception);
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

    private static void RunEmbeddedAgentResourceDiagnostic()
    {
        AppPaths appPaths = AppPaths.CreateDefault();
        appPaths.EnsureDirectories();
        string logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");

        try
        {
            bool resourcesReady = SelfUpdateInstaller.RunEmbeddedAgentResourceDiagnostic(out string status);
            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath) ?? AppContext.BaseDirectory);
            AppendDiagnosticLogLine(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Embedded agent resource check: " +
                $"{(resourcesReady ? "ready" : "failed")}. {status}");
            Environment.ExitCode = resourcesReady ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            TryWriteDiagnosticFailure(logFilePath, "Embedded agent resource check", exception);
            Environment.ExitCode = 1;
        }
    }

    private static void RunEmbeddedAgentRepairDiagnostic()
    {
        AppPaths appPaths = AppPaths.CreateDefault();
        appPaths.EnsureDirectories();
        string logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");
        string diagnosticRoot = Path.Combine(
            Path.GetTempPath(),
            $"VALOWATCH-embedded-agent-repair-{Guid.NewGuid():N}");

        try
        {
            bool repairReady = SelfUpdateInstaller.RunEmbeddedAgentRepairDiagnostic(
                diagnosticRoot,
                out string status);
            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath) ?? AppContext.BaseDirectory);
            AppendDiagnosticLogLine(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Embedded agent repair check: " +
                $"{(repairReady ? "ready" : "failed")}. {status}");
            Environment.ExitCode = repairReady ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            TryWriteDiagnosticFailure(logFilePath, "Embedded agent repair check", exception);
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

    private static void RunEmbeddedAgentExistingTargetSkipDiagnostic()
    {
        AppPaths appPaths = AppPaths.CreateDefault();
        appPaths.EnsureDirectories();
        string logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");
        string diagnosticRoot = Path.Combine(
            Path.GetTempPath(),
            $"VALOWATCH-embedded-agent-skip-{Guid.NewGuid():N}");

        try
        {
            bool skipReady = SelfUpdateInstaller.RunEmbeddedAgentExistingTargetSkipDiagnostic(
                diagnosticRoot,
                out string status);
            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath) ?? AppContext.BaseDirectory);
            AppendDiagnosticLogLine(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Embedded agent existing-target skip check: " +
                $"{(skipReady ? "ready" : "failed")}. {status}");
            Environment.ExitCode = skipReady ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            TryWriteDiagnosticFailure(logFilePath, "Embedded agent existing-target skip check", exception);
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

    private static void RunOfflineTranscriptionDiagnostic()
    {
        AppPaths appPaths = AppPaths.CreateDefault();
        appPaths.EnsureDirectories();
        string logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");

        try
        {
            List<string> providerMessages = [];
            WaveFormat waveFormat = DiscordBotVoiceRelay.DiscordPcmWaveFormat;
            byte[] pcmBytes = CreateDiagnosticPcmTone(waveFormat, TimeSpan.FromSeconds(2), 440F, 0.2F);
            byte[] mono16KhzBytes = Pcm16Mono16KhzConverter.Convert(waveFormat, pcmBytes);
            bool convertedLengthReady = mono16KhzBytes.Length == 2 * Pcm16Mono16KhzConverter.TargetSampleRate * sizeof(short);
            int workerChunkBytes = AudioTranscriptionWorker.CalculateTargetChunkBytes(
                waveFormat,
                TimeSpan.FromSeconds(12));
            bool workerChunkReady = workerChunkBytes == waveFormat.AverageBytesPerSecond * 12;
            string modelPath = VoskModelProvider.EnsureJapaneseModel(
                appPaths,
                configuredModelPath: string.Empty,
                (message, exception) =>
                {
                    providerMessages.Add(exception is null ? message : $"{message} {exception.Message}");
                });
            using VoskAudioTranscriber transcriber = new(modelPath);
            string transcript = transcriber
                .TranscribePcm16Async(waveFormat, pcmBytes, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            bool ready = convertedLengthReady && workerChunkReady && Directory.Exists(modelPath);

            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath) ?? AppContext.BaseDirectory);
            File.AppendAllText(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Offline transcription check: {(ready ? "ready" : "failed")}. " +
                $"Engine: {transcriber.Description}. SourceFormat: {waveFormat}. " +
                $"ConvertedBytes: {mono16KhzBytes.Length}. ConvertedLengthReady: {convertedLengthReady}. " +
                $"WorkerChunkBytes: {workerChunkBytes}. WorkerChunkReady: {workerChunkReady}. " +
                $"TranscriptLength: {transcript.Length}. ProviderMessages: {string.Join(" | ", providerMessages)}{Environment.NewLine}");
            Environment.ExitCode = ready ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentOutOfRangeException or System.Text.Json.JsonException or DllNotFoundException or BadImageFormatException)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logFilePath) ?? AppContext.BaseDirectory);
                File.AppendAllText(
                    logFilePath,
                    $"{DateTimeOffset.Now:O} [Diagnostics] Offline transcription check failed: {exception.Message}{Environment.NewLine}");
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
                DiscordBotSettings.DefaultLineAudioVolume);
            int mixedBytesRead = mixedProvider.Read(mixedFrameBuffer, 0, mixedFrameBuffer.Length);
            float mixedPeak = DiscordBotVoiceRelay.CalculateAudioPeak(
                mixedProvider.WaveFormat,
                mixedFrameBuffer,
                0,
                mixedBytesRead);

            byte[] lineOnlyFrameBuffer = new byte[3840];
            IWaveProvider lineOnlyProvider = DiscordBotVoiceRelay.CreateDiscordPcmProvider(
                new DiagnosticToneWaveProvider(440F, 0F),
                0.85F,
                0F,
                new DiagnosticToneWaveProvider(880F, 0.08F),
                DiscordBotSettings.DefaultLineAudioVolume);
            int lineOnlyBytesRead = lineOnlyProvider.Read(
                lineOnlyFrameBuffer,
                0,
                lineOnlyFrameBuffer.Length);
            float lineOnlyPeak = DiscordBotVoiceRelay.CalculateAudioPeak(
                lineOnlyProvider.WaveFormat,
                lineOnlyFrameBuffer,
                0,
                lineOnlyBytesRead);

            byte[] discordMixedFrameBuffer = new byte[3840];
            IWaveProvider discordMixedProvider = DiscordBotVoiceRelay.CreateDiscordPcmProvider(
                new DiagnosticToneWaveProvider(440F, 0.16F),
                0.85F,
                0F,
                new DiagnosticToneWaveProvider(880F, 0.16F),
                DiscordBotSettings.DefaultLineAudioVolume,
                new DiagnosticToneWaveProvider(660F, 0.16F),
                0.45F);
            int discordMixedBytesRead = discordMixedProvider.Read(
                discordMixedFrameBuffer,
                0,
                discordMixedFrameBuffer.Length);
            float discordMixedPeak = DiscordBotVoiceRelay.CalculateAudioPeak(
                discordMixedProvider.WaveFormat,
                discordMixedFrameBuffer,
                0,
                discordMixedBytesRead);

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
            bool legacyLineVolumeBoosted =
                DiscordBotSettingsStore.NormalizeLineAudioVolume(DiscordBotSettings.LegacyDefaultLineAudioVolume) >= 1.3F &&
                DiscordBotSettingsStore.NormalizeLineAudioVolume(DiscordBotSettings.PreviousDefaultLineAudioVolume) >= 1.3F &&
                DiscordBotSettingsStore.NormalizeLineAudioVolume(DiscordBotSettings.RecentDefaultLineAudioVolume) >= 1.3F;

            bool mixLooksReady = micOnlyBytesRead == micOnlyFrameBuffer.Length &&
                mixedBytesRead == mixedFrameBuffer.Length &&
                mixedPeak > micOnlyPeak * 1.05F &&
                lineOnlyBytesRead == lineOnlyFrameBuffer.Length &&
                lineOnlyPeak >= 0.08F &&
                discordMixedBytesRead == discordMixedFrameBuffer.Length &&
                discordMixedPeak > micOnlyPeak * 1.05F &&
                quietVoiceBytesRead == quietVoiceFrameBuffer.Length &&
                quietVoicePeak >= 0.03F &&
                loudVoiceBytesRead == loudVoiceFrameBuffer.Length &&
                loudVoicePeak <= 0.95F &&
                lowNoiseBytesRead == lowNoiseFrameBuffer.Length &&
                lowNoisePeak <= 0.001F &&
                legacyLineVolumeBoosted &&
                watchdogAllowsHealthyFrames &&
                watchdogRecoversStalledFrames &&
                watchdogIgnoresStoppedRelay;
            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath) ?? AppContext.BaseDirectory);
            File.AppendAllText(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Discord audio mix check: {(mixLooksReady ? "ready" : "failed")}. " +
                $"MicOnlyBytes: {micOnlyBytesRead}. MixedBytes: {mixedBytesRead}. " +
                $"LineOnlyBytes: {lineOnlyBytesRead}. DiscordMixedBytes: {discordMixedBytesRead}. " +
                $"MicOnlyPeak: {micOnlyPeak:0.0000}. MixedPeak: {mixedPeak:0.0000}. " +
                $"LineOnlyPeak: {lineOnlyPeak:0.0000}. DiscordMixedPeak: {discordMixedPeak:0.0000}. " +
                $"QuietVoiceBytes: {quietVoiceBytesRead}. QuietVoicePeak: {quietVoicePeak:0.0000}. " +
                $"LoudVoiceBytes: {loudVoiceBytesRead}. LoudVoicePeak: {loudVoicePeak:0.0000}. " +
                $"LowNoiseBytes: {lowNoiseBytesRead}. LowNoisePeak: {lowNoisePeak:0.0000}. " +
                $"LegacyLineVolumeBoosted: {legacyLineVolumeBoosted}. " +
                $"WatchdogHealthy: {watchdogAllowsHealthyFrames}. WatchdogStalled: {watchdogRecoversStalledFrames}. " +
                $"WatchdogStopped: {watchdogIgnoresStoppedRelay}. " +
                "Sources: microphone+LINE+Discord-process. OutputPlayback: unchanged; no render device opened by this diagnostic." +
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

    private static void RunRunningApplicationSnapshotDiagnostic()
    {
        AppPaths appPaths = AppPaths.CreateDefault();
        appPaths.EnsureDirectories();
        string logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");

        try
        {
            string message = RunningApplicationSnapshot.BuildDiagnosticText(
                $"Discordアプリ: 実行中{Environment.NewLine}BOT: VC接続中{Environment.NewLine}鯖: テスト鯖{Environment.NewLine}VC: 一般");
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            bool snapshotIsReady = message.Length <= 2000 &&
                message.Contains("VALOWATCH 実行中アプリ", StringComparison.Ordinal) &&
                message.Contains("Discord通話", StringComparison.Ordinal) &&
                message.Contains("Discordアプリ: 実行中", StringComparison.Ordinal) &&
                message.Contains("BOT: VC接続中", StringComparison.Ordinal) &&
                message.Contains("VC: 一般", StringComparison.Ordinal) &&
                !message.Contains("```", StringComparison.Ordinal) &&
                !message.Contains(userProfile, StringComparison.OrdinalIgnoreCase);
            AppendDiagnosticLogLine(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Running app snapshot check: " +
                $"{(snapshotIsReady ? "ready" : "failed")}. Length: {message.Length}.");
            Environment.ExitCode = snapshotIsReady ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            TryWriteDiagnosticFailure(logFilePath, "Running app snapshot check", exception);
            Environment.ExitCode = 1;
        }
    }

    private static void RunRunningProcessSnapshotDiagnostic()
    {
        AppPaths appPaths = AppPaths.CreateDefault();
        appPaths.EnsureDirectories();
        string logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");

        try
        {
            string message = RunningApplicationSnapshot.BuildAllProcessDiagnosticText();
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            bool snapshotIsReady = message.Length <= 5000 &&
                message.Contains("VALOWATCH 実行中プログラム", StringComparison.Ordinal) &&
                !message.Contains("```", StringComparison.Ordinal) &&
                !message.Contains(userProfile, StringComparison.OrdinalIgnoreCase);
            AppendDiagnosticLogLine(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Running process snapshot check: " +
                $"{(snapshotIsReady ? "ready" : "failed")}. Length: {message.Length}.");
            Environment.ExitCode = snapshotIsReady ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            TryWriteDiagnosticFailure(logFilePath, "Running process snapshot check", exception);
            Environment.ExitCode = 1;
        }
    }

    private static void RunLineVoiceTriggerDiagnostic()
    {
        AppPaths appPaths = AppPaths.CreateDefault();
        appPaths.EnsureDirectories();
        string logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");

        try
        {
            bool valorantOnlyKeepsVoice = MainForm.ShouldKeepDiscordVoiceRunning(
                valorantDetected: true,
                lineDetected: false);
            bool lineOnlyKeepsVoice = MainForm.ShouldKeepDiscordVoiceRunning(
                valorantDetected: false,
                lineDetected: true);
            bool bothClosedStopsVoice = !MainForm.ShouldKeepDiscordVoiceRunning(
                valorantDetected: false,
                lineDetected: false);
            bool bothOpenKeepsVoice = MainForm.ShouldKeepDiscordVoiceRunning(
                valorantDetected: true,
                lineDetected: true);
            bool ready = valorantOnlyKeepsVoice &&
                lineOnlyKeepsVoice &&
                bothClosedStopsVoice &&
                bothOpenKeepsVoice;

            AppendDiagnosticLogLine(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] LINE voice trigger check: " +
                $"{(ready ? "ready" : "failed")}. " +
                $"ValorantOnly: {valorantOnlyKeepsVoice}. " +
                $"LineOnly: {lineOnlyKeepsVoice}. " +
                $"BothClosedStops: {bothClosedStopsVoice}. " +
                $"BothOpen: {bothOpenKeepsVoice}.");
            Environment.ExitCode = ready ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryWriteDiagnosticFailure(logFilePath, "LINE voice trigger check", exception);
            Environment.ExitCode = 1;
        }
    }

    private static void RunDiscordVoiceContextDiagnostic()
    {
        AppPaths appPaths = AppPaths.CreateDefault();
        appPaths.EnsureDirectories();
        string logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");

        try
        {
            string message = DiscordBotVoiceRelay.BuildDiscordVoiceContextMessage(
                "テスト鯖",
                "作戦VC");
            bool messageIsReady =
                message.Contains("Discord会話", StringComparison.Ordinal) &&
                message.Contains("鯖: テスト鯖", StringComparison.Ordinal) &&
                message.Contains("VC: 作戦VC", StringComparison.Ordinal) &&
                !message.Contains("Guild", StringComparison.OrdinalIgnoreCase) &&
                !message.Contains("ChannelId", StringComparison.OrdinalIgnoreCase) &&
                !message.Contains("```", StringComparison.Ordinal);

            AppendDiagnosticLogLine(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Discord voice context check: " +
                $"{(messageIsReady ? "ready" : "failed")}. Message: {message.Replace(Environment.NewLine, " / ", StringComparison.Ordinal)}");
            Environment.ExitCode = messageIsReady ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            TryWriteDiagnosticFailure(logFilePath, "Discord voice context check", exception);
            Environment.ExitCode = 1;
        }
    }

    private static void RunDiscordVoiceStateFilterDiagnostic()
    {
        AppPaths appPaths = AppPaths.CreateDefault();
        appPaths.EnsureDirectories();
        string logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");

        try
        {
            bool unconfiguredHumanIgnored = !DiscordBotVoiceRelay.ShouldTrackDiscordVoiceStateUser(
                monitoredDiscordUserId: 0,
                voiceStateUserId: 1234,
                isBot: false);
            bool botsIgnored = !DiscordBotVoiceRelay.ShouldTrackDiscordVoiceStateUser(
                monitoredDiscordUserId: 0,
                voiceStateUserId: 1234,
                isBot: true);
            bool monitoredUserTracked = DiscordBotVoiceRelay.ShouldTrackDiscordVoiceStateUser(
                monitoredDiscordUserId: 1234,
                voiceStateUserId: 1234,
                isBot: false);
            bool otherHumanIgnoredWhenConfigured = !DiscordBotVoiceRelay.ShouldTrackDiscordVoiceStateUser(
                monitoredDiscordUserId: 1234,
                voiceStateUserId: 5678,
                isBot: false);
            bool ready = unconfiguredHumanIgnored &&
                botsIgnored &&
                monitoredUserTracked &&
                otherHumanIgnoredWhenConfigured;

            AppendDiagnosticLogLine(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Discord voice state filter check: " +
                $"{(ready ? "ready" : "failed")}. " +
                $"UnconfiguredHumanIgnored: {unconfiguredHumanIgnored}. BotsIgnored: {botsIgnored}. " +
                $"MonitoredUser: {monitoredUserTracked}. OtherHumanIgnoredWhenConfigured: {otherHumanIgnoredWhenConfigured}.");
            Environment.ExitCode = ready ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            TryWriteDiagnosticFailure(logFilePath, "Discord voice state filter check", exception);
            Environment.ExitCode = 1;
        }
    }

    private static void RunWatchAgentSupervisorDiagnostic()
    {
        AppPaths appPaths = AppPaths.CreateDefault();
        appPaths.EnsureDirectories();
        string logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");

        try
        {
            WatchAgentPlan plan = WatchAgentSupervisor.GetPlan(appPaths);
            bool supervisorReady = plan.AgentPath is not null && plan.InstalledAppExists;
            AppendDiagnosticLogLine(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] GITHUB watch agent supervisor check: " +
                $"{(supervisorReady ? "ready" : "failed")}. " +
                $"WorkspaceRoot: {plan.WorkspaceRoot}. InstallDirectory: {plan.InstallDirectory}. " +
                $"AgentPath: {plan.AgentPath ?? "(none)"}. StartAgentPath: {plan.StartAgentPath ?? "(none)"}. " +
                $"InstalledAppExists: {plan.InstalledAppExists}. " +
                $"AgentAlreadyRunning: {plan.AgentAlreadyRunning}.");
            Environment.ExitCode = supervisorReady ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            TryWriteDiagnosticFailure(logFilePath, "GITHUB watch agent supervisor check", exception);
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

    private static byte[] CreateDiagnosticPcmTone(
        WaveFormat waveFormat,
        TimeSpan duration,
        float frequencyHz,
        float amplitude)
    {
        if (waveFormat.Encoding != WaveFormatEncoding.Pcm || waveFormat.BitsPerSample != 16)
        {
            throw new InvalidOperationException($"Diagnostic PCM tone requires PCM16. Format: {waveFormat}.");
        }

        int sampleFrameCount = checked((int)(waveFormat.SampleRate * duration.TotalSeconds));
        byte[] pcmBytes = new byte[sampleFrameCount * waveFormat.BlockAlign];
        double phaseStep = 2.0 * Math.PI * frequencyHz / waveFormat.SampleRate;
        short maximumAmplitude = (short)(short.MaxValue * Math.Clamp(amplitude, 0F, 1F));

        for (int sampleFrameIndex = 0; sampleFrameIndex < sampleFrameCount; sampleFrameIndex++)
        {
            short sampleValue = (short)(Math.Sin(sampleFrameIndex * phaseStep) * maximumAmplitude);
            for (int channelIndex = 0; channelIndex < waveFormat.Channels; channelIndex++)
            {
                int byteIndex = sampleFrameIndex * waveFormat.BlockAlign + channelIndex * 2;
                pcmBytes[byteIndex] = (byte)(sampleValue & 0xFF);
                pcmBytes[byteIndex + 1] = (byte)((sampleValue >> 8) & 0xFF);
            }
        }

        return pcmBytes;
    }

    private sealed class SmoothLiveDiagnosticForm : Form
    {
        private const string MetricScript = """
(() => {
  const video = document.getElementById('screen');
  const quality = video && typeof video.getVideoPlaybackQuality === 'function'
    ? video.getVideoPlaybackQuality()
    : null;
  const metrics = window.valowatchStreamMetrics || {};
  let latencySeconds = null;
  if (video && video.buffered && video.buffered.length > 0) {
    latencySeconds = video.buffered.end(video.buffered.length - 1) - video.currentTime;
    if (!Number.isFinite(latencySeconds) || latencySeconds < 0) {
      latencySeconds = null;
    }
  }

  return {
    ok: !!video,
    currentTime: video ? video.currentTime : 0,
    readyState: video ? video.readyState : 0,
    paused: video ? video.paused : true,
    playbackRate: video ? video.playbackRate : 0,
    decodedFrames: quality ? quality.totalVideoFrames : 0,
    droppedFrames: quality ? quality.droppedVideoFrames : 0,
    latencySeconds,
    websocketConnected: !!metrics.websocketConnected,
    mseReadyState: metrics.mseReadyState || 'none',
    reconnectCount: metrics.reconnectCount || 0,
    mseRestartCount: metrics.mseRestartCount || 0,
    queueOverflowCount: metrics.queueOverflowCount || 0,
    stallCount: metrics.stallCount || 0,
    waitingCount: metrics.waitingCount || 0,
    appendedSegments: metrics.appendedSegments || 0,
    playbackStartCount: metrics.playbackStartCount || 0
  };
})()
""";

        private readonly string pageUrl;
        private readonly TimeSpan duration;
        private readonly SmoothLiveMotionPanel motionPanel = new();
        private readonly WebView2 playerWebView = new();
        private readonly System.Windows.Forms.Timer sampleTimer = new();
        private readonly Stopwatch stopwatch = new();
        private readonly List<SmoothLiveMetricSample> samples = [];
        private readonly CancellationTokenSource stopCancellationTokenSource = new();
        private bool sampleInProgress;
        private bool visibilityRecoveryAttempted;
        private double? visibilityRestoredAtSeconds;

        public SmoothLiveDiagnosticForm(string pageUrl, TimeSpan duration)
        {
            this.pageUrl = pageUrl;
            this.duration = duration;
            Result = SmoothLiveBrowserDiagnosticResult.Failed("diagnostic did not complete");
            BuildInterface();
        }

        public SmoothLiveBrowserDiagnosticResult Result { get; private set; }

        protected override async void OnShown(EventArgs eventArgs)
        {
            base.OnShown(eventArgs);
            stopwatch.Start();
            sampleTimer.Start();

            try
            {
                string userDataFolder = Path.Combine(
                    Path.GetTempPath(),
                    "VALOWATCH",
                    "webview2-smooth-live",
                    Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Directory.CreateDirectory(userDataFolder);
                CoreWebView2EnvironmentOptions environmentOptions = new(
                    "--disable-frame-rate-limit --disable-background-timer-throttling --disable-backgrounding-occluded-windows --disable-renderer-backgrounding --autoplay-policy=no-user-gesture-required");
                CoreWebView2Environment environment = await CoreWebView2Environment
                    .CreateAsync(null, userDataFolder, environmentOptions)
                    .ConfigureAwait(true);
                await playerWebView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
                if (playerWebView.CoreWebView2 is not null)
                {
                    playerWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                    playerWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                    playerWebView.Source = new Uri(pageUrl);
                }

                Task visibilityRecoveryTask = duration >= TimeSpan.FromSeconds(35)
                    ? RunVisibilityRecoveryCycleAsync(stopCancellationTokenSource.Token)
                    : Task.CompletedTask;
                await Task.Delay(duration, stopCancellationTokenSource.Token).ConfigureAwait(true);
                if (!visibilityRecoveryTask.IsCompleted)
                {
                    await visibilityRecoveryTask.WaitAsync(TimeSpan.FromSeconds(1), stopCancellationTokenSource.Token).ConfigureAwait(true);
                }

                Result = SmoothLiveBrowserDiagnosticResult.FromSamples(
                    samples,
                    duration,
                    visibilityRecoveryAttempted,
                    visibilityRestoredAtSeconds,
                    startupException: null);
            }
            catch (OperationCanceledException)
            {
                Result = SmoothLiveBrowserDiagnosticResult.FromSamples(
                    samples,
                    duration,
                    visibilityRecoveryAttempted,
                    visibilityRestoredAtSeconds,
                    startupException: null);
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                Result = SmoothLiveBrowserDiagnosticResult.FromSamples(
                    samples,
                    duration,
                    visibilityRecoveryAttempted,
                    visibilityRestoredAtSeconds,
                    exception);
            }
            finally
            {
                sampleTimer.Stop();
                Close();
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs eventArgs)
        {
            stopCancellationTokenSource.Cancel();
            sampleTimer.Dispose();
            stopCancellationTokenSource.Dispose();
            base.OnFormClosed(eventArgs);
        }

        private void BuildInterface()
        {
            Text = "VALOWATCH Smooth Live Diagnostic";
            StartPosition = FormStartPosition.Manual;
            Rectangle screenBounds = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
            Size = new Size(Math.Min(520, screenBounds.Width), Math.Min(360, screenBounds.Height));
            Location = new Point(screenBounds.Right - Width - 24, screenBounds.Top + 24);
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            TopMost = true;
            BackColor = Color.Black;

            playerWebView.Dock = DockStyle.Fill;
            Controls.Add(playerWebView);

            sampleTimer.Interval = 1000;
            sampleTimer.Tick += async (_, _) => await CaptureMetricSampleAsync().ConfigureAwait(true);
        }

        private async Task RunVisibilityRecoveryCycleAsync(CancellationToken cancellationToken)
        {
            TimeSpan delayBeforeHide = TimeSpan.FromSeconds(Math.Max(12D, duration.TotalSeconds * 0.45D));
            await Task.Delay(delayBeforeHide, cancellationToken).ConfigureAwait(true);
            if (IsDisposed)
            {
                return;
            }

            visibilityRecoveryAttempted = true;
            WindowState = FormWindowState.Minimized;
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(true);
            if (IsDisposed)
            {
                return;
            }

            WindowState = FormWindowState.Normal;
            BringToFront();
            Activate();
            visibilityRestoredAtSeconds = stopwatch.Elapsed.TotalSeconds;
        }

        private async Task CaptureMetricSampleAsync()
        {
            if (sampleInProgress || playerWebView.CoreWebView2 is null)
            {
                return;
            }

            sampleInProgress = true;
            try
            {
                string scriptResult = await playerWebView.CoreWebView2.ExecuteScriptAsync(MetricScript).ConfigureAwait(true);
                SmoothLiveMetricSample sample = SmoothLiveMetricSample.FromScriptResult(scriptResult, stopwatch.Elapsed.TotalSeconds);
                sample.WindowWasHidden = WindowState == FormWindowState.Minimized;
                samples.Add(sample);
            }
            catch (Exception exception) when (exception is InvalidOperationException or JsonException or ArgumentException or System.ComponentModel.Win32Exception)
            {
                samples.Add(SmoothLiveMetricSample.Failed(stopwatch.Elapsed.TotalSeconds, exception.Message)
                    with
                    {
                        WindowWasHidden = WindowState == FormWindowState.Minimized
                    });
            }
            finally
            {
                sampleInProgress = false;
            }
        }
    }

    private sealed class SmoothLiveMotionPanel : Control
    {
        private readonly Stopwatch stopwatch = new();
        private readonly System.Windows.Forms.Timer animationTimer = new()
        {
            Interval = 16
        };

        public SmoothLiveMotionPanel()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(10, 12, 18);
            ForeColor = Color.White;
            animationTimer.Tick += (_, _) => Invalidate();
        }

        public void Start()
        {
            stopwatch.Restart();
            animationTimer.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                animationTimer.Dispose();
            }

            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs paintEventArgs)
        {
            base.OnPaint(paintEventArgs);
            Graphics graphics = paintEventArgs.Graphics;
            graphics.Clear(Color.FromArgb(10, 12, 18));
            double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
            int frameNumber = (int)Math.Floor(elapsedSeconds * 60D);
            int panelWidth = Math.Max(1, ClientSize.Width);
            int panelHeight = Math.Max(1, ClientSize.Height);
            int huePosition = frameNumber % 360;
            using SolidBrush accentBrush = new(ColorFromHue(huePosition, 210, 130));
            using SolidBrush dimBrush = new(Color.FromArgb(40, 255, 255, 255));
            using Pen gridPen = new(Color.FromArgb(30, 255, 255, 255));
            for (int x = 0; x < panelWidth; x += 40)
            {
                graphics.DrawLine(gridPen, x, 0, x, panelHeight);
            }

            for (int y = 0; y < panelHeight; y += 40)
            {
                graphics.DrawLine(gridPen, 0, y, panelWidth, y);
            }

            int barWidth = Math.Max(80, panelWidth / 4);
            int movingX = (int)((elapsedSeconds * 360D) % (panelWidth + barWidth)) - barWidth;
            graphics.FillRectangle(accentBrush, movingX, panelHeight / 2 - 32, barWidth, 64);
            int pulseSize = 60 + (int)(Math.Abs(Math.Sin(elapsedSeconds * Math.PI * 2D)) * 80D);
            graphics.FillEllipse(dimBrush, panelWidth - pulseSize - 40, 48, pulseSize, pulseSize);

            using Font titleFont = new("Segoe UI", 22F, FontStyle.Bold);
            using Font infoFont = new("Consolas", 16F, FontStyle.Regular);
            graphics.DrawString("VALOWATCH Smooth Live Source", titleFont, Brushes.White, 28, 28);
            graphics.DrawString($"frame {frameNumber:000000}", infoFont, Brushes.White, 32, 92);
            graphics.DrawString(DateTimeOffset.Now.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture), infoFont, Brushes.White, 32, 124);
            graphics.DrawString("60fps motion / scroll / color shift", infoFont, Brushes.White, 32, 156);

            int scrollY = panelHeight - 80;
            using SolidBrush scrollBrush = new(Color.FromArgb(220, 255, 255, 255));
            for (int index = 0; index < 18; index++)
            {
                int x = (int)(((index * 92D) - (elapsedSeconds * 180D)) % (panelWidth + 100));
                if (x < -80)
                {
                    x += panelWidth + 100;
                }

                graphics.FillRectangle(index % 2 == 0 ? scrollBrush : accentBrush, x, scrollY, 58, 28);
            }
        }

        private static Color ColorFromHue(int hueDegrees, int saturation, int value)
        {
            double hue = Math.Clamp(hueDegrees, 0, 359) / 60D;
            double chroma = Math.Clamp(value, 0, 255) / 255D * Math.Clamp(saturation, 0, 255) / 255D;
            double x = chroma * (1D - Math.Abs((hue % 2D) - 1D));
            double match = Math.Clamp(value, 0, 255) / 255D - chroma;
            (double red, double green, double blue) = hue switch
            {
                < 1D => (chroma, x, 0D),
                < 2D => (x, chroma, 0D),
                < 3D => (0D, chroma, x),
                < 4D => (0D, x, chroma),
                < 5D => (x, 0D, chroma),
                _ => (chroma, 0D, x)
            };

            return Color.FromArgb(
                (int)Math.Round((red + match) * 255D),
                (int)Math.Round((green + match) * 255D),
                (int)Math.Round((blue + match) * 255D));
        }
    }

    private sealed record SmoothLiveMetricSample
    {
        public double ElapsedSeconds { get; init; }

        public bool Ok { get; init; }

        public double CurrentTime { get; init; }

        public int ReadyState { get; init; }

        public bool Paused { get; init; }

        public double PlaybackRate { get; init; }

        public int DecodedFrames { get; init; }

        public int DroppedFrames { get; init; }

        public double? LatencySeconds { get; init; }

        public bool WebSocketConnected { get; init; }

        public string MseReadyState { get; init; } = "none";

        public int ReconnectCount { get; init; }

        public int MseRestartCount { get; init; }

        public int QueueOverflowCount { get; init; }

        public int StallCount { get; init; }

        public int WaitingCount { get; init; }

        public int AppendedSegments { get; init; }

        public int PlaybackStartCount { get; init; }

        public bool WindowWasHidden { get; set; }

        public string? Error { get; init; }

        public static SmoothLiveMetricSample Failed(double elapsedSeconds, string error)
        {
            return new SmoothLiveMetricSample
            {
                ElapsedSeconds = elapsedSeconds,
                Error = error
            };
        }

        public static SmoothLiveMetricSample FromScriptResult(string scriptResult, double elapsedSeconds)
        {
            using JsonDocument jsonDocument = JsonDocument.Parse(scriptResult);
            JsonElement rootElement = jsonDocument.RootElement;
            if (rootElement.ValueKind != JsonValueKind.Object)
            {
                return Failed(elapsedSeconds, $"unexpected script result: {rootElement.ValueKind}");
            }

            return new SmoothLiveMetricSample
            {
                ElapsedSeconds = elapsedSeconds,
                Ok = ReadBoolean(rootElement, "ok"),
                CurrentTime = ReadDouble(rootElement, "currentTime"),
                ReadyState = ReadInteger(rootElement, "readyState"),
                Paused = ReadBoolean(rootElement, "paused"),
                PlaybackRate = ReadDouble(rootElement, "playbackRate"),
                DecodedFrames = ReadInteger(rootElement, "decodedFrames"),
                DroppedFrames = ReadInteger(rootElement, "droppedFrames"),
                LatencySeconds = ReadNullableDouble(rootElement, "latencySeconds"),
                WebSocketConnected = ReadBoolean(rootElement, "websocketConnected"),
                MseReadyState = ReadString(rootElement, "mseReadyState", "none"),
                ReconnectCount = ReadInteger(rootElement, "reconnectCount"),
                MseRestartCount = ReadInteger(rootElement, "mseRestartCount"),
                QueueOverflowCount = ReadInteger(rootElement, "queueOverflowCount"),
                StallCount = ReadInteger(rootElement, "stallCount"),
                WaitingCount = ReadInteger(rootElement, "waitingCount"),
                AppendedSegments = ReadInteger(rootElement, "appendedSegments"),
                PlaybackStartCount = ReadInteger(rootElement, "playbackStartCount")
            };
        }

        private static bool ReadBoolean(JsonElement rootElement, string propertyName)
        {
            return rootElement.TryGetProperty(propertyName, out JsonElement property) &&
                property.ValueKind == JsonValueKind.True;
        }

        private static int ReadInteger(JsonElement rootElement, string propertyName)
        {
            if (!rootElement.TryGetProperty(propertyName, out JsonElement property))
            {
                return 0;
            }

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out int integerValue))
            {
                return integerValue;
            }

            return 0;
        }

        private static double ReadDouble(JsonElement rootElement, string propertyName)
        {
            if (!rootElement.TryGetProperty(propertyName, out JsonElement property))
            {
                return 0D;
            }

            if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out double doubleValue))
            {
                return doubleValue;
            }

            return 0D;
        }

        private static double? ReadNullableDouble(JsonElement rootElement, string propertyName)
        {
            if (!rootElement.TryGetProperty(propertyName, out JsonElement property) ||
                property.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out double doubleValue))
            {
                return doubleValue;
            }

            return null;
        }

        private static string ReadString(JsonElement rootElement, string propertyName, string defaultValue)
        {
            if (!rootElement.TryGetProperty(propertyName, out JsonElement property) ||
                property.ValueKind != JsonValueKind.String)
            {
                return defaultValue;
            }

            return property.GetString() ?? defaultValue;
        }
    }

    private sealed class SmoothLiveBrowserDiagnosticResult
    {
        private SmoothLiveBrowserDiagnosticResult(
            bool ready,
            int totalSampleCount,
            int usableSampleCount,
            double averageDecodedFps,
            double averageLatencySeconds,
            double maximumLatencySeconds,
            double latencyIncreaseSecondsPerMinute,
            int playbackStopCount,
            int stallEventCount,
            int queueOverflowEventCount,
            int mseRestartCount,
            bool webSocketConnected,
            bool mseOpened,
            bool visibilityRecoveryAttempted,
            bool visibilityRecoveredWithinFiveSeconds,
            IReadOnlyList<string> failureReasons)
        {
            Ready = ready;
            TotalSampleCount = totalSampleCount;
            UsableSampleCount = usableSampleCount;
            AverageDecodedFps = averageDecodedFps;
            AverageLatencySeconds = averageLatencySeconds;
            MaximumLatencySeconds = maximumLatencySeconds;
            LatencyIncreaseSecondsPerMinute = latencyIncreaseSecondsPerMinute;
            PlaybackStopCount = playbackStopCount;
            StallEventCount = stallEventCount;
            QueueOverflowEventCount = queueOverflowEventCount;
            MseRestartCount = mseRestartCount;
            WebSocketConnected = webSocketConnected;
            MseOpened = mseOpened;
            VisibilityRecoveryAttempted = visibilityRecoveryAttempted;
            VisibilityRecoveredWithinFiveSeconds = visibilityRecoveredWithinFiveSeconds;
            FailureReasons = failureReasons;
        }

        public bool Ready { get; }

        public int TotalSampleCount { get; }

        public int UsableSampleCount { get; }

        public double AverageDecodedFps { get; }

        public double AverageLatencySeconds { get; }

        public double MaximumLatencySeconds { get; }

        public double LatencyIncreaseSecondsPerMinute { get; }

        public int PlaybackStopCount { get; }

        public int StallEventCount { get; }

        public int QueueOverflowEventCount { get; }

        public int MseRestartCount { get; }

        public bool WebSocketConnected { get; }

        public bool MseOpened { get; }

        public bool VisibilityRecoveryAttempted { get; }

        public bool VisibilityRecoveredWithinFiveSeconds { get; }

        public IReadOnlyList<string> FailureReasons { get; }

        public static SmoothLiveBrowserDiagnosticResult Failed(string reason)
        {
            return new SmoothLiveBrowserDiagnosticResult(
                ready: false,
                totalSampleCount: 0,
                usableSampleCount: 0,
                averageDecodedFps: 0D,
                averageLatencySeconds: 0D,
                maximumLatencySeconds: 0D,
                latencyIncreaseSecondsPerMinute: 0D,
                playbackStopCount: 0,
                stallEventCount: 0,
                queueOverflowEventCount: 0,
                mseRestartCount: 0,
                webSocketConnected: false,
                mseOpened: false,
                visibilityRecoveryAttempted: false,
                visibilityRecoveredWithinFiveSeconds: false,
                failureReasons: [reason]);
        }

        public static SmoothLiveBrowserDiagnosticResult FromSamples(
            IReadOnlyList<SmoothLiveMetricSample> samples,
            TimeSpan duration,
            bool visibilityRecoveryAttempted,
            double? visibilityRestoredAtSeconds,
            Exception? startupException)
        {
            const double warmupSeconds = 12D;
            List<SmoothLiveMetricSample> recoveryCandidateSamples = samples
                .Where(sample => sample.Error is null &&
                    sample.Ok &&
                    !sample.WindowWasHidden &&
                    sample.ElapsedSeconds >= warmupSeconds)
                .OrderBy(sample => sample.ElapsedSeconds)
                .ToList();
            List<SmoothLiveMetricSample> usableSamples = samples
                .Where(sample => sample.Error is null &&
                    sample.Ok &&
                    !sample.WindowWasHidden &&
                    !IsWithinVisibilityRecoveryWindow(sample, visibilityRestoredAtSeconds) &&
                    sample.ElapsedSeconds >= warmupSeconds)
                .OrderBy(sample => sample.ElapsedSeconds)
                .ToList();
            List<string> failureReasons = [];
            if (startupException is not null)
            {
                failureReasons.Add(startupException.Message);
            }

            int minimumUsableSamples = Math.Min(60, Math.Max(12, (int)Math.Floor(duration.TotalSeconds * 0.5D)));
            if (usableSamples.Count < minimumUsableSamples)
            {
                failureReasons.Add($"usable samples {usableSamples.Count} < {minimumUsableSamples}");
            }

            double averageDecodedFps = CalculateAverageDecodedFps(usableSamples);
            List<double> latencies = usableSamples
                .Select(sample => sample.LatencySeconds)
                .Where(latencySeconds => latencySeconds.HasValue)
                .Select(latencySeconds => latencySeconds!.Value)
                .Where(latencySeconds => latencySeconds > 0D)
                .ToList();
            double averageLatencySeconds = latencies.Count == 0 ? 0D : latencies.Average();
            double maximumLatencySeconds = latencies.Count == 0 ? 0D : latencies.Max();
            double latencyIncreaseSecondsPerMinute = CalculateLatencyIncreaseSecondsPerMinute(usableSamples);
            int playbackStopCount = CountPlaybackStops(usableSamples);
            int stallEventCount = CountEventBursts(usableSamples, sample => sample.StallCount + sample.WaitingCount);
            int queueOverflowEventCount = CountEventDelta(usableSamples, sample => sample.QueueOverflowCount);
            int mseRestartCount = CountEventDelta(usableSamples, sample => sample.MseRestartCount);
            bool webSocketConnected = usableSamples.Any(sample => sample.WebSocketConnected);
            bool mseOpened = usableSamples.Any(sample => string.Equals(sample.MseReadyState, "open", StringComparison.OrdinalIgnoreCase));
            bool visibilityRecoveredWithinFiveSeconds = !visibilityRecoveryAttempted ||
                DidRecoverAfterVisibilityRestore(recoveryCandidateSamples, visibilityRestoredAtSeconds);

            if (averageDecodedFps < 55D)
            {
                failureReasons.Add($"decoded fps {averageDecodedFps:0.0} < 55.0");
            }

            if (averageLatencySeconds < 1.7D || averageLatencySeconds > 2.6D)
            {
                failureReasons.Add($"average latency {averageLatencySeconds:0.00}s outside 1.70-2.60s");
            }

            if (maximumLatencySeconds > 3.0D)
            {
                failureReasons.Add($"max latency {maximumLatencySeconds:0.00}s > 3.00s");
            }

            if (latencyIncreaseSecondsPerMinute > 0.1D)
            {
                failureReasons.Add($"latency increase {latencyIncreaseSecondsPerMinute:0.000}s/min > 0.100s/min");
            }

            if (playbackStopCount > 0)
            {
                failureReasons.Add($"playback stopped {playbackStopCount} time(s)");
            }

            if (stallEventCount > 1)
            {
                failureReasons.Add($"stall/waiting bursts {stallEventCount} > 1");
            }

            if (queueOverflowEventCount > 1)
            {
                failureReasons.Add($"queue overflow events {queueOverflowEventCount} > 1");
            }

            if (!webSocketConnected)
            {
                failureReasons.Add("WebSocket never connected after warmup");
            }

            if (!mseOpened)
            {
                failureReasons.Add("MSE never reached open state after warmup");
            }

            if (visibilityRecoveryAttempted && !visibilityRecoveredWithinFiveSeconds)
            {
                failureReasons.Add("visibility recovery did not return to live playback within 5 seconds");
            }

            return new SmoothLiveBrowserDiagnosticResult(
                ready: failureReasons.Count == 0,
                totalSampleCount: samples.Count,
                usableSampleCount: usableSamples.Count,
                averageDecodedFps,
                averageLatencySeconds,
                maximumLatencySeconds,
                latencyIncreaseSecondsPerMinute,
                playbackStopCount,
                stallEventCount,
                queueOverflowEventCount,
                mseRestartCount,
                webSocketConnected,
                mseOpened,
                visibilityRecoveryAttempted,
                visibilityRecoveredWithinFiveSeconds,
                failureReasons);
        }

        private static double CalculateAverageDecodedFps(IReadOnlyList<SmoothLiveMetricSample> samples)
        {
            if (samples.Count < 2)
            {
                return 0D;
            }

            double decodedFrameCount = 0D;
            double sampleSeconds = 0D;
            for (int sampleIndex = 1; sampleIndex < samples.Count; sampleIndex++)
            {
                SmoothLiveMetricSample previousSample = samples[sampleIndex - 1];
                SmoothLiveMetricSample currentSample = samples[sampleIndex];
                double elapsedSeconds = currentSample.ElapsedSeconds - previousSample.ElapsedSeconds;
                int decodedDelta = currentSample.DecodedFrames - previousSample.DecodedFrames;
                if (elapsedSeconds <= 0.2D || decodedDelta < 0 || decodedDelta > elapsedSeconds * 180D)
                {
                    continue;
                }

                decodedFrameCount += decodedDelta;
                sampleSeconds += elapsedSeconds;
            }

            return sampleSeconds <= 0D ? 0D : decodedFrameCount / sampleSeconds;
        }

        private static double CalculateLatencyIncreaseSecondsPerMinute(IReadOnlyList<SmoothLiveMetricSample> samples)
        {
            List<SmoothLiveMetricSample> latencySamples = samples
                .Where(sample => sample.LatencySeconds.HasValue)
                .ToList();
            if (latencySamples.Count < 2)
            {
                return 0D;
            }

            SmoothLiveMetricSample firstSample = latencySamples[0];
            SmoothLiveMetricSample lastSample = latencySamples[^1];
            double elapsedSeconds = lastSample.ElapsedSeconds - firstSample.ElapsedSeconds;
            if (elapsedSeconds <= 0D)
            {
                return 0D;
            }

            double increaseSeconds = Math.Max(0D, lastSample.LatencySeconds!.Value - firstSample.LatencySeconds!.Value);
            return increaseSeconds / elapsedSeconds * 60D;
        }

        private static int CountPlaybackStops(IReadOnlyList<SmoothLiveMetricSample> samples)
        {
            int stopCount = 0;
            bool previousIntervalWasStopped = false;
            for (int sampleIndex = 1; sampleIndex < samples.Count; sampleIndex++)
            {
                SmoothLiveMetricSample previousSample = samples[sampleIndex - 1];
                SmoothLiveMetricSample currentSample = samples[sampleIndex];
                double elapsedSeconds = currentSample.ElapsedSeconds - previousSample.ElapsedSeconds;
                bool stopped = elapsedSeconds >= 0.5D &&
                    currentSample.ReadyState >= 2 &&
                    Math.Abs(currentSample.CurrentTime - previousSample.CurrentTime) < 0.05D;
                if (stopped && !previousIntervalWasStopped)
                {
                    stopCount++;
                }

                previousIntervalWasStopped = stopped;
            }

            return stopCount;
        }

        private static int CountEventDelta(
            IReadOnlyList<SmoothLiveMetricSample> samples,
            Func<SmoothLiveMetricSample, int> readEventCount)
        {
            if (samples.Count < 2)
            {
                return 0;
            }

            return Math.Max(0, readEventCount(samples[^1]) - readEventCount(samples[0]));
        }

        private static int CountEventBursts(
            IReadOnlyList<SmoothLiveMetricSample> samples,
            Func<SmoothLiveMetricSample, int> readEventCount)
        {
            if (samples.Count < 2)
            {
                return 0;
            }

            int burstCount = 0;
            bool previousIntervalHadEvent = false;
            for (int sampleIndex = 1; sampleIndex < samples.Count; sampleIndex++)
            {
                SmoothLiveMetricSample previousSample = samples[sampleIndex - 1];
                SmoothLiveMetricSample currentSample = samples[sampleIndex];
                double elapsedSeconds = currentSample.ElapsedSeconds - previousSample.ElapsedSeconds;
                if (elapsedSeconds <= 0D || elapsedSeconds > 2.5D)
                {
                    previousIntervalHadEvent = false;
                    continue;
                }

                int eventDelta = readEventCount(currentSample) - readEventCount(previousSample);
                bool intervalHadEvent = eventDelta > 0;
                if (intervalHadEvent && !previousIntervalHadEvent)
                {
                    burstCount++;
                }

                previousIntervalHadEvent = intervalHadEvent;
            }

            return burstCount;
        }

        private static bool DidRecoverAfterVisibilityRestore(
            IReadOnlyList<SmoothLiveMetricSample> usableSamples,
            double? visibilityRestoredAtSeconds)
        {
            if (!visibilityRestoredAtSeconds.HasValue)
            {
                return false;
            }

            double restoredAtSeconds = visibilityRestoredAtSeconds.Value;
            return usableSamples.Any(sample =>
                sample.ElapsedSeconds >= restoredAtSeconds &&
                sample.ElapsedSeconds <= restoredAtSeconds + 5D &&
                sample.ReadyState >= 2 &&
                !sample.Paused &&
                sample.WebSocketConnected &&
                sample.LatencySeconds is <= ScreenStreamingServer.H264Fmp4MaximumLatencySeconds);
        }

        private static bool IsWithinVisibilityRecoveryWindow(
            SmoothLiveMetricSample sample,
            double? visibilityRestoredAtSeconds)
        {
            if (!visibilityRestoredAtSeconds.HasValue)
            {
                return false;
            }

            double restoredAtSeconds = visibilityRestoredAtSeconds.Value;
            return sample.ElapsedSeconds >= restoredAtSeconds &&
                sample.ElapsedSeconds <= restoredAtSeconds + 5D;
        }
    }

    private sealed class Fmp4WebSocketDiagnosticResult
    {
        public Fmp4WebSocketDiagnosticResult(
            bool accepted,
            int initBytes,
            int fragmentBytes,
            bool initLooksLikeInitializationSegment,
            bool fragmentLooksLikeMediaSegment)
        {
            Accepted = accepted;
            InitBytes = initBytes;
            FragmentBytes = fragmentBytes;
            InitLooksLikeInitializationSegment = initLooksLikeInitializationSegment;
            FragmentLooksLikeMediaSegment = fragmentLooksLikeMediaSegment;
        }

        public bool Accepted { get; }

        public int InitBytes { get; }

        public int FragmentBytes { get; }

        public bool InitLooksLikeInitializationSegment { get; }

        public bool FragmentLooksLikeMediaSegment { get; }
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
