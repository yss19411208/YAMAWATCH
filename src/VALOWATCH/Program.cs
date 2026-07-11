using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Runtime.InteropServices;

namespace VALOWATCH;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
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

        if (args.Any(argument => string.Equals(argument, "--list-microphones", StringComparison.OrdinalIgnoreCase)))
        {
            RunMicrophoneListDiagnostic();
            return;
        }

        using Mutex singleInstanceMutex = new(true, "Local\\VALOWATCH.SingleInstance", out bool ownsSingleInstance);
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

        Application.Run(new MainForm(
            appPaths,
            new AppStateStore(appPaths),
            new LoopbackRecorder(discordBotSettingsStore),
            new DiscordMediaSharer(appPaths, discordBotSettingsStore),
            new VideoCaptureSession(appPaths, new VideoCaptureSettingsStore(appPaths)),
            new DiscordBotVoiceRelay(discordBotSettingsStore, appPaths),
            new GitUpdateChecker(new GitUpdateSettingsStore(appPaths)),
            new GitAutoUpdater(new GitUpdateSettingsStore(appPaths), appPaths),
            new StartupService(),
            disableDiscordAutomation));

        GC.KeepAlive(singleInstanceMutex);
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
            string selectedDeviceName = DiscordBotVoiceRelay.GetDefaultMicrophoneDevice(preferredMicrophoneDeviceName).FriendlyName;
            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath) ?? AppContext.BaseDirectory);
            File.AppendAllText(
                logFilePath,
                $"{DateTimeOffset.Now:O} [Diagnostics] Microphone devices: preferred=\"{preferredMicrophoneDeviceName}\"; selected=\"{selectedDeviceName}\"; active=[{string.Join(" | ", deviceNames)}]{Environment.NewLine}");
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
}
