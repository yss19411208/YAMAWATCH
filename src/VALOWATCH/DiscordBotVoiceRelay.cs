using Discord;
using Discord.Audio;
using Discord.LibDave;
using Discord.WebSocket;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace VALOWATCH;

public sealed class DiscordBotVoiceRelay : IDisposable
{
    private static readonly WaveFormat DiscordPcmFormat = new(48000, 16, 2);
    private const int DiscordPcmFrameBytes = 3840;
    private const float AudiblePeakThreshold = 0.003F;
    private static readonly byte[] SilenceFrame = new byte[DiscordPcmFrameBytes];
    private static readonly TimeSpan MicrophoneCaptureBufferDuration = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MicrophoneBufferDuration = TimeSpan.FromMilliseconds(1600);
    private static readonly TimeSpan MicrophoneStartupBufferDuration = TimeSpan.FromMilliseconds(260);
    private static readonly TimeSpan LineLoopbackBufferDuration = TimeSpan.FromMilliseconds(1600);
    private static readonly TimeSpan RelayFrameDuration = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan AudioStatsLogInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DiscordGatewayReadyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DiscordVoiceConnectTimeout = TimeSpan.FromSeconds(20);
    private const bool DiscordVoiceDaveEncryptionEnabled = true;

    private readonly DiscordBotSettingsStore settingsStore;
    private readonly AppPaths appPaths;
    private readonly string logFilePath;
    private readonly object logLock = new();
    private readonly object stateLock = new();
    private readonly object audioStatsLock = new();

    private DiscordSocketClient? discordClient;
    private IAudioClient? audioClient;
    private WasapiCapture? microphoneCapture;
    private BufferedWaveProvider? bufferedWaveProvider;
    private LineProcessLoopbackWaveProvider? lineProcessLoopbackProvider;
    private IWaveProvider? discordPcmProvider;
    private AudioOutStream? discordStream;
    private CancellationTokenSource? relayCancellationTokenSource;
    private Task? relayTask;
    private bool stopRequested;
    private long capturedCallbackCount;
    private long capturedByteCount;
    private long capturedAudibleCallbackCount;
    private long writtenFrameCount;
    private long writtenAudibleFrameCount;
    private long writtenSilenceFrameCount;
    private long writtenShortFrameCount;
    private float capturedPeak;
    private float writtenPeak;
    private bool loggedFirstAudibleCapture;
    private bool loggedFirstAudibleWrite;
    private bool audioDiagnosticMessageSent;
    private string currentMicrophoneDeviceName = string.Empty;
    private string currentCaptureDeviceList = string.Empty;
    private string currentLineLoopbackSourceName = string.Empty;
    private DateTimeOffset audioStatsStartedAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastAudioStatsLogTime = DateTimeOffset.MinValue;

    public DiscordBotVoiceRelay(DiscordBotSettingsStore settingsStore, AppPaths appPaths)
    {
        this.settingsStore = settingsStore;
        this.appPaths = appPaths;
        logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");
        settingsStore.EnsureSampleConfig();
    }

    public string StatusText { get; private set; } = "Discord idle";

    public bool HasConfig => settingsStore.HasConfig;

    public bool IsRunning { get; private set; }

    public async Task StartForValorantAsync()
    {
        lock (stateLock)
        {
            if (IsRunning)
            {
                return;
            }

            stopRequested = false;
            StatusText = settingsStore.HasConfig ? "Discord connecting" : "Discord config missing";
        }

        WriteLog("VALORANT trigger received. Starting Discord automation.");

        DiscordBotSettings? settings;
        try
        {
            settings = settingsStore.Load(out string configStatusText);
            if (settings is null)
            {
                StatusText = configStatusText;
                WriteLog($"Discord settings are not usable: {configStatusText}");
                return;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            StatusText = $"Discord config failed: {exception.Message}";
            WriteLog("Discord settings failed to load.", exception);
            return;
        }

        WriteLog(
            $"Discord settings loaded. Guild: {settings.GuildId}. Voice: {settings.VoiceChannelId}. " +
            $"Text: {settings.TextChannelId}. StreamMic: {settings.StreamMicrophoneAudio}. " +
            $"MicDevice: {settings.MicrophoneDeviceName}. Volume: {settings.MicrophoneVolume:0.00}. " +
            $"NoiseGate: {settings.MicrophoneNoiseGate:0.000}. " +
            $"StreamLineAudio: {settings.StreamLineAudioWhenRunning}. " +
            $"LineProcesses: {string.Join(",", settings.LineAudioProcessNames)}. " +
            $"ShareMedia: {settings.ShareMediaFiles}.");

        if (settings.TryScreenShare)
        {
            StatusText = "Screen share unsupported";
        }

        if (!TryEnsureVoiceNativeDependencies(out string nativeDependencyStatus))
        {
            StatusText = nativeDependencyStatus;
            WriteLog(nativeDependencyStatus);
            return;
        }

        try
        {
            DiscordSocketClient client = CreateClient();
            WriteRuntimeDiagnostic(client);
            AttachClientEvents(client);
            discordClient = client;

            TaskCompletionSource readyCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Ready += () =>
            {
                readyCompletionSource.TrySetResult();
                return Task.CompletedTask;
            };

            await client.LoginAsync(TokenType.Bot, settings.BotToken).ConfigureAwait(false);
            await client.StartAsync().ConfigureAwait(false);
            await readyCompletionSource.Task.WaitAsync(DiscordGatewayReadyTimeout).ConfigureAwait(false);
            WriteLog("Discord gateway is ready.");

            SocketGuild guild = client.GetGuild(settings.GuildId)
                ?? throw new InvalidOperationException("指定されたDiscordサーバーが見つかりません。Botがサーバーに参加しているか確認してください。");
            SocketVoiceChannel voiceChannel = guild.GetVoiceChannel(settings.VoiceChannelId)
                ?? throw new InvalidOperationException("指定されたDiscord VCが見つかりません。VoiceChannelIdを確認してください。");

            EnsureVoiceChannelPermissions(guild, voiceChannel);

            WriteLog($"Connecting to Discord voice channel {voiceChannel.Id}.");
            audioClient = await voiceChannel
                .ConnectAsync(selfDeaf: false, selfMute: false)
                .WaitAsync(DiscordVoiceConnectTimeout)
                .ConfigureAwait(false);
            WriteLog($"Joined Discord voice channel {voiceChannel.Id}. SelfDeaf: false. SelfMute: false.");
            WriteLog("Discord text notifications are disabled.");

            bool audioRelayStarted = false;

            if (settings.StreamMicrophoneAudio)
            {
                try
                {
                    StartMicrophoneAudioRelay(settings);
                    audioRelayStarted = true;
                }
                catch (Exception audioException)
                {
                    WriteLog("Discord voice channel joined, but microphone audio relay could not start.", audioException);
                    DisposeAudioObjects();
                    lock (stateLock)
                    {
                        IsRunning = true;
                        StatusText = FormatRunningStatus(
                            "Discord joined VC, audio failed",
                            audioException.Message);
                    }

                    return;
                }
            }

            lock (stateLock)
            {
                IsRunning = true;
                StatusText = FormatRunningStatus(audioRelayStarted ? "Discord mic live" : "Discord joined VC");
            }
        }
        catch (Exception exception)
        {
            WriteLog("Discord startup failed. Stopping Discord client before retry.", exception);
            await StopAsync().ConfigureAwait(false);
            StatusText = $"Discord failed: {exception.Message}";
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cancellationTokenSource;
        Task? activeRelayTask;

        lock (stateLock)
        {
            cancellationTokenSource = relayCancellationTokenSource;
            activeRelayTask = relayTask;
            relayCancellationTokenSource = null;
            relayTask = null;
            IsRunning = false;
            stopRequested = true;
            StatusText = settingsStore.HasConfig ? "Discord idle" : "Discord config missing";
        }

        if (cancellationTokenSource is not null)
        {
            await cancellationTokenSource.CancelAsync().ConfigureAwait(false);
        }

        if (activeRelayTask is not null)
        {
            try
            {
                await activeRelayTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                WriteLog("Audio relay task ended while stopping.", exception);
            }
        }

        DisposeAudioObjects();

        if (audioClient is not null)
        {
            await audioClient.StopAsync().ConfigureAwait(false);
            audioClient.Dispose();
            audioClient = null;
        }

        if (discordClient is not null)
        {
            DetachClientEvents(discordClient);
            await discordClient.LogoutAsync().ConfigureAwait(false);
            await discordClient.StopAsync().ConfigureAwait(false);
            await discordClient.DisposeAsync().ConfigureAwait(false);
            discordClient = null;
        }
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }

    private static DiscordSocketClient CreateClient()
    {
        return new DiscordSocketClient(new DiscordSocketConfig
        {
            EnableVoiceDaveEncryption = DiscordVoiceDaveEncryptionEnabled,
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates,
            LogLevel = LogSeverity.Warning
        });
    }

    internal static bool TryEnsureVoiceNativeDependencies(out string statusText)
    {
        if (!TryLoadNativeLibrary("libsodium", out string sodiumStatus))
        {
            statusText = $"Discord voice DLL missing: {sodiumStatus}";
            return false;
        }

        if (!TryLoadNativeLibrary("libdave", out string libdaveStatus))
        {
            statusText = $"Discord voice DLL missing: {libdaveStatus}";
            return false;
        }

        try
        {
            if (!Dave.CheckAvailability())
            {
                statusText = "Discord voice DLL missing: libdave is not available";
                return false;
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            statusText = $"Discord voice DLL missing: {exception.Message}";
            return false;
        }

        if (!TryLoadNativeLibrary("opus", out string opusStatus))
        {
            statusText = $"Discord voice DLL missing: {opusStatus}";
            return false;
        }

        statusText = "Discord voice DLL ready";
        return true;
    }

    private static bool TryLoadNativeLibrary(string libraryName, out string statusText)
    {
        string platformLibraryPath = Path.Combine(AppContext.BaseDirectory, GetPlatformLibraryFileName(libraryName));
        if (File.Exists(platformLibraryPath) && NativeLibrary.TryLoad(platformLibraryPath, out IntPtr platformLibraryHandle))
        {
            NativeLibrary.Free(platformLibraryHandle);
            statusText = $"{libraryName} loaded from app directory";
            return true;
        }

        if (!NativeLibrary.TryLoad(libraryName, out IntPtr libraryHandle))
        {
            statusText = $"{libraryName} could not be loaded";
            return false;
        }

        NativeLibrary.Free(libraryHandle);
        statusText = $"{libraryName} loaded";
        return true;
    }

    private static string GetPlatformLibraryFileName(string libraryName)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return $"{libraryName}.dll";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            string libraryBaseName = libraryName.StartsWith("lib", StringComparison.OrdinalIgnoreCase)
                ? libraryName
                : $"lib{libraryName}";
            return $"{libraryBaseName}.dylib";
        }

        string sharedObjectBaseName = libraryName.StartsWith("lib", StringComparison.OrdinalIgnoreCase)
            ? libraryName
            : $"lib{libraryName}";
        return $"{sharedObjectBaseName}.so";
    }

    private void AttachClientEvents(DiscordSocketClient client)
    {
        client.Log += OnDiscordLogAsync;
        client.Connected += OnDiscordConnectedAsync;
        client.Disconnected += OnDiscordDisconnectedAsync;
    }

    private void DetachClientEvents(DiscordSocketClient client)
    {
        client.Log -= OnDiscordLogAsync;
        client.Connected -= OnDiscordConnectedAsync;
        client.Disconnected -= OnDiscordDisconnectedAsync;
    }

    private Task OnDiscordLogAsync(LogMessage logMessage)
    {
        if (logMessage.Exception is null)
        {
            WriteLog($"Discord.Net {logMessage.Severity}: {logMessage.Source}: {logMessage.Message}");
        }
        else
        {
            WriteLog($"Discord.Net {logMessage.Severity}: {logMessage.Source}: {logMessage.Message}", logMessage.Exception);
        }

        if (logMessage.Message?.Contains("libdave will be required", StringComparison.OrdinalIgnoreCase) == true)
        {
            WriteLog(
                "Discord.Net emitted the legacy libdave warning. " +
                "Current VALOWATCH builds request DAVE explicitly; check the preceding Runtime diagnostic line. " +
                "If DaveClientInternal is not True, an old executable is still running.");
        }

        return Task.CompletedTask;
    }

    private void WriteRuntimeDiagnostic(DiscordSocketClient client)
    {
        try
        {
            Assembly applicationAssembly = typeof(DiscordBotVoiceRelay).Assembly;
            string informationalVersion = applicationAssembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "(unknown)";
            string currentCommit = ReadConfiguredCurrentCommit();
            string discordNetVersion = typeof(DiscordSocketClient)
                .Assembly
                .GetName()
                .Version
                ?.ToString() ?? "(unknown)";
            string daveClientSetting = TryReadClientLibDaveSetting(client);
            string daveMaxProtocolVersion = TryReadDaveMaxProtocolVersion();

            WriteLog(
                "Runtime diagnostic. " +
                $"BaseDirectory: {AppContext.BaseDirectory}. " +
                $"ProcessPath: {Environment.ProcessPath ?? "(unknown)"}. " +
                $"AppVersion: {informationalVersion}. " +
                $"ConfiguredCommit: {currentCommit}. " +
                $"DiscordNetVersion: {discordNetVersion}. " +
                $"DaveRequested: {DiscordVoiceDaveEncryptionEnabled}. " +
                $"DaveClientInternal: {daveClientSetting}. " +
                $"DaveMaxProtocol: {daveMaxProtocolVersion}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or AmbiguousMatchException or TargetInvocationException)
        {
            WriteLog("Runtime diagnostic failed.", exception);
        }
    }

    private string ReadConfiguredCurrentCommit()
    {
        try
        {
            IReadOnlyDictionary<string, string> envValues = EnvSettingsLoader.Load(appPaths);
            return TryReadEnvValue(envValues, "VALOWATCH_UPDATE_CURRENT_COMMIT", "CURRENT_COMMIT");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"(unavailable: {exception.Message})";
        }
    }

    private static string TryReadEnvValue(IReadOnlyDictionary<string, string> envValues, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (envValues.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "(not set)";
    }

    private static string TryReadClientLibDaveSetting(DiscordSocketClient client)
    {
        try
        {
            PropertyInfo? libDaveEnabledProperty = typeof(DiscordSocketClient).GetProperty(
                "LibDaveEnabled",
                BindingFlags.Instance | BindingFlags.NonPublic);
            object? libDaveEnabledValue = libDaveEnabledProperty?.GetValue(client);
            return libDaveEnabledValue?.ToString() ?? "(null)";
        }
        catch (Exception exception) when (exception is AmbiguousMatchException or TargetInvocationException or MethodAccessException)
        {
            return $"(unavailable: {exception.Message})";
        }
    }

    private static string TryReadDaveMaxProtocolVersion()
    {
        try
        {
            return Dave.MaxSupportedProtocolVersion.ToString();
        }
        catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            return $"(unavailable: {exception.Message})";
        }
    }

    private Task OnDiscordConnectedAsync()
    {
        WriteLog("Discord gateway connected.");
        return Task.CompletedTask;
    }

    private Task OnDiscordDisconnectedAsync(Exception exception)
    {
        WriteLog("Discord gateway disconnected.", exception);
        lock (stateLock)
        {
            if (!stopRequested && IsRunning)
            {
                StatusText = $"Discord reconnecting: {exception.Message}";
            }
        }

        return Task.CompletedTask;
    }

    private static string FormatRunningStatus(string baseStatus, string? audioFailure = null)
    {
        if (!string.IsNullOrWhiteSpace(audioFailure))
        {
            return $"{baseStatus}: {audioFailure}";
        }

        return baseStatus;
    }

    private void EnsureVoiceChannelPermissions(SocketGuild guild, SocketVoiceChannel voiceChannel)
    {
        SocketGuildUser currentUser = guild.CurrentUser;
        ChannelPermissions permissions = currentUser.GetPermissions(voiceChannel);
        WriteLog($"Discord voice permissions. Connect: {permissions.Connect}. Speak: {permissions.Speak}.");

        if (!permissions.Connect)
        {
            throw new InvalidOperationException("BotにVCへ接続する権限がありません。Discord側で Connect 権限を付けてください。");
        }

        if (!permissions.Speak)
        {
            throw new InvalidOperationException("BotにVCで発言する権限がありません。Discord側で Speak 権限を付けてください。");
        }
    }

    private void StartMicrophoneAudioRelay(DiscordBotSettings settings)
    {
        if (audioClient is null)
        {
            throw new InvalidOperationException("Discord VCへ接続していません。");
        }

        currentCaptureDeviceList = string.Join(" | ", ListActiveMicrophoneDevices());
        WriteLog($"Active capture devices: {currentCaptureDeviceList}.");
        MMDevice defaultMicrophoneDevice = GetDefaultMicrophoneDevice(settings.MicrophoneDeviceName);
        currentMicrophoneDeviceName = defaultMicrophoneDevice.FriendlyName;
        microphoneCapture = new WasapiCapture(
            defaultMicrophoneDevice,
            useEventSync: false,
            audioBufferMillisecondsLength: (int)MicrophoneCaptureBufferDuration.TotalMilliseconds);
        bufferedWaveProvider = new BufferedWaveProvider(microphoneCapture.WaveFormat)
        {
            BufferDuration = MicrophoneBufferDuration,
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };

        IWaveProvider? lineAudioProvider = TryStartLineLoopbackAudio(settings);
        discordPcmProvider = CreateDiscordPcmProvider(
            bufferedWaveProvider,
            settings.MicrophoneVolume,
            settings.MicrophoneNoiseGate,
            lineAudioProvider,
            settings.LineAudioVolume);

        discordStream = audioClient.CreatePCMStream(AudioApplication.Voice);
        relayCancellationTokenSource = new CancellationTokenSource();
        ResetAudioStats();

        microphoneCapture.DataAvailable += OnMicrophoneDataAvailable;
        microphoneCapture.RecordingStopped += OnMicrophoneRecordingStopped;
        microphoneCapture.StartRecording();
        WriteLog(
            $"Microphone capture started. Device: {defaultMicrophoneDevice.FriendlyName}. " +
            $"Source format: {microphoneCapture.WaveFormat}. Discord format: {discordPcmProvider.WaveFormat}. " +
            $"Capture buffer: {MicrophoneCaptureBufferDuration.TotalMilliseconds:0}ms. " +
            $"Relay buffer: {MicrophoneBufferDuration.TotalMilliseconds:0}ms. " +
            $"Startup buffer: {MicrophoneStartupBufferDuration.TotalMilliseconds:0}ms. " +
            $"Volume: {settings.MicrophoneVolume:0.00}. Noise gate: {settings.MicrophoneNoiseGate:0.000}. " +
            $"Line loopback: {(lineAudioProvider is null ? "off" : currentLineLoopbackSourceName)}. " +
            $"Line volume: {settings.LineAudioVolume:0.00}. " +
            $"Preferred device: {settings.MicrophoneDeviceName}.");

        relayTask = Task.Run(
            () => RelayAudioLoopAsync(relayCancellationTokenSource.Token),
            relayCancellationTokenSource.Token);
        _ = relayTask.ContinueWith(
            ObserveRelayTaskCompletion,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RelayAudioLoopAsync(CancellationToken cancellationToken)
    {
        if (discordPcmProvider is null || discordStream is null)
        {
            return;
        }

        byte[] pcmFrameBuffer = new byte[DiscordPcmFrameBytes];
        await WaitForMicrophoneStartupBufferAsync(cancellationToken).ConfigureAwait(false);
        Stopwatch relayStopwatch = Stopwatch.StartNew();
        TimeSpan nextFrameDueAt = TimeSpan.Zero;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesRead = discordPcmProvider.Read(pcmFrameBuffer, 0, pcmFrameBuffer.Length);
                if (bytesRead <= 0)
                {
                    await discordStream.WriteAsync(SilenceFrame, cancellationToken).ConfigureAwait(false);
                    ObserveWrittenDiscordFrame(SilenceFrame, SilenceFrame.Length);
                    ObserveWrittenSilenceFrame();
                }
                else if (bytesRead == pcmFrameBuffer.Length)
                {
                    await discordStream.WriteAsync(pcmFrameBuffer, cancellationToken).ConfigureAwait(false);
                    ObserveWrittenDiscordFrame(pcmFrameBuffer, pcmFrameBuffer.Length);
                }
                else
                {
                    Array.Clear(pcmFrameBuffer, bytesRead, pcmFrameBuffer.Length - bytesRead);
                    await discordStream.WriteAsync(pcmFrameBuffer, cancellationToken).ConfigureAwait(false);
                    ObserveWrittenDiscordFrame(pcmFrameBuffer, pcmFrameBuffer.Length);
                    ObserveWrittenShortFrame();
                }

                MaybeWriteAudioStats();
                nextFrameDueAt += RelayFrameDuration;
                TimeSpan frameDelay = nextFrameDueAt - relayStopwatch.Elapsed;
                if (frameDelay > TimeSpan.Zero)
                {
                    await Task.Delay(frameDelay, cancellationToken).ConfigureAwait(false);
                }
                else if (frameDelay < TimeSpan.FromMilliseconds(-120))
                {
                    nextFrameDueAt = relayStopwatch.Elapsed;
                }
            }
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await discordStream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
                {
                    WriteLog("Discord audio stream flush failed.", exception);
                }
            }
        }
    }

    private void ObserveRelayTaskCompletion(Task completedRelayTask)
    {
        if (completedRelayTask.IsCanceled)
        {
            WriteLog("Discord audio relay stopped by cancellation.");
            return;
        }

        if (completedRelayTask.Exception is null)
        {
            WriteLog("Discord audio relay ended without an exception.");
            return;
        }

        Exception relayException = completedRelayTask.Exception.GetBaseException();
        WriteLog("Discord audio relay failed. Keeping the bot in VC without audio.", relayException);
        QueueDiscordStatusMessage(
            "VALOWATCH 音声リレー停止\n" +
            relayException.Message +
            "\nVCには残りますが、音声送信は停止しました。");
        DisposeAudioObjects();

        lock (stateLock)
        {
            if (IsRunning && !stopRequested)
            {
                StatusText = $"Discord joined VC, audio stopped: {relayException.Message}";
            }
        }
    }

    private async Task WaitForMicrophoneStartupBufferAsync(CancellationToken cancellationToken)
    {
        if (bufferedWaveProvider is null)
        {
            return;
        }

        Stopwatch waitStopwatch = Stopwatch.StartNew();
        while (bufferedWaveProvider.BufferedDuration < MicrophoneStartupBufferDuration &&
            waitStopwatch.Elapsed < TimeSpan.FromSeconds(2))
        {
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }

        WriteLog(
            $"Microphone relay start buffer ready. Buffered: {bufferedWaveProvider.BufferedDuration.TotalMilliseconds:0}ms. " +
            $"Waited: {waitStopwatch.ElapsedMilliseconds}ms.");
    }

    private void OnMicrophoneDataAvailable(object? sender, WaveInEventArgs eventArgs)
    {
        if (bufferedWaveProvider is null || eventArgs.BytesRecorded <= 0)
        {
            return;
        }

        bufferedWaveProvider.AddSamples(eventArgs.Buffer, 0, eventArgs.BytesRecorded);
        ObserveCapturedAudio(eventArgs.Buffer, eventArgs.BytesRecorded);
    }

    private IWaveProvider? TryStartLineLoopbackAudio(DiscordBotSettings settings)
    {
        if (!settings.StreamLineAudioWhenRunning)
        {
            WriteLog("LINE process-only loopback audio is disabled.");
            return null;
        }

        string[] lineProcessNames = settings.LineAudioProcessNames.Length == 0
            ? ["LINE", "Line", "line"]
            : settings.LineAudioProcessNames;

        try
        {
            lineProcessLoopbackProvider = new LineProcessLoopbackWaveProvider(
                lineProcessNames,
                LineLoopbackBufferDuration,
                (message, exception) => WriteLog(message, exception));
            currentLineLoopbackSourceName = lineProcessLoopbackProvider.CurrentSourceDescription;
            WriteLog(
                $"LINE process-only loopback provider started. " +
                $"Format: {lineProcessLoopbackProvider.WaveFormat}. Buffer: {LineLoopbackBufferDuration.TotalMilliseconds:0}ms. " +
                $"ProcessNames: {string.Join(", ", lineProcessNames)}.");
            return lineProcessLoopbackProvider;
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException or ArgumentException)
        {
            WriteLog("LINE process-only loopback provider could not start. Continuing with microphone only.", exception);
            DisposeLineLoopbackObjects();
            return null;
        }
    }

    private void OnMicrophoneRecordingStopped(object? sender, StoppedEventArgs eventArgs)
    {
        if (eventArgs.Exception is not null)
        {
            WriteLog("Microphone capture stopped because of an audio device error.", eventArgs.Exception);
            QueueDiscordStatusMessage(
                "VALOWATCH マイク入力停止\n" +
                eventArgs.Exception.Message +
                "\nマイクの抜き差し、Windowsのマイク権限、既定の入力デバイスを確認してください。");
            lock (stateLock)
            {
                if (IsRunning && !stopRequested)
                {
                    StatusText = $"Discord joined VC, capture stopped: {eventArgs.Exception.Message}";
                }
            }

            return;
        }

        WriteLog("Microphone capture stopped.");
    }

    private void DisposeAudioObjects()
    {
        if (microphoneCapture is not null)
        {
            microphoneCapture.DataAvailable -= OnMicrophoneDataAvailable;
            microphoneCapture.RecordingStopped -= OnMicrophoneRecordingStopped;
            try
            {
                microphoneCapture.StopRecording();
            }
            catch (InvalidOperationException)
            {
            }
        }

        DisposeLineLoopbackObjects();
        discordStream?.Dispose();
        microphoneCapture?.Dispose();

        discordStream = null;
        discordPcmProvider = null;
        bufferedWaveProvider = null;
        microphoneCapture = null;
    }

    private void DisposeLineLoopbackObjects()
    {
        lineProcessLoopbackProvider?.Dispose();
        lineProcessLoopbackProvider = null;
        currentLineLoopbackSourceName = string.Empty;
    }

    internal static MMDevice GetDefaultMicrophoneDevice(string? preferredDeviceName = null)
    {
        using MMDeviceEnumerator deviceEnumerator = new();
        List<MMDevice> activeCaptureDevices = deviceEnumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .ToList();

        if (!string.IsNullOrWhiteSpace(preferredDeviceName))
        {
            string trimmedPreferredDeviceName = preferredDeviceName.Trim();
            MMDevice? preferredDevice = activeCaptureDevices.FirstOrDefault(device =>
                device.FriendlyName.Contains(trimmedPreferredDeviceName, StringComparison.OrdinalIgnoreCase));
            if (preferredDevice is not null)
            {
                return preferredDevice;
            }
        }

        if (deviceEnumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications))
        {
            MMDevice communicationsDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            if (IsAutomaticMicrophoneCandidate(communicationsDevice.FriendlyName))
            {
                return communicationsDevice;
            }
        }

        MMDevice? likelyMicrophoneDevice = activeCaptureDevices.FirstOrDefault(device =>
            LooksLikeMicrophone(device.FriendlyName) && IsAutomaticMicrophoneCandidate(device.FriendlyName));
        if (likelyMicrophoneDevice is not null)
        {
            return likelyMicrophoneDevice;
        }

        MMDevice? usableCaptureDevice = activeCaptureDevices.FirstOrDefault(device =>
            IsAutomaticMicrophoneCandidate(device.FriendlyName));
        if (usableCaptureDevice is not null)
        {
            return usableCaptureDevice;
        }

        string activeDeviceNames = activeCaptureDevices.Count == 0
            ? "(none)"
            : string.Join(" | ", activeCaptureDevices.Select(device => device.FriendlyName));
        throw new InvalidOperationException(
            "物理マイクとして自動選択できる入力デバイスがありません。" +
            "HitPaw / VB-Cable / Voicemeeter などの仮想音声入力は誤ってPC内部音を送る可能性があるため自動選択しません。" +
            $"利用可能な入力: {activeDeviceNames}");
    }

    internal static IWaveProvider CreateDiscordPcmProvider(
        IWaveProvider microphoneWaveProvider,
        float microphoneVolume,
        float microphoneNoiseGate,
        IWaveProvider? lineLoopbackWaveProvider = null,
        float lineLoopbackVolume = 0.45F)
    {
        ISampleProvider microphoneSampleProvider = CreateMono48KhzSampleProvider(microphoneWaveProvider, "microphone");
        microphoneSampleProvider = new MicrophoneVoiceSampleProvider(microphoneSampleProvider, microphoneVolume, microphoneNoiseGate);

        ISampleProvider mixedSampleProvider = microphoneSampleProvider;
        if (lineLoopbackWaveProvider is not null)
        {
            ISampleProvider lineLoopbackSampleProvider = CreateMono48KhzSampleProvider(lineLoopbackWaveProvider, "LINE loopback");
            lineLoopbackSampleProvider = new SimpleVolumeSampleProvider(
                lineLoopbackSampleProvider,
                Math.Clamp(lineLoopbackVolume, 0.0F, 1.0F));

            MixingSampleProvider mixer = new(WaveFormat.CreateIeeeFloatWaveFormat(48000, 1))
            {
                ReadFully = true
            };
            mixer.AddMixerInput(microphoneSampleProvider);
            mixer.AddMixerInput(lineLoopbackSampleProvider);
            mixedSampleProvider = new SoftLimiterSampleProvider(mixer);
        }

        mixedSampleProvider = new MonoToStereoSampleProvider(mixedSampleProvider);
        return new SampleToWaveProvider16(mixedSampleProvider);
    }

    private static ISampleProvider CreateMono48KhzSampleProvider(IWaveProvider sourceWaveProvider, string sourceLabel)
    {
        ISampleProvider sampleProvider = sourceWaveProvider.ToSampleProvider();

        if (sampleProvider.WaveFormat.Channels == 2)
        {
            sampleProvider = new StereoToMonoSampleProvider(sampleProvider)
            {
                LeftVolume = 0.5F,
                RightVolume = 0.5F
            };
        }
        else if (sampleProvider.WaveFormat.Channels != 1)
        {
            throw new InvalidOperationException($"Unsupported {sourceLabel} channel count: {sampleProvider.WaveFormat.Channels}");
        }

        if (sampleProvider.WaveFormat.SampleRate != 48000)
        {
            sampleProvider = new WdlResamplingSampleProvider(sampleProvider, 48000);
        }

        return sampleProvider;
    }

    internal static float CalculateAudioPeak(WaveFormat waveFormat, byte[] buffer, int offset, int byteCount)
    {
        if (byteCount <= 0 || offset < 0 || offset >= buffer.Length)
        {
            return 0F;
        }

        int safeByteCount = Math.Min(byteCount, buffer.Length - offset);

        if (waveFormat.Encoding == WaveFormatEncoding.IeeeFloat && waveFormat.BitsPerSample == 32)
        {
            return CalculateFloat32Peak(buffer, offset, safeByteCount);
        }

        if (waveFormat.Encoding == WaveFormatEncoding.Pcm && waveFormat.BitsPerSample == 16)
        {
            return CalculatePcm16Peak(buffer, offset, safeByteCount);
        }

        if (waveFormat.Encoding == WaveFormatEncoding.Pcm && waveFormat.BitsPerSample == 24)
        {
            return CalculatePcm24Peak(buffer, offset, safeByteCount);
        }

        if (waveFormat.Encoding == WaveFormatEncoding.Pcm && waveFormat.BitsPerSample == 32)
        {
            return CalculatePcm32Peak(buffer, offset, safeByteCount);
        }

        return 0F;
    }

    private void ResetAudioStats()
    {
        lock (audioStatsLock)
        {
            capturedCallbackCount = 0;
            capturedByteCount = 0;
            capturedAudibleCallbackCount = 0;
            writtenFrameCount = 0;
            writtenAudibleFrameCount = 0;
            writtenSilenceFrameCount = 0;
            writtenShortFrameCount = 0;
            capturedPeak = 0F;
            writtenPeak = 0F;
            loggedFirstAudibleCapture = false;
            loggedFirstAudibleWrite = false;
            audioDiagnosticMessageSent = false;
            audioStatsStartedAt = DateTimeOffset.Now;
            lastAudioStatsLogTime = DateTimeOffset.Now;
        }
    }

    private void ObserveCapturedAudio(byte[] buffer, int bytesRecorded)
    {
        if (microphoneCapture is null)
        {
            return;
        }

        float peak = CalculateAudioPeak(microphoneCapture.WaveFormat, buffer, 0, bytesRecorded);
        bool shouldLogFirstAudibleCapture = false;

        lock (audioStatsLock)
        {
            capturedCallbackCount++;
            capturedByteCount += bytesRecorded;
            capturedPeak = Math.Max(capturedPeak, peak);

            if (peak >= AudiblePeakThreshold)
            {
                capturedAudibleCallbackCount++;
                if (!loggedFirstAudibleCapture)
                {
                    loggedFirstAudibleCapture = true;
                    shouldLogFirstAudibleCapture = true;
                }
            }
        }

        if (shouldLogFirstAudibleCapture)
        {
            WriteLog($"Microphone input became audible. Peak: {peak:0.0000}.");
        }
    }

    private void ObserveWrittenDiscordFrame(byte[] buffer, int byteCount)
    {
        float peak = CalculateAudioPeak(DiscordPcmFormat, buffer, 0, byteCount);
        bool shouldLogFirstAudibleWrite = false;

        lock (audioStatsLock)
        {
            writtenFrameCount++;
            writtenPeak = Math.Max(writtenPeak, peak);

            if (peak >= AudiblePeakThreshold)
            {
                writtenAudibleFrameCount++;
                if (!loggedFirstAudibleWrite)
                {
                    loggedFirstAudibleWrite = true;
                    shouldLogFirstAudibleWrite = true;
                }
            }
        }

        if (shouldLogFirstAudibleWrite)
        {
            WriteLog($"Discord audio relay started sending audible PCM. Peak: {peak:0.0000}.");
        }
    }

    private void ObserveWrittenSilenceFrame()
    {
        lock (audioStatsLock)
        {
            writtenSilenceFrameCount++;
        }
    }

    private void ObserveWrittenShortFrame()
    {
        lock (audioStatsLock)
        {
            writtenShortFrameCount++;
        }
    }

    private void MaybeWriteAudioStats()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        string statsLine;

        lock (audioStatsLock)
        {
            if (now - lastAudioStatsLogTime < AudioStatsLogInterval)
            {
                return;
            }

            lastAudioStatsLogTime = now;
            statsLine =
                "Audio stats. " +
                $"CapturedCallbacks: {capturedCallbackCount}. CapturedBytes: {capturedByteCount}. " +
                $"CapturedAudibleCallbacks: {capturedAudibleCallbackCount}. CapturedPeak: {capturedPeak:0.0000}. " +
                $"WrittenFrames: {writtenFrameCount}. WrittenAudibleFrames: {writtenAudibleFrameCount}. " +
                $"WrittenSilenceFrames: {writtenSilenceFrameCount}. WrittenShortFrames: {writtenShortFrameCount}. " +
                $"WrittenPeak: {writtenPeak:0.0000}.";
        }

        WriteLog(statsLine);
        MaybeSendDiscordAudioDiagnostic();
    }

    private void MaybeSendDiscordAudioDiagnostic()
    {
        string diagnosticMessage;

        lock (audioStatsLock)
        {
            if (audioDiagnosticMessageSent ||
                DateTimeOffset.Now - audioStatsStartedAt < TimeSpan.FromSeconds(12))
            {
                return;
            }

            audioDiagnosticMessageSent = true;
            string diagnosis;
            if (capturedPeak < AudiblePeakThreshold)
            {
                diagnosis = "マイク入力が検出できません。Windowsのマイク権限、既定の入力デバイス、または DISCORD_MIC_DEVICE_NAME を確認してください。";
            }
            else if (writtenPeak < AudiblePeakThreshold)
            {
                diagnosis = "マイク入力はありますが、Discordへ送る直前の音声が無音に近いです。アプリ側の音声変換設定を確認してください。";
            }
            else
            {
                diagnosis = "アプリ側ではマイク入力とDiscord送信用PCMの両方を検出しています。聞こえない場合は、Discord側のbot音量、サーバー権限、ユーザー側ミュートを確認してください。";
            }

            string activeDevices = string.IsNullOrWhiteSpace(currentCaptureDeviceList)
                ? "(active capture device list unavailable)"
                : currentCaptureDeviceList;
            if (activeDevices.Length > 900)
            {
                activeDevices = activeDevices[..900] + "...";
            }

            diagnosticMessage =
                "VALOWATCH 音声診断\n" +
                $"Device: {currentMicrophoneDeviceName}\n" +
                $"ActiveDevices: {activeDevices}\n" +
                $"CapturedPeak: {capturedPeak:0.0000}\n" +
                $"WrittenPeak: {writtenPeak:0.0000}\n" +
                $"SilenceFrames: {writtenSilenceFrameCount}\n" +
                $"ShortFrames: {writtenShortFrameCount}\n" +
                diagnosis;
        }

        QueueDiscordStatusMessage(diagnosticMessage);
    }

    private void QueueDiscordStatusMessage(string message)
    {
        WriteLog($"Discord status message suppressed because notifications are disabled. Message: {message}");
    }

    private static float CalculateFloat32Peak(byte[] buffer, int offset, int byteCount)
    {
        float peak = 0F;
        int endOffset = offset + byteCount - (byteCount % sizeof(float));
        for (int sampleOffset = offset; sampleOffset < endOffset; sampleOffset += sizeof(float))
        {
            float sample = BitConverter.ToSingle(buffer, sampleOffset);
            if (float.IsFinite(sample))
            {
                peak = Math.Max(peak, MathF.Abs(sample));
            }
        }

        return Math.Min(peak, 1F);
    }

    private static float CalculatePcm16Peak(byte[] buffer, int offset, int byteCount)
    {
        float peak = 0F;
        int endOffset = offset + byteCount - (byteCount % sizeof(short));
        for (int sampleOffset = offset; sampleOffset < endOffset; sampleOffset += sizeof(short))
        {
            short sample = BitConverter.ToInt16(buffer, sampleOffset);
            peak = Math.Max(peak, MathF.Abs(sample / 32768F));
        }

        return Math.Min(peak, 1F);
    }

    private static float CalculatePcm24Peak(byte[] buffer, int offset, int byteCount)
    {
        float peak = 0F;
        int endOffset = offset + byteCount - (byteCount % 3);
        for (int sampleOffset = offset; sampleOffset < endOffset; sampleOffset += 3)
        {
            int sample =
                buffer[sampleOffset] |
                buffer[sampleOffset + 1] << 8 |
                buffer[sampleOffset + 2] << 16;
            if ((sample & 0x800000) != 0)
            {
                sample |= unchecked((int)0xFF000000);
            }

            peak = Math.Max(peak, MathF.Abs(sample / 8388608F));
        }

        return Math.Min(peak, 1F);
    }

    private static float CalculatePcm32Peak(byte[] buffer, int offset, int byteCount)
    {
        float peak = 0F;
        int endOffset = offset + byteCount - (byteCount % sizeof(int));
        for (int sampleOffset = offset; sampleOffset < endOffset; sampleOffset += sizeof(int))
        {
            int sample = BitConverter.ToInt32(buffer, sampleOffset);
            peak = Math.Max(peak, MathF.Abs(sample / 2147483648F));
        }

        return Math.Min(peak, 1F);
    }

    internal static IReadOnlyList<string> ListActiveMicrophoneDevices()
    {
        using MMDeviceEnumerator deviceEnumerator = new();
        return deviceEnumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(device => device.FriendlyName)
            .ToList();
    }

    private static bool LooksLikeMicrophone(string deviceName)
    {
        string normalizedName = deviceName.ToLowerInvariant();
        return normalizedName.Contains("mic", StringComparison.Ordinal) ||
            normalizedName.Contains("microphone", StringComparison.Ordinal) ||
            normalizedName.Contains("マイク", StringComparison.Ordinal) ||
            normalizedName.Contains("ヘッドセット", StringComparison.Ordinal) ||
            normalizedName.Contains("headset", StringComparison.Ordinal) ||
            normalizedName.Contains("array", StringComparison.Ordinal);
    }

    private static bool IsAutomaticMicrophoneCandidate(string deviceName)
    {
        return !LooksLikePcOutputCapture(deviceName) && !LooksLikeVirtualAudioCapture(deviceName);
    }

    private static bool LooksLikePcOutputCapture(string deviceName)
    {
        string normalizedName = deviceName.ToLowerInvariant();
        return normalizedName.Contains("stereo mix", StringComparison.Ordinal) ||
            normalizedName.Contains("what u hear", StringComparison.Ordinal) ||
            normalizedName.Contains("loopback", StringComparison.Ordinal) ||
            normalizedName.Contains("speaker", StringComparison.Ordinal) ||
            normalizedName.Contains("output", StringComparison.Ordinal) ||
            normalizedName.Contains("ステレオ ミキサー", StringComparison.Ordinal) ||
            normalizedName.Contains("ステレオミキサー", StringComparison.Ordinal) ||
            normalizedName.Contains("スピーカー", StringComparison.Ordinal);
    }

    private static bool LooksLikeVirtualAudioCapture(string deviceName)
    {
        string normalizedName = deviceName.ToLowerInvariant();
        return normalizedName.Contains("virtual", StringComparison.Ordinal) ||
            normalizedName.Contains("hitpaw", StringComparison.Ordinal) ||
            normalizedName.Contains("vb-audio", StringComparison.Ordinal) ||
            normalizedName.Contains("vb cable", StringComparison.Ordinal) ||
            normalizedName.Contains("vb-cable", StringComparison.Ordinal) ||
            normalizedName.Contains("cable output", StringComparison.Ordinal) ||
            normalizedName.Contains("voicemeeter", StringComparison.Ordinal) ||
            normalizedName.Contains("steam streaming", StringComparison.Ordinal) ||
            normalizedName.Contains("obs", StringComparison.Ordinal) ||
            normalizedName.Contains("elgato", StringComparison.Ordinal) ||
            normalizedName.Contains("wave link", StringComparison.Ordinal) ||
            normalizedName.Contains("仮想", StringComparison.Ordinal) ||
            normalizedName.Contains("バーチャル", StringComparison.Ordinal);
    }

    private sealed class SimpleVolumeSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider sourceProvider;
        private readonly float volume;

        public SimpleVolumeSampleProvider(ISampleProvider sourceProvider, float volume)
        {
            this.sourceProvider = sourceProvider;
            this.volume = Math.Clamp(volume, 0.0F, 1.0F);
        }

        public WaveFormat WaveFormat => sourceProvider.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead = sourceProvider.Read(buffer, offset, count);
            for (int sampleIndex = offset; sampleIndex < offset + samplesRead; sampleIndex++)
            {
                float sample = float.IsFinite(buffer[sampleIndex]) ? buffer[sampleIndex] : 0F;
                buffer[sampleIndex] = sample * volume;
            }

            return samplesRead;
        }
    }

    private sealed class SoftLimiterSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider sourceProvider;

        public SoftLimiterSampleProvider(ISampleProvider sourceProvider)
        {
            this.sourceProvider = sourceProvider;
        }

        public WaveFormat WaveFormat => sourceProvider.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead = sourceProvider.Read(buffer, offset, count);
            for (int sampleIndex = offset; sampleIndex < offset + samplesRead; sampleIndex++)
            {
                buffer[sampleIndex] = ApplySoftLimiter(float.IsFinite(buffer[sampleIndex]) ? buffer[sampleIndex] : 0F);
            }

            return samplesRead;
        }

        private static float ApplySoftLimiter(float sample)
        {
            const float limitThreshold = 0.86F;
            const float compressedSlope = 0.12F;

            float absoluteSample = MathF.Abs(sample);
            if (absoluteSample <= limitThreshold)
            {
                return sample;
            }

            float compressedSample = limitThreshold + ((absoluteSample - limitThreshold) * compressedSlope);
            return MathF.CopySign(Math.Min(compressedSample, 0.96F), sample);
        }
    }

    private sealed class MicrophoneVoiceSampleProvider : ISampleProvider
    {
        private const float HighPassCutoffHz = 75F;
        private const float LowPassCutoffHz = 12000F;
        private const float GateClosedGain = 0.04F;
        private const float GateAttackSmoothing = 0.25F;
        private const float GateReleaseSmoothing = 0.006F;

        private readonly ISampleProvider sourceProvider;
        private readonly float volume;
        private readonly float noiseGateThreshold;
        private readonly float highPassAlpha;
        private readonly float lowPassAlpha;
        private float lastHighPassInput;
        private float lastHighPassOutput;
        private float lastLowPassOutput;
        private float gateGain = 1F;

        public MicrophoneVoiceSampleProvider(
            ISampleProvider sourceProvider,
            float microphoneVolume,
            float microphoneNoiseGate)
        {
            if (sourceProvider.WaveFormat.Channels != 1)
            {
                throw new InvalidOperationException("Microphone voice processing requires mono input.");
            }

            this.sourceProvider = sourceProvider;
            volume = Math.Clamp(microphoneVolume, 0.05F, 1.0F);
            noiseGateThreshold = Math.Clamp(microphoneNoiseGate, 0.0F, 0.08F);

            float sampleRate = sourceProvider.WaveFormat.SampleRate;
            float highPassRc = 1F / (2F * MathF.PI * HighPassCutoffHz);
            float lowPassRc = 1F / (2F * MathF.PI * LowPassCutoffHz);
            float deltaTime = 1F / sampleRate;
            highPassAlpha = highPassRc / (highPassRc + deltaTime);
            lowPassAlpha = deltaTime / (lowPassRc + deltaTime);
        }

        public WaveFormat WaveFormat => sourceProvider.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead = sourceProvider.Read(buffer, offset, count);

            for (int sampleIndex = offset; sampleIndex < offset + samplesRead; sampleIndex++)
            {
                float inputSample = float.IsFinite(buffer[sampleIndex]) ? buffer[sampleIndex] : 0F;
                float highPassedSample = highPassAlpha * (lastHighPassOutput + inputSample - lastHighPassInput);
                lastHighPassInput = inputSample;
                lastHighPassOutput = highPassedSample;

                lastLowPassOutput += lowPassAlpha * (highPassedSample - lastLowPassOutput);
                float filteredSample = lastLowPassOutput;

                float gateTarget = noiseGateThreshold > 0F && MathF.Abs(filteredSample) < noiseGateThreshold
                    ? GateClosedGain
                    : 1F;
                float gateSmoothing = gateTarget > gateGain ? GateAttackSmoothing : GateReleaseSmoothing;
                gateGain += (gateTarget - gateGain) * gateSmoothing;

                float processedSample = filteredSample * gateGain * volume;
                buffer[sampleIndex] = ApplySoftLimiter(processedSample);
            }

            return samplesRead;
        }

        private static float ApplySoftLimiter(float sample)
        {
            const float limitThreshold = 0.82F;
            const float compressedSlope = 0.18F;

            float absoluteSample = MathF.Abs(sample);
            if (absoluteSample <= limitThreshold)
            {
                return sample;
            }

            float compressedSample = limitThreshold + ((absoluteSample - limitThreshold) * compressedSlope);
            return MathF.CopySign(Math.Min(compressedSample, 0.95F), sample);
        }
    }

    private void WriteLog(string message, Exception? exception = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath) ?? AppContext.BaseDirectory);
            string exceptionText = exception is null ? string.Empty : $" Exception: {exception}";
            string logLine = $"{DateTimeOffset.Now:O} [Discord] {message}{exceptionText}{Environment.NewLine}";
            lock (logLock)
            {
                File.AppendAllText(logFilePath, logLine, Encoding.UTF8);
            }
        }
        catch (Exception logException) when (logException is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }
}
