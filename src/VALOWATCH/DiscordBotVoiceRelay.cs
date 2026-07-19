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
    private static readonly TimeSpan RelayShutdownTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AudioStatsLogInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RuntimeLogInitialDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RuntimeLogInterval = TimeSpan.FromMinutes(5);
    private const int DiscordEmbedDescriptionLimit = 4096;
    private const int DiscordEmbedDescriptionSafetyMargin = 120;
    private static readonly TimeSpan DiscordNetworkWarningLogInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StartupNotificationCooldown = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MicrophoneHealthCheckInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MicrophoneCallbackTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan DiscordFrameWriteTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MicrophoneRecentActivityDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MicrophoneSilentCandidateDuration = TimeSpan.FromSeconds(30);
    private const float MicrophoneActivityPeakThreshold = 0.0002F;
    private static readonly TimeSpan DiscordGatewayReadyTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DiscordVoiceConnectTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DiscordShutdownTimeout = TimeSpan.FromSeconds(5);
    private const bool DiscordVoiceDaveEncryptionEnabled = true;
    internal const string DiscordVoiceChannelConnectStartupStage = "Discord voice channel connect";
    private const string DiscordAudioCommandName = "valowatch-discord-audio";
    private const string DiscordAudioCommandEnabledOptionName = "enabled";
    private const string StartCommandName = "start";
    private const string RunningAppCommandName = "app";
    private const string SelfDiagnosticsCommandName = "valowatch-diagnostics";
    private const string SelfDiagnosticsDownloadOptionName = "download";

    internal static WaveFormat DiscordPcmWaveFormat => DiscordPcmFormat;

    private readonly DiscordBotSettingsStore settingsStore;
    private readonly AppPaths appPaths;
    private readonly string logFilePath;
    private readonly object logLock = new();
    private readonly object stateLock = new();
    private readonly object audioStatsLock = new();
    private readonly object microphoneCaptureLock = new();
    private readonly object discordNetworkWarningLock = new();
    private readonly SemaphoreSlim lifecycleSemaphore = new(1, 1);
    private readonly SemaphoreSlim runtimeLogSemaphore = new(1, 1);

    private DiscordSocketClient? discordClient;
    private IAudioClient? audioClient;
    private SocketTextChannel? discordStatusTextChannel;
    private IMessageChannel? discordTranscriptionTextChannel;
    private WasapiCapture? microphoneCapture;
    private BufferedWaveProvider? bufferedWaveProvider;
    private LineProcessLoopbackWaveProvider? lineProcessLoopbackProvider;
    private LineProcessLoopbackWaveProvider? discordProcessLoopbackProvider;
    private IWaveProvider? discordPcmProvider;
    private AudioOutStream? discordStream;
    private AudioTranscriptionWorker? audioTranscriptionWorker;
    private CancellationTokenSource? relayCancellationTokenSource;
    private Task? relayTask;
    private Task? microphoneHealthTask;
    private SwitchingSampleProvider? microphoneSourceSwitcher;
    private SwitchingSampleProvider? discordAudioSourceSwitcher;
    private IReadOnlyList<MicrophoneDeviceCandidate> microphoneCandidates = [];
    private int currentMicrophoneCandidateIndex = -1;
    private bool microphoneCaptureFaulted;
    private long microphoneAttemptCallbackCount;
    private float microphoneAttemptPeak;
    private DateTimeOffset microphoneAttemptStartedAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastMicrophoneCallbackAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastMicrophoneActivityAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastDiscordFrameWrittenAt = DateTimeOffset.MinValue;
    private bool microphoneSignalLocked;
    private int discordRecoveryScheduled;
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
    private bool valorantOpenedNotificationSentForCurrentSession;
    private bool microphoneNotificationSentForCurrentSession;
    private string currentMicrophoneDeviceName = string.Empty;
    private string lastNotifiedMicrophoneDeviceName = string.Empty;
    private bool versionNotificationSent;
    private DateTimeOffset lastValorantOpenedMessageSentAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset lastMicrophoneMessageSentAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset lastLineOpenedMessageSentAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset lastAudioDiagnosticMessageSentAtUtc = DateTimeOffset.MinValue;
    private CancellationTokenSource? runtimeLogCancellationTokenSource;
    private Task? runtimeLogTask;
    private string currentCaptureDeviceList = string.Empty;
    private string currentLineLoopbackSourceName = string.Empty;
    private string currentDiscordLoopbackSourceName = string.Empty;
    private string[] currentDiscordAudioProcessNames = [];
    private float currentDiscordAudioVolume;
    private bool discordProcessAudioRuntimeEnabled;
    private bool discordAudioCommandEnabled;
    private ulong currentMonitoredDiscordUserId;
    private ulong currentVoiceGuildId;
    private string currentVoiceGuildName = string.Empty;
    private string currentVoiceChannelName = string.Empty;
    private string currentDiscordConversationGuildName = string.Empty;
    private string currentDiscordConversationChannelName = string.Empty;
    private string lastDiscordVoiceContextNotificationKey = string.Empty;
    private DateTimeOffset audioStatsStartedAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastAudioStatsLogTime = DateTimeOffset.MinValue;
    private DateTimeOffset lastDiscordNetworkWarningLoggedAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastRunningApplicationSnapshotSentAtUtc = DateTimeOffset.MinValue;
    private int suppressedDiscordNetworkWarningCount;

    public DiscordBotVoiceRelay(DiscordBotSettingsStore settingsStore, AppPaths appPaths)
    {
        this.settingsStore = settingsStore;
        this.appPaths = appPaths;
        logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");
        settingsStore.EnsureSampleConfig();
    }

    public string StatusText { get; private set; } = "Discord idle";

    public bool HasConfig => settingsStore.HasConfig;

    public bool IsOnline { get; private set; }

    public bool IsRunning { get; private set; }

    public async Task StartPresenceAsync()
    {
        await lifecycleSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            await StartPresenceCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            lifecycleSemaphore.Release();
        }
    }

    public async Task StartForValorantAsync()
    {
        await StartForVoiceActivityAsync(valorantDetected: true, lineDetected: false).ConfigureAwait(false);
    }

    public async Task StartForVoiceActivityAsync(bool valorantDetected, bool lineDetected)
    {
        await lifecycleSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            await StartForVoiceActivityCoreAsync(valorantDetected, lineDetected).ConfigureAwait(false);
        }
        finally
        {
            lifecycleSemaphore.Release();
        }
    }

    private async Task StartPresenceCoreAsync()
    {
        lock (stateLock)
        {
            if (IsOnline && discordClient is not null)
            {
                return;
            }

            stopRequested = false;
            StatusText = settingsStore.HasConfig ? "Discord presence connecting" : "Discord config missing";
        }

        WriteLog("PC startup presence requested. Starting Discord gateway without voice.");

        DiscordBotSettings? settings = LoadUsableSettings(out string statusText);
        if (settings is null)
        {
            StatusText = statusText;
            return;
        }

        try
        {
            DiscordGatewayContext gatewayContext = await EnsureDiscordGatewayReadyAsync(settings)
                .ConfigureAwait(false);
            ConfigureDiscordUserVoiceTracking(settings, gatewayContext.Guild);
            await EnsureDiscordAudioCommandAsync(gatewayContext.Guild, settings).ConfigureAwait(false);
            await EnsureStartCommandAsync(gatewayContext.Guild).ConfigureAwait(false);
            await EnsureRunningAppCommandAsync(gatewayContext.Guild).ConfigureAwait(false);
            await EnsureSelfDiagnosticsCommandAsync(gatewayContext.Guild).ConfigureAwait(false);
            await SendObservedDiscordVoiceContextIfNeededAsync(gatewayContext.Client).ConfigureAwait(false);
            await SendPendingUpdateNotificationAsync().ConfigureAwait(false);

            lock (stateLock)
            {
                IsOnline = true;
                stopRequested = false;
                StatusText = "Discord online idle";
            }

            WriteLog("Discord gateway is online for PC startup presence.");
        }
        catch (TimeoutException exception)
        {
            WriteLog("Discord presence startup timed out. Stopping Discord client before retry.", exception);
            await StopCoreAsync(resetValorantNotificationSession: false).ConfigureAwait(false);
            StatusText = "Discord presence timed out";
        }
        catch (Exception exception)
        {
            WriteLog("Discord presence startup failed. Stopping Discord client before retry.", exception);
            await StopCoreAsync(resetValorantNotificationSession: false).ConfigureAwait(false);
            StatusText = $"Discord presence failed: {exception.Message}";
        }
    }

    private async Task StartForVoiceActivityCoreAsync(bool valorantDetected, bool lineDetected)
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

        string triggerLabel = valorantDetected
            ? "VALORANT"
            : lineDetected
                ? "LINE"
                : "voice activity";
        WriteLog($"{triggerLabel} trigger received. Starting Discord automation.");

        DiscordBotSettings? settings = LoadUsableSettings(out string configStatusText);
        if (settings is null)
        {
            StatusText = configStatusText;
            return;
        }

        WriteLog(
            $"Discord settings loaded. Guild: {settings.GuildId}. Voice: {settings.VoiceChannelId}. " +
            $"Text: {settings.TextChannelId}. StreamMic: {settings.StreamMicrophoneAudio}. " +
            $"MicDevice: {settings.MicrophoneDeviceName}. Volume: {settings.MicrophoneVolume:0.00}. " +
            $"NoiseGate: {settings.MicrophoneNoiseGate:0.000}. " +
            $"StreamLineAudio: {settings.StreamLineAudioWhenRunning}. " +
            $"LineProcesses: {string.Join(",", settings.LineAudioProcessNames)}. " +
            $"StreamDiscordAudio: {settings.StreamDiscordAudioWhenRunning}. " +
            $"DiscordAudioProcesses: {string.Join(",", settings.DiscordAudioProcessNames)}. " +
            $"DiscordAudioCommand: {settings.DiscordAudioCommandEnabled}. " +
            $"Transcription: {settings.TranscriptionEnabled}. " +
            $"TranscriptionEngine: {settings.TranscriptionEngine}. " +
            $"TranscriptionChunkSeconds: {settings.TranscriptionChunkSeconds}.");

        string startupStage = "initializing Discord client";
        try
        {
            startupStage = "Discord gateway ready";
            DiscordGatewayContext gatewayContext = await EnsureDiscordGatewayReadyAsync(settings)
                .ConfigureAwait(false);
            SocketGuild guild = gatewayContext.Guild;
            SocketVoiceChannel voiceChannel = guild.GetVoiceChannel(settings.VoiceChannelId)
                ?? throw new InvalidOperationException("指定されたDiscord VCが見つかりません。VoiceChannelIdを確認してください。");
            ConfigureDiscordConversationState(settings, guild, voiceChannel);
            discordStatusTextChannel = settings.TextChannelId == 0
                ? null
                : guild.GetTextChannel(settings.TextChannelId);
            discordTranscriptionTextChannel = ResolveTranscriptionTextChannel(
                guild,
                voiceChannel,
                discordStatusTextChannel,
                settings);

            if (!TryEnsureVoiceNativeDependencies(out string nativeDependencyStatus))
            {
                StatusText = nativeDependencyStatus;
                WriteLog(nativeDependencyStatus);
                await SendRequestedDiscordNotificationAsync(
                    $"VALOWATCH 音声DLL確認失敗: {nativeDependencyStatus}").ConfigureAwait(false);
                await SendRuntimeLogUpdatesAsync().ConfigureAwait(false);
                await StopCoreAsync(
                        resetValorantNotificationSession: false,
                        keepDiscordGatewayOnline: true)
                    .ConfigureAwait(false);
                return;
            }

            EnsureVoiceChannelPermissions(guild, voiceChannel);
            await EnsureDiscordAudioCommandAsync(guild, settings).ConfigureAwait(false);
            await EnsureStartCommandAsync(guild).ConfigureAwait(false);
            await EnsureRunningAppCommandAsync(guild).ConfigureAwait(false);
            await EnsureSelfDiagnosticsCommandAsync(guild).ConfigureAwait(false);

            WriteLog($"Connecting to Discord voice channel {voiceChannel.Id}.");
            startupStage = DiscordVoiceChannelConnectStartupStage;
            audioClient = await ConnectVoiceChannelWithTimeoutAsync(voiceChannel).ConfigureAwait(false);
            WriteLog($"Joined Discord voice channel {voiceChannel.Id}. SelfDeaf: true. SelfMute: false.");
            await SendObservedDiscordVoiceContextIfNeededAsync(gatewayContext.Client).ConfigureAwait(false);
            if (valorantDetected)
            {
                await SendValorantOpenedNotificationIfNeededAsync(settings).ConfigureAwait(false);
            }

            await SendVersionNotificationIfNeededAsync().ConfigureAwait(false);
            await SendPendingUpdateNotificationAsync().ConfigureAwait(false);

            lock (stateLock)
            {
                IsRunning = true;
                StatusText = FormatRunningStatus("Discord joined VC");
            }

            _ = Task.Run(SendRuntimeLogUpdatesAsync);
            StartRuntimeLogUpdates();

            bool audioRelayStarted = false;

            if (settings.StreamMicrophoneAudio)
            {
                try
                {
                    StartMicrophoneAudioRelay(settings);
                    audioRelayStarted = true;
                    if (await SendMicrophoneNotificationIfNeededAsync(currentMicrophoneDeviceName)
                        .ConfigureAwait(false))
                    {
                        lastNotifiedMicrophoneDeviceName = currentMicrophoneDeviceName;
                    }
                }
                catch (Exception audioException)
                {
                    WriteLog("Discord voice channel joined, but microphone audio relay could not start.", audioException);
                    await SendRequestedDiscordNotificationAsync(
                        $"VALOWATCH 音声開始失敗: {audioException.Message}").ConfigureAwait(false);
                    await SendRuntimeLogUpdatesAsync().ConfigureAwait(false);
                    await StopAudioRelayComponentsAsync().ConfigureAwait(false);
                    StatusText = FormatRunningStatus("Discord audio recovery pending", audioException.Message);
                    return;
                }
            }

            lock (stateLock)
            {
                IsRunning = true;
                StatusText = FormatRunningStatus(audioRelayStarted ? "Discord mic live" : "Discord joined VC");
            }
        }
        catch (TimeoutException exception)
        {
            WriteLog($"Discord startup timed out during {startupStage}. Stopping Discord client before retry.", exception);
            await StopCoreAsync(
                    resetValorantNotificationSession: false,
                    keepDiscordGatewayOnline: ShouldKeepGatewayOnlineAfterStartupTimeout(
                        startupStage,
                        IsOnline,
                        discordClient is not null))
                .ConfigureAwait(false);
            StatusText = $"Discord timed out: {startupStage}";
        }
        catch (Exception exception)
        {
            WriteLog("Discord startup failed. Stopping Discord client before retry.", exception);
            await StopCoreAsync(
                    resetValorantNotificationSession: false,
                    keepDiscordGatewayOnline: IsOnline && discordClient is not null)
                .ConfigureAwait(false);
            StatusText = $"Discord failed: {exception.Message}";
        }
    }

    public async Task StopAsync()
    {
        await lifecycleSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync(resetValorantNotificationSession: true).ConfigureAwait(false);
        }
        finally
        {
            lifecycleSemaphore.Release();
        }
    }

    public async Task StopForValorantAsync()
    {
        await lifecycleSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync(
                    resetValorantNotificationSession: true,
                    keepDiscordGatewayOnline: true)
                .ConfigureAwait(false);
        }
        finally
        {
            lifecycleSemaphore.Release();
        }
    }

    private DiscordBotSettings? LoadUsableSettings(out string statusText)
    {
        try
        {
            DiscordBotSettings? settings = settingsStore.Load(out statusText);
            if (settings is null)
            {
                WriteLog($"Discord settings are not usable: {statusText}");
            }

            return settings;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            statusText = $"Discord config failed: {exception.Message}";
            WriteLog("Discord settings failed to load.", exception);
            return null;
        }
    }

    private async Task<DiscordGatewayContext> EnsureDiscordGatewayReadyAsync(DiscordBotSettings settings)
    {
        DiscordSocketClient? existingClient = discordClient;
        if (existingClient is not null)
        {
            if (existingClient.ConnectionState != ConnectionState.Disconnected)
            {
                await existingClient.SetStatusAsync(UserStatus.Online).ConfigureAwait(false);
                lock (stateLock)
                {
                    IsOnline = true;
                    stopRequested = false;
                }

                return ResolveDiscordGatewayContext(existingClient, settings);
            }

            WriteLog("Discord gateway client was disconnected; recreating the gateway session.");
            DetachClientEvents(existingClient);
            await CompleteShutdownOperationAsync(
                () => existingClient.DisposeAsync().AsTask(),
                "Discord disconnected client dispose").ConfigureAwait(false);
            discordClient = null;
            IsOnline = false;
        }

        DiscordSocketClient client = CreateClient();
        WriteRuntimeDiagnostic(client);
        AttachClientEvents(client);
        discordClient = client;

        TaskCompletionSource readyCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task OnReadyAsync()
        {
            readyCompletionSource.TrySetResult();
            return Task.CompletedTask;
        }

        client.Ready += OnReadyAsync;
        try
        {
            await client.LoginAsync(TokenType.Bot, settings.BotToken).ConfigureAwait(false);
            await client.StartAsync().ConfigureAwait(false);
            await readyCompletionSource.Task.WaitAsync(DiscordGatewayReadyTimeout).ConfigureAwait(false);
            await client.SetStatusAsync(UserStatus.Online).ConfigureAwait(false);
        }
        finally
        {
            client.Ready -= OnReadyAsync;
        }

        lock (stateLock)
        {
            IsOnline = true;
            stopRequested = false;
        }

        WriteLog("Discord gateway is ready and bot status is online.");
        return ResolveDiscordGatewayContext(client, settings);
    }

    private DiscordGatewayContext ResolveDiscordGatewayContext(
        DiscordSocketClient client,
        DiscordBotSettings settings)
    {
        SocketGuild guild = client.GetGuild(settings.GuildId)
            ?? throw new InvalidOperationException("指定されたDiscordサーバーが見つかりません。Botがサーバーに参加しているか確認してください。");

        discordStatusTextChannel = settings.TextChannelId == 0
            ? null
            : guild.GetTextChannel(settings.TextChannelId);
        return new DiscordGatewayContext(client, guild);
    }

    private async Task<IAudioClient> ConnectVoiceChannelWithTimeoutAsync(SocketVoiceChannel voiceChannel)
    {
        Task<IAudioClient> connectTask = voiceChannel.ConnectAsync(selfDeaf: true, selfMute: false);
        Task timeoutTask = Task.Delay(DiscordVoiceConnectTimeout);
        Task completedTask = await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false);
        if (ReferenceEquals(completedTask, connectTask))
        {
            return await connectTask.ConfigureAwait(false);
        }

        _ = CleanupLateVoiceConnectAsync(connectTask, voiceChannel.Id);
        throw new TimeoutException(
            $"Discord voice channel connect did not complete within {DiscordVoiceConnectTimeout.TotalSeconds:0} seconds.");
    }

    private async Task CleanupLateVoiceConnectAsync(Task<IAudioClient> connectTask, ulong voiceChannelId)
    {
        try
        {
            IAudioClient lateAudioClient = await connectTask.ConfigureAwait(false);
            WriteLog(
                "Discord voice channel connect completed after the startup timeout; " +
                $"stopping stale voice client for channel {voiceChannelId}.");
            await CompleteShutdownOperationAsync(
                    () => lateAudioClient.StopAsync(),
                    "late Discord voice client stop")
                .ConfigureAwait(false);
            lateAudioClient.Dispose();
        }
        catch (Exception exception)
        {
            WriteLog(
                "Discord voice channel connect finished after timeout with no reusable voice client.",
                exception);
        }
    }

    internal static bool ShouldKeepGatewayOnlineAfterStartupTimeout(
        string startupStage,
        bool isOnline,
        bool hasDiscordClient)
    {
        return !string.Equals(
                startupStage,
                DiscordVoiceChannelConnectStartupStage,
                StringComparison.Ordinal) &&
            isOnline &&
            hasDiscordClient;
    }

    public async Task<bool> NotifyLineOpenedAsync(string message)
    {
        string notificationMessage = string.IsNullOrWhiteSpace(message)
            ? "LINEを開いた"
            : message.Trim();

        lock (stateLock)
        {
            if (stopRequested || !IsOnline)
            {
                WriteLog("LINE opened notification delayed because Discord is not running.");
                return false;
            }
        }

        if (discordStatusTextChannel is null)
        {
            WriteLog("LINE opened notification delayed because the text channel is not ready.");
            return false;
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        lock (stateLock)
        {
            if (nowUtc - lastLineOpenedMessageSentAtUtc < StartupNotificationCooldown)
            {
                WriteLog("Skipped duplicate LINE opened notification during reconnect cooldown.");
                return true;
            }
        }

        if (!await SendRequestedDiscordNotificationAsync(notificationMessage).ConfigureAwait(false))
        {
            return false;
        }

        lock (stateLock)
        {
            lastLineOpenedMessageSentAtUtc = DateTimeOffset.UtcNow;
        }

        return true;
    }

    private async Task StopAudioRelayComponentsAsync()
    {
        CancellationTokenSource? cancellationTokenSource;
        Task? activeRelayTask;
        Task? activeMicrophoneHealthTask;

        lock (stateLock)
        {
            cancellationTokenSource = relayCancellationTokenSource;
            activeRelayTask = relayTask;
            activeMicrophoneHealthTask = microphoneHealthTask;
            relayCancellationTokenSource = null;
            relayTask = null;
            microphoneHealthTask = null;
        }

        if (cancellationTokenSource is not null)
        {
            await cancellationTokenSource.CancelAsync().ConfigureAwait(false);
        }

        if (activeRelayTask is not null)
        {
            try
            {
                await activeRelayTask.WaitAsync(RelayShutdownTimeout).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (TimeoutException exception)
            {
                WriteLog("Audio relay cleanup timed out; disposing the Discord stream to unblock it.", exception);
                discordStream?.Dispose();
            }
            catch (Exception exception)
            {
                WriteLog("Audio relay cleanup failed; continuing without stopping Discord presence.", exception);
            }
        }

        if (activeMicrophoneHealthTask is not null)
        {
            try
            {
                await activeMicrophoneHealthTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                WriteLog("Microphone health cleanup failed; continuing without stopping Discord presence.", exception);
            }
        }

        DisposeAudioObjects();
        await StopAudioTranscriptionWorkerAsync().ConfigureAwait(false);
        cancellationTokenSource?.Dispose();
    }

    private async Task StopCoreAsync(
        bool resetValorantNotificationSession,
        bool keepDiscordGatewayOnline = false)
    {
        CancellationTokenSource? cancellationTokenSource;
        Task? activeRelayTask;
        Task? activeMicrophoneHealthTask;

        lock (stateLock)
        {
            cancellationTokenSource = relayCancellationTokenSource;
            activeRelayTask = relayTask;
            activeMicrophoneHealthTask = microphoneHealthTask;
            relayCancellationTokenSource = null;
            relayTask = null;
            microphoneHealthTask = null;
            IsRunning = false;
            stopRequested = !keepDiscordGatewayOnline;
            StatusText = keepDiscordGatewayOnline
                ? "Discord online idle"
                : settingsStore.HasConfig ? "Discord idle" : "Discord config missing";
            if (resetValorantNotificationSession)
            {
                valorantOpenedNotificationSentForCurrentSession = false;
                microphoneNotificationSentForCurrentSession = false;
                lastDiscordVoiceContextNotificationKey = string.Empty;
                lastNotifiedMicrophoneDeviceName = string.Empty;
                lastValorantOpenedMessageSentAtUtc = DateTimeOffset.MinValue;
                lastMicrophoneMessageSentAtUtc = DateTimeOffset.MinValue;
            }
        }

        if (cancellationTokenSource is not null)
        {
            await cancellationTokenSource.CancelAsync().ConfigureAwait(false);
        }

        if (activeRelayTask is not null)
        {
            try
            {
                await activeRelayTask.WaitAsync(RelayShutdownTimeout).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (TimeoutException)
            {
                WriteLog("Audio relay did not stop after cancellation; disposing the Discord stream to unblock it.");
                discordStream?.Dispose();
                try
                {
                    await activeRelayTask.WaitAsync(RelayShutdownTimeout).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
                {
                    WriteLog("Audio relay did not confirm shutdown after stream dispose; cleanup will continue.");
                }
                catch (Exception exception)
                {
                    WriteLog("Audio relay remained unavailable during forced shutdown; cleanup will continue.", exception);
                }
            }
            catch (Exception exception)
            {
                WriteLog("Audio relay task ended while stopping.", exception);
            }
        }

        if (activeMicrophoneHealthTask is not null)
        {
            try
            {
                await activeMicrophoneHealthTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                WriteLog("Microphone health monitor ended while stopping.", exception);
            }
        }

        DisposeAudioObjects();
        await StopAudioTranscriptionWorkerAsync().ConfigureAwait(false);

        if (audioClient is not null)
        {
            await CompleteShutdownOperationAsync(
                () => audioClient.StopAsync(),
                "Discord voice client stop").ConfigureAwait(false);
            audioClient.Dispose();
            audioClient = null;
        }

        await StopRuntimeLogUpdatesAsync().ConfigureAwait(false);
        await SendRuntimeLogUpdatesAsync().ConfigureAwait(false);

        if (keepDiscordGatewayOnline)
        {
            lock (stateLock)
            {
                IsOnline = discordClient is not null &&
                    discordClient.ConnectionState != ConnectionState.Disconnected;
                stopRequested = false;
            }

            return;
        }

        if (discordClient is not null)
        {
            DetachClientEvents(discordClient);
            await CompleteShutdownOperationAsync(
                () => discordClient.LogoutAsync(),
                "Discord client logout").ConfigureAwait(false);
            await CompleteShutdownOperationAsync(
                () => discordClient.StopAsync(),
                "Discord client stop").ConfigureAwait(false);
            await CompleteShutdownOperationAsync(
                () => discordClient.DisposeAsync().AsTask(),
                "Discord client dispose").ConfigureAwait(false);
            discordClient = null;
            discordStatusTextChannel = null;
            discordTranscriptionTextChannel = null;
        }

        lock (stateLock)
        {
            IsOnline = false;
        }
    }

    private async Task CompleteShutdownOperationAsync(Func<Task> shutdownOperation, string operationName)
    {
        Task shutdownTask;
        try
        {
            shutdownTask = shutdownOperation();
        }
        catch (Exception exception)
        {
            WriteLog($"{operationName} could not start; cleanup will continue.", exception);
            return;
        }

        try
        {
            await shutdownTask.WaitAsync(DiscordShutdownTimeout).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException exception)
        {
            WriteLog($"{operationName} timed out; cleanup will continue.", exception);
            _ = ObserveLateShutdownFaultAsync(shutdownTask, operationName);
        }
        catch (Exception exception)
        {
            WriteLog($"{operationName} failed; cleanup will continue.", exception);
        }
    }

    private async Task ObserveLateShutdownFaultAsync(Task shutdownTask, string operationName)
    {
        try
        {
            await shutdownTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            WriteLog($"{operationName} finished later with an error.", exception);
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
        client.SlashCommandExecuted += OnSlashCommandExecutedAsync;
        client.UserVoiceStateUpdated += OnDiscordUserVoiceStateUpdatedAsync;
    }

    private void DetachClientEvents(DiscordSocketClient client)
    {
        client.Log -= OnDiscordLogAsync;
        client.Connected -= OnDiscordConnectedAsync;
        client.Disconnected -= OnDiscordDisconnectedAsync;
        client.SlashCommandExecuted -= OnSlashCommandExecutedAsync;
        client.UserVoiceStateUpdated -= OnDiscordUserVoiceStateUpdatedAsync;
    }

    private async Task OnDiscordUserVoiceStateUpdatedAsync(
        SocketUser user,
        SocketVoiceState before,
        SocketVoiceState after)
    {
        try
        {
            if (!ShouldTrackDiscordVoiceStateUser(currentMonitoredDiscordUserId, user.Id, user.IsBot))
            {
                return;
            }

            SocketVoiceChannel? joinedVoiceChannel = after.VoiceChannel;
            SocketVoiceChannel? previousVoiceChannel = before.VoiceChannel;
            if (joinedVoiceChannel is null)
            {
                ClearObservedDiscordVoiceContextIfMatching(previousVoiceChannel, user.Id);
                return;
            }

            await SendDiscordVoiceContextNotificationIfNeededAsync(joinedVoiceChannel, user.Id)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Discord.Net.HttpException or HttpRequestException or TaskCanceledException)
        {
            WriteLog("Discord voice state update handling failed.", exception);
        }
    }

    private async Task OnSlashCommandExecutedAsync(SocketSlashCommand command)
    {
        WriteLog($"Slash command received: /{command.Data.Name}. User: {command.User.Id}.");

        if (string.Equals(command.Data.Name, StartCommandName, StringComparison.OrdinalIgnoreCase))
        {
            await HandleStartSlashCommandAsync(command).ConfigureAwait(false);
            return;
        }

        if (string.Equals(command.Data.Name, RunningAppCommandName, StringComparison.OrdinalIgnoreCase))
        {
            await HandleRunningAppSlashCommandAsync(command).ConfigureAwait(false);
            return;
        }

        if (string.Equals(command.Data.Name, SelfDiagnosticsCommandName, StringComparison.OrdinalIgnoreCase))
        {
            await HandleSelfDiagnosticsSlashCommandAsync(command).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(command.Data.Name, DiscordAudioCommandName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            if (!discordAudioCommandEnabled)
            {
                await command
                    .RespondAsync("VALOWATCHのDiscord音声中継コマンドは無効です。", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            if (command.User is not SocketGuildUser guildUser ||
                (currentVoiceGuildId != 0 && guildUser.Guild.Id != currentVoiceGuildId))
            {
                await command
                    .RespondAsync("このサーバーではVALOWATCHのDiscord音声中継を操作できません。", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            if (!guildUser.GuildPermissions.Administrator && !guildUser.GuildPermissions.ManageGuild)
            {
                await command
                    .RespondAsync("VALOWATCHのDiscord音声中継を切り替えるにはサーバー管理権限が必要です。", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            object? optionValue = command.Data.Options
                .FirstOrDefault(option => string.Equals(
                    option.Name,
                    DiscordAudioCommandEnabledOptionName,
                    StringComparison.OrdinalIgnoreCase))
                ?.Value;
            if (optionValue is not bool enabled)
            {
                await command
                    .RespondAsync("enabled に true か false を指定してください。", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            bool stateChanged = SetDiscordProcessAudioEnabled(enabled, "Discord slash command");
            bool enabledAfterCommand = discordProcessLoopbackProvider is not null;
            string statusText = enabledAfterCommand ? "ON" : "OFF";
            string changedText = stateChanged
                ? "切り替えました"
                : enabled == enabledAfterCommand
                    ? "すでにその状態です"
                    : "切り替えできませんでした";
            await command
                .RespondAsync(
                    $"VALOWATCH Discord音声中継: {statusText} ({changedText})",
                    ephemeral: true)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or TaskCanceledException or Discord.Net.HttpException)
        {
            WriteLog("Discord audio slash command handling failed.", exception);
            try
            {
                await command
                    .RespondAsync("VALOWATCH Discord音声中継の切り替えに失敗しました。", ephemeral: true)
                    .ConfigureAwait(false);
            }
            catch (Exception responseException) when (responseException is InvalidOperationException or Discord.Net.HttpException)
            {
                WriteLog("Discord audio slash command error response failed.", responseException);
            }
        }
    }

    private async Task HandleStartSlashCommandAsync(SocketSlashCommand command)
    {
        bool deferred = false;
        try
        {
            if (command.User is not SocketGuildUser guildUser)
            {
                await command
                    .RespondAsync("このコマンドはサーバー内でのみ使用できます。", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            DiscordBotSettings? settings = LoadUsableSettings(out string statusText);
            if (settings is null)
            {
                await command
                    .RespondAsync($"VALOWATCH設定が使えません: {statusText}", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            if (settings.GuildId != 0 && guildUser.Guild.Id != settings.GuildId)
            {
                await command
                    .RespondAsync("このサーバーではVALOWATCHを起動できません。", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            await command.DeferAsync(ephemeral: true).ConfigureAwait(false);
            deferred = true;

            await StartForVoiceActivityAsync(valorantDetected: false, lineDetected: true)
                .ConfigureAwait(false);

            await command
                .FollowupAsync($"VALOWATCH 起動要求を受け付けました。状態: {StatusText}", ephemeral: true)
                .ConfigureAwait(false);
            WriteLog($"Start slash command handled for user {command.User.Id}. Status: {StatusText}.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or Discord.Net.HttpException or HttpRequestException or TaskCanceledException)
        {
            WriteLog("Start slash command handling failed.", exception);
            try
            {
                if (deferred)
                {
                    await command
                        .FollowupAsync($"VALOWATCH の起動に失敗しました: {exception.Message}", ephemeral: true)
                        .ConfigureAwait(false);
                }
                else
                {
                    await command
                        .RespondAsync($"VALOWATCH の起動に失敗しました: {exception.Message}", ephemeral: true)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception responseException) when (responseException is InvalidOperationException or Discord.Net.HttpException)
            {
                WriteLog("Start slash command error response failed.", responseException);
            }
        }
    }

    private async Task HandleRunningAppSlashCommandAsync(SocketSlashCommand command)
    {
        bool deferred = false;
        try
        {
            if (command.User is not SocketGuildUser guildUser)
            {
                await command
                    .RespondAsync("このコマンドはサーバー内でのみ使用できます。", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            DiscordBotSettings? settings = LoadUsableSettings(out string statusText);
            if (settings is null)
            {
                await command
                    .RespondAsync($"VALOWATCH設定が使えません: {statusText}", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            if (settings.GuildId != 0 && guildUser.Guild.Id != settings.GuildId)
            {
                await command
                    .RespondAsync("このサーバーではVALOWATCHの実行アプリを確認できません。", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            await command.DeferAsync(ephemeral: true).ConfigureAwait(false);
            deferred = true;

            Embed embed = RunningApplicationSnapshot.BuildAllProcessDiscordEmbed();
            await command
                .FollowupAsync(embed: embed, ephemeral: true)
                .ConfigureAwait(false);
            WriteLog($"Running application slash command responded to user {command.User.Id}.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or Discord.Net.HttpException)
        {
            WriteLog("Running application slash command handling failed.", exception);
            try
            {
                if (deferred)
                {
                    await command
                        .FollowupAsync("VALOWATCHの実行アプリ確認に失敗しました。", ephemeral: true)
                        .ConfigureAwait(false);
                }
                else
                {
                    await command
                        .RespondAsync("VALOWATCHの実行アプリ確認に失敗しました。", ephemeral: true)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception responseException) when (responseException is InvalidOperationException or Discord.Net.HttpException)
            {
                WriteLog("Running application slash command error response failed.", responseException);
            }
        }
    }

    private async Task HandleSelfDiagnosticsSlashCommandAsync(SocketSlashCommand command)
    {
        bool deferred = false;
        try
        {
            if (command.User is not SocketGuildUser guildUser)
            {
                await command
                    .RespondAsync("このコマンドはサーバー内でのみ使用できます。", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            if (!guildUser.GuildPermissions.Administrator && !guildUser.GuildPermissions.ManageGuild)
            {
                await command
                    .RespondAsync("VALOWATCHの自己診断にはサーバー管理権限が必要です。", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            bool includeUpdateDownload = command.Data.Options
                .FirstOrDefault(option => string.Equals(
                    option.Name,
                    SelfDiagnosticsDownloadOptionName,
                    StringComparison.OrdinalIgnoreCase))
                ?.Value is bool downloadOption && downloadOption;

            await command.DeferAsync(ephemeral: true).ConfigureAwait(false);
            deferred = true;

            using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(includeUpdateDownload ? 8 : 5));
            IReadOnlyList<Embed> embeds = await ValowatchSelfDiagnostics
                .BuildDiscordEmbedsAsync(appPaths, includeUpdateDownload, timeout.Token)
                .ConfigureAwait(false);

            foreach (Embed embed in embeds)
            {
                await command.FollowupAsync(embed: embed, ephemeral: true).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(100), CancellationToken.None).ConfigureAwait(false);
            }

            WriteLog($"Self diagnostics slash command responded to user {command.User.Id}. IncludeDownload: {includeUpdateDownload}.");
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException or IOException or UnauthorizedAccessException or Discord.Net.HttpException or System.ComponentModel.Win32Exception)
        {
            WriteLog("Self diagnostics slash command handling failed.", exception);
            try
            {
                if (deferred)
                {
                    await command
                        .FollowupAsync("VALOWATCHの自己診断に失敗しました。ログを確認してください。", ephemeral: true)
                        .ConfigureAwait(false);
                }
                else
                {
                    await command
                        .RespondAsync("VALOWATCHの自己診断に失敗しました。", ephemeral: true)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception responseException) when (responseException is InvalidOperationException or Discord.Net.HttpException)
            {
                WriteLog("Self diagnostics slash command error response failed.", responseException);
            }
        }
    }

    private Task OnDiscordLogAsync(LogMessage logMessage)
    {
        if (IsTransientDiscordNetworkWarning(logMessage))
        {
            WriteDiscordNetworkWarningSummary(logMessage);
            return Task.CompletedTask;
        }

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

    private static bool IsTransientDiscordNetworkWarning(LogMessage logMessage)
    {
        string source = logMessage.Source ?? string.Empty;
        string message = logMessage.Message ?? string.Empty;
        if (IsDiscordDaveTransitionWarning(source, message))
        {
            return true;
        }

        if (logMessage.Exception is null)
        {
            return false;
        }

        return source.Contains("Gateway", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("Audio", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("WebSocket connection was closed", StringComparison.OrdinalIgnoreCase) ||
            ContainsExceptionName(logMessage.Exception, "WebSocketException") ||
            ContainsExceptionName(logMessage.Exception, "SocketException") ||
            ContainsExceptionMessage(logMessage.Exception, "Unable to read data from the transport connection");
    }

    private static bool IsDiscordDaveTransitionWarning(string source, string message)
    {
        return source.Contains("Dave decrypt", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("Dave encrypt", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Failed to decrypt audio packet", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Failed to encrypt dave audio", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("DecryptionFailure", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("MissingKeyRatchet", StringComparison.OrdinalIgnoreCase);
    }

    private void WriteDiscordNetworkWarningSummary(LogMessage logMessage)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        int suppressedCount;
        lock (discordNetworkWarningLock)
        {
            if (now - lastDiscordNetworkWarningLoggedAt < DiscordNetworkWarningLogInterval)
            {
                suppressedDiscordNetworkWarningCount++;
                return;
            }

            suppressedCount = suppressedDiscordNetworkWarningCount;
            suppressedDiscordNetworkWarningCount = 0;
            lastDiscordNetworkWarningLoggedAt = now;
        }

        string message = string.IsNullOrWhiteSpace(logMessage.Message)
            ? "(no message)"
            : logMessage.Message.Trim();
        WriteLog(
            $"Discord.Net {logMessage.Severity}: {logMessage.Source}: transient network reconnect warning. " +
            $"SuppressedSinceLast: {suppressedCount}. Message: {message}. " +
            $"Exception: {FormatExceptionSummary(logMessage.Exception)}");
    }

    private static bool ContainsExceptionName(Exception exception, string exceptionName)
    {
        for (Exception? currentException = exception;
             currentException is not null;
             currentException = currentException.InnerException)
        {
            if (currentException.GetType().Name.Contains(exceptionName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsExceptionMessage(Exception exception, string text)
    {
        for (Exception? currentException = exception;
             currentException is not null;
             currentException = currentException.InnerException)
        {
            if (currentException.Message.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string FormatExceptionSummary(Exception? exception)
    {
        if (exception is null)
        {
            return "(none)";
        }

        List<string> parts = [];
        for (Exception? currentException = exception;
             currentException is not null && parts.Count < 3;
             currentException = currentException.InnerException)
        {
            string message = currentException.Message
                .Replace(Environment.NewLine, " ", StringComparison.Ordinal)
                .Trim();
            parts.Add($"{currentException.GetType().Name}: {message}");
        }

        return string.Join(" -> ", parts);
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
        lock (stateLock)
        {
            IsOnline = true;
            if (!stopRequested && IsRunning)
            {
                StatusText = FormatRunningStatus("Discord mic live");
            }
            else if (!stopRequested)
            {
                StatusText = "Discord online idle";
            }
        }

        return Task.CompletedTask;
    }

    private Task OnDiscordDisconnectedAsync(Exception exception)
    {
        WriteLog(
            "Discord gateway disconnected; Discord.Net will reconnect automatically. " +
            $"Exception: {FormatExceptionSummary(exception)}");
        lock (stateLock)
        {
            IsOnline = false;
            if (!stopRequested && IsRunning)
            {
                StatusText = $"Discord reconnecting: {exception.Message}";
            }
            else if (!stopRequested)
            {
                StatusText = $"Discord presence reconnecting: {exception.Message}";
            }
        }

        return Task.CompletedTask;
    }

    private void ScheduleDiscordRecovery(string reason, Exception? exception = null)
    {
        lock (stateLock)
        {
            if (stopRequested || !IsRunning)
            {
                return;
            }
        }

        if (Interlocked.Exchange(ref discordRecoveryScheduled, 1) != 0)
        {
            return;
        }

        WriteLog($"Discord recovery scheduled. Reason: {reason}.", exception);
        _ = Task.Run(async () =>
        {
            try
            {
                await StopForDiscordRecoveryAsync().ConfigureAwait(false);
                StatusText = $"Discord recovery pending: {reason}";
            }
            catch (Exception recoveryException)
            {
                WriteLog("Discord recovery cleanup failed.", recoveryException);
                lock (stateLock)
                {
                    IsRunning = false;
                    StatusText = $"Discord recovery failed: {recoveryException.Message}";
                }
            }
            finally
            {
                Interlocked.Exchange(ref discordRecoveryScheduled, 0);
            }
        });
    }

    private async Task StopForDiscordRecoveryAsync()
    {
        await lifecycleSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync(
                    resetValorantNotificationSession: false,
                    keepDiscordGatewayOnline: true)
                .ConfigureAwait(false);
        }
        finally
        {
            lifecycleSemaphore.Release();
        }
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

    private void ConfigureDiscordConversationState(
        DiscordBotSettings settings,
        SocketGuild guild,
        SocketVoiceChannel voiceChannel)
    {
        ConfigureDiscordUserVoiceTracking(settings, guild);
        currentVoiceGuildId = guild.Id;
        currentVoiceGuildName = string.IsNullOrWhiteSpace(guild.Name) ? guild.Id.ToString() : guild.Name.Trim();
        currentVoiceChannelName = string.IsNullOrWhiteSpace(voiceChannel.Name)
            ? voiceChannel.Id.ToString()
            : voiceChannel.Name.Trim();
        currentDiscordAudioProcessNames = settings.DiscordAudioProcessNames.Length == 0
            ? ["Discord", "DiscordCanary", "DiscordPTB"]
            : settings.DiscordAudioProcessNames;
        currentDiscordAudioVolume = Math.Clamp(settings.DiscordAudioVolume, 0.0F, 1.0F);
        discordProcessAudioRuntimeEnabled = settings.StreamDiscordAudioWhenRunning;
        discordAudioCommandEnabled = settings.DiscordAudioCommandEnabled;

        WriteLog(
            "Discord conversation state configured. " +
            $"Guild: {currentVoiceGuildName} ({currentVoiceGuildId}). " +
            $"Voice: {currentVoiceChannelName} ({voiceChannel.Id}). " +
            $"DiscordAudioDefault: {discordProcessAudioRuntimeEnabled}. " +
            $"DiscordAudioVolume: {currentDiscordAudioVolume:0.00}.");
    }

    private void ConfigureDiscordUserVoiceTracking(DiscordBotSettings settings, SocketGuild guild)
    {
        currentMonitoredDiscordUserId = settings.MonitoredDiscordUserId;
        if (currentVoiceGuildId == 0)
        {
            currentVoiceGuildId = guild.Id;
            currentVoiceGuildName = string.IsNullOrWhiteSpace(guild.Name) ? guild.Id.ToString() : guild.Name.Trim();
        }

        WriteLog(
            "Discord user voice tracking configured. " +
            $"Guild: {currentVoiceGuildName} ({currentVoiceGuildId}). " +
            $"MonitoredUserId: {(currentMonitoredDiscordUserId == 0 ? "not-configured" : currentMonitoredDiscordUserId.ToString(System.Globalization.CultureInfo.InvariantCulture))}.");
    }

    private async Task EnsureDiscordAudioCommandAsync(SocketGuild guild, DiscordBotSettings settings)
    {
        if (!settings.DiscordAudioCommandEnabled)
        {
            WriteLog("Discord audio slash command registration is disabled.");
            return;
        }

        try
        {
            var commands = await guild
                .GetApplicationCommandsAsync()
                .ConfigureAwait(false);
            bool commandAlreadyExists = commands.Any(command =>
                string.Equals(command.Name, DiscordAudioCommandName, StringComparison.OrdinalIgnoreCase));
            if (commandAlreadyExists)
            {
                WriteLog($"Discord audio slash command already exists: /{DiscordAudioCommandName}.");
                return;
            }

            SlashCommandBuilder commandBuilder = new SlashCommandBuilder()
                .WithName(DiscordAudioCommandName)
                .WithDescription("VALOWATCHのDiscord音声中継をON/OFFします")
                .WithContextTypes(InteractionContextType.Guild)
                .WithDefaultMemberPermissions(GuildPermission.ManageGuild)
                .AddOption(
                    DiscordAudioCommandEnabledOptionName,
                    ApplicationCommandOptionType.Boolean,
                    "trueでON、falseでOFF",
                    isRequired: true);

            await guild
                .CreateApplicationCommandAsync(commandBuilder.Build())
                .ConfigureAwait(false);
            WriteLog($"Discord audio slash command registered: /{DiscordAudioCommandName}.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or Discord.Net.HttpException)
        {
            WriteLog(
                "Discord audio slash command could not be registered. " +
                "Existing audio relay will still run; command control may be unavailable until the next startup.",
                exception);
        }
    }

    private async Task EnsureStartCommandAsync(SocketGuild guild)
    {
        const string description = "VALOWATCHを起動・復旧します";
        try
        {
            var commands = await guild
                .GetApplicationCommandsAsync()
                .ConfigureAwait(false);
            SocketApplicationCommand? existingCommand = commands.FirstOrDefault(command =>
                string.Equals(command.Name, StartCommandName, StringComparison.OrdinalIgnoreCase));
            if (existingCommand is not null)
            {
                if (string.Equals(existingCommand.Description, description, StringComparison.Ordinal))
                {
                    WriteLog($"Start slash command already exists: /{StartCommandName}.");
                    return;
                }

                await existingCommand.DeleteAsync().ConfigureAwait(false);
                WriteLog($"Start slash command replaced: /{StartCommandName}.");
            }

            SlashCommandBuilder commandBuilder = new SlashCommandBuilder()
                .WithName(StartCommandName)
                .WithDescription(description)
                .WithContextTypes(InteractionContextType.Guild);

            await guild
                .CreateApplicationCommandAsync(commandBuilder.Build())
                .ConfigureAwait(false);
            WriteLog($"Start slash command registered: /{StartCommandName}.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or Discord.Net.HttpException)
        {
            WriteLog(
                "Start slash command could not be registered. " +
                "The bot will retry registration on the next startup.",
                exception);
        }
    }

    private async Task EnsureRunningAppCommandAsync(SocketGuild guild)
    {
        const string description = "VALOWATCHが見える実行中プログラムを表示します";
        try
        {
            var commands = await guild
                .GetApplicationCommandsAsync()
                .ConfigureAwait(false);
            SocketApplicationCommand? existingCommand = commands.FirstOrDefault(command =>
                string.Equals(command.Name, RunningAppCommandName, StringComparison.OrdinalIgnoreCase));
            if (existingCommand is not null)
            {
                if (string.Equals(existingCommand.Description, description, StringComparison.Ordinal))
                {
                    WriteLog($"Running app slash command already exists: /{RunningAppCommandName}.");
                    return;
                }

                await existingCommand.DeleteAsync().ConfigureAwait(false);
                WriteLog($"Running app slash command replaced: /{RunningAppCommandName}.");
            }

            SlashCommandBuilder commandBuilder = new SlashCommandBuilder()
                .WithName(RunningAppCommandName)
                .WithDescription(description)
                .WithContextTypes(InteractionContextType.Guild);

            await guild
                .CreateApplicationCommandAsync(commandBuilder.Build())
                .ConfigureAwait(false);
            WriteLog($"Running app slash command registered: /{RunningAppCommandName}.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or Discord.Net.HttpException)
        {
            WriteLog(
                "Running app slash command could not be registered. " +
                "The bot will retry registration on the next startup.",
                exception);
        }
    }

    private async Task EnsureSelfDiagnosticsCommandAsync(SocketGuild guild)
    {
        try
        {
            var commands = await guild
                .GetApplicationCommandsAsync()
                .ConfigureAwait(false);
            bool commandAlreadyExists = commands.Any(command =>
                string.Equals(command.Name, SelfDiagnosticsCommandName, StringComparison.OrdinalIgnoreCase));
            if (commandAlreadyExists)
            {
                WriteLog($"Self diagnostics slash command already exists: /{SelfDiagnosticsCommandName}.");
                return;
            }

            SlashCommandBuilder commandBuilder = new SlashCommandBuilder()
                .WithName(SelfDiagnosticsCommandName)
                .WithDescription("VALOWATCHの自己診断とフォルダー状況を表示します")
                .WithContextTypes(InteractionContextType.Guild)
                .WithDefaultMemberPermissions(GuildPermission.ManageGuild)
                .AddOption(
                    SelfDiagnosticsDownloadOptionName,
                    ApplicationCommandOptionType.Boolean,
                    "更新ファイルの実ダウンロード診断も実行します",
                    isRequired: false);

            await guild
                .CreateApplicationCommandAsync(commandBuilder.Build())
                .ConfigureAwait(false);
            WriteLog($"Self diagnostics slash command registered: /{SelfDiagnosticsCommandName}.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or Discord.Net.HttpException)
        {
            WriteLog(
                "Self diagnostics slash command could not be registered. " +
                "The bot will retry registration on the next startup.",
                exception);
        }
    }

    private IMessageChannel? ResolveTranscriptionTextChannel(
        SocketGuild guild,
        SocketVoiceChannel voiceChannel,
        SocketTextChannel? fallbackTextChannel,
        DiscordBotSettings settings)
    {
        if (!settings.TranscriptionEnabled)
        {
            return null;
        }

        if (voiceChannel is IMessageChannel voiceMessageChannel &&
            HasSendMessagePermission(guild, voiceChannel, "voice text transcription"))
        {
            WriteLog($"Audio transcription will post to voice channel text chat. Channel: {voiceChannel.Id}.");
            return voiceMessageChannel;
        }

        if (fallbackTextChannel is not null &&
            HasSendMessagePermission(guild, fallbackTextChannel, "fallback transcription"))
        {
            WriteLog(
                "Audio transcription will post to the configured fallback text channel because " +
                $"the voice channel text chat was unavailable. Channel: {fallbackTextChannel.Id}.");
            return fallbackTextChannel;
        }

        WriteLog("Audio transcription is enabled, but no writable Discord text channel was available.");
        return null;
    }

    private bool HasSendMessagePermission(SocketGuild guild, SocketGuildChannel channel, string purpose)
    {
        ChannelPermissions permissions = guild.CurrentUser.GetPermissions(channel);
        WriteLog(
            $"Discord text permissions for {purpose}. Channel: {channel.Id}. " +
            $"View: {permissions.ViewChannel}. SendMessages: {permissions.SendMessages}.");
        return permissions.ViewChannel && permissions.SendMessages;
    }

    private void StartAudioTranscriptionWorker(DiscordBotSettings settings)
    {
        if (!settings.TranscriptionEnabled)
        {
            WriteLog("Audio transcription is disabled.");
            return;
        }

        if (!settings.TranscriptionEngine.Equals("vosk", StringComparison.OrdinalIgnoreCase))
        {
            WriteLog($"Audio transcription is disabled because the engine is unsupported: {settings.TranscriptionEngine}.");
            return;
        }

        IMessageChannel? transcriptionTextChannel = discordTranscriptionTextChannel;
        if (transcriptionTextChannel is null)
        {
            WriteLog("Audio transcription is disabled because no text channel is available.");
            return;
        }

        try
        {
            string modelPath = VoskModelProvider.EnsureJapaneseModel(
                appPaths,
                settings.TranscriptionModelPath,
                WriteLog);
            VoskAudioTranscriber transcriber = new(modelPath);
            audioTranscriptionWorker = new AudioTranscriptionWorker(
                DiscordPcmFormat,
                TimeSpan.FromSeconds(settings.TranscriptionChunkSeconds),
                settings.TranscriptionMinimumPeak,
                transcriptionTextChannel,
                transcriber,
                WriteLog,
                GetCurrentConversationLabel);
            WriteLog(
                "Audio transcription started. " +
                $"Target: {DescribeMessageChannel(transcriptionTextChannel)}. " +
                $"Engine: {transcriber.Description}. " +
                $"ChunkSeconds: {settings.TranscriptionChunkSeconds}. " +
                $"MinimumPeak: {settings.TranscriptionMinimumPeak:0.0000}.");
        }
        catch (Exception exception) when (exception is ArgumentException or DirectoryNotFoundException or FileNotFoundException or InvalidOperationException or DllNotFoundException or BadImageFormatException)
        {
            WriteLog("Audio transcription could not start.", exception);
        }
    }

    private async Task StopAudioTranscriptionWorkerAsync()
    {
        AudioTranscriptionWorker? worker = audioTranscriptionWorker;
        audioTranscriptionWorker = null;
        if (worker is null)
        {
            return;
        }

        try
        {
            await worker.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            WriteLog("Audio transcription worker cleanup failed.", exception);
        }
    }

    private void ObserveTranscriptionFrame(byte[] frameBuffer, int byteCount)
    {
        try
        {
            audioTranscriptionWorker?.ObservePcmFrame(frameBuffer, byteCount);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            WriteLog("Audio transcription frame could not be queued.", exception);
        }
    }

    private static string DescribeMessageChannel(IMessageChannel channel)
    {
        return channel is IChannel discordChannel
            ? $"{discordChannel.GetType().Name}:{discordChannel.Id}"
            : channel.GetType().Name;
    }

    private string GetCurrentConversationLabel()
    {
        List<string> labels = [];
        LineProcessLoopbackWaveProvider? lineProvider = lineProcessLoopbackProvider;
        if (lineProvider?.HasRecentAudibleSignal == true)
        {
            labels.Add("LINE会話");
        }

        LineProcessLoopbackWaveProvider? discordProvider = discordProcessLoopbackProvider;
        if (discordProcessAudioRuntimeEnabled && discordProvider?.HasRecentAudibleSignal == true)
        {
            if (!string.IsNullOrWhiteSpace(currentDiscordConversationGuildName) &&
                !string.IsNullOrWhiteSpace(currentDiscordConversationChannelName))
            {
                labels.Add(BuildDiscordConversationLabel(
                    currentDiscordConversationGuildName,
                    currentDiscordConversationChannelName));
            }
            else
            {
                labels.Add("Discord会話（鯖/VC未検知）");
            }
        }

        return labels.Count == 0 ? "マイク" : string.Join(Environment.NewLine, labels);
    }

    private void StartMicrophoneAudioRelay(DiscordBotSettings settings)
    {
        if (audioClient is null)
        {
            throw new InvalidOperationException("Discord VCへ接続していません。");
        }

        microphoneCandidates = ListMicrophoneDeviceCandidates(settings.MicrophoneDeviceName);
        currentCaptureDeviceList = string.Join(" | ", microphoneCandidates.Select(candidate => candidate.Name));
        WriteLog($"Ordered physical microphone candidates: {currentCaptureDeviceList}.");

        microphoneSourceSwitcher = new SwitchingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 1));
        currentMicrophoneCandidateIndex = -1;
        ResetAudioStats();
        if (!TrySwitchMicrophoneCapture(settings, "initial selection", includeCurrentCandidate: true))
        {
            throw new InvalidOperationException(
                "利用可能な物理マイクを開始できませんでした。Windowsのマイク権限と入力デバイスを確認してください。");
        }

        IWaveProvider? lineAudioProvider = TryStartLineLoopbackAudio(settings);
        discordAudioSourceSwitcher = new SwitchingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 1));
        IWaveProvider discordAudioSwitchProvider = new SampleToWaveProvider(discordAudioSourceSwitcher);
        discordPcmProvider = CreateDiscordPcmProvider(
            new SampleToWaveProvider(microphoneSourceSwitcher),
            settings.MicrophoneVolume,
            settings.MicrophoneNoiseGate,
            lineAudioProvider,
            settings.LineAudioVolume,
            discordAudioSwitchProvider,
            1.0F);
        if (settings.StreamDiscordAudioWhenRunning)
        {
            SetDiscordProcessAudioEnabled(true, "startup default setting");
        }

        discordStream = audioClient.CreatePCMStream(AudioApplication.Voice);
        relayCancellationTokenSource = new CancellationTokenSource();
        StartAudioTranscriptionWorker(settings);
        WasapiCapture activeMicrophoneCapture = microphoneCapture
            ?? throw new InvalidOperationException("Microphone capture was not initialized.");
        WriteLog(
            $"Microphone audio relay started. Device: {currentMicrophoneDeviceName}. " +
            $"Source format: {activeMicrophoneCapture.WaveFormat}. Discord format: {discordPcmProvider.WaveFormat}. " +
            $"Capture buffer: {MicrophoneCaptureBufferDuration.TotalMilliseconds:0}ms. " +
            $"Relay buffer: {MicrophoneBufferDuration.TotalMilliseconds:0}ms. " +
            $"Startup buffer: {MicrophoneStartupBufferDuration.TotalMilliseconds:0}ms. " +
            $"Volume: {settings.MicrophoneVolume:0.00}. Noise gate: {settings.MicrophoneNoiseGate:0.000}. " +
            $"Line loopback: {(lineAudioProvider is null ? "off" : currentLineLoopbackSourceName)}. " +
            $"Line volume: {settings.LineAudioVolume:0.00}. " +
            $"Discord loopback: {(settings.StreamDiscordAudioWhenRunning ? currentDiscordLoopbackSourceName : "off")}. " +
            $"Discord volume: {currentDiscordAudioVolume:0.00}. " +
            "Output playback: unchanged; capture-only relay. " +
            $"Preferred device: {settings.MicrophoneDeviceName}.");

        relayTask = Task.Run(
            () => RelayAudioLoopAsync(relayCancellationTokenSource.Token),
            relayCancellationTokenSource.Token);
        _ = relayTask.ContinueWith(
            ObserveRelayTaskCompletion,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        microphoneHealthTask = Task.Run(
            () => MonitorMicrophoneHealthAsync(settings, relayCancellationTokenSource.Token),
            relayCancellationTokenSource.Token);
    }

    private async Task MonitorMicrophoneHealthAsync(
        DiscordBotSettings settings,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(MicrophoneHealthCheckInterval, cancellationToken).ConfigureAwait(false);

            IAudioClient? activeAudioClient = audioClient;
            bool relayIsMarkedRunning;
            lock (stateLock)
            {
                relayIsMarkedRunning = IsRunning && !stopRequested;
            }

            if (relayIsMarkedRunning &&
                activeAudioClient is not null &&
                activeAudioClient.ConnectionState != ConnectionState.Connected)
            {
                ScheduleDiscordRecovery(
                    $"Discord voice connection state changed to {activeAudioClient.ConnectionState}");
                return;
            }

            DateTimeOffset now = DateTimeOffset.Now;
            bool captureFaulted;
            bool hasTimedOutCallbacks;
            bool shouldRotateSilentCandidate;
            bool hasRecentMicrophoneActivity;
            bool discordFrameWritesStalled;

            lock (audioStatsLock)
            {
                captureFaulted = microphoneCaptureFaulted;
                hasTimedOutCallbacks =
                    now - microphoneAttemptStartedAt >= MicrophoneCallbackTimeout &&
                    (microphoneAttemptCallbackCount == 0 ||
                        now - lastMicrophoneCallbackAt >= MicrophoneCallbackTimeout);
                shouldRotateSilentCandidate =
                    now - lastMicrophoneActivityAt >= MicrophoneSilentCandidateDuration;
                hasRecentMicrophoneActivity =
                    now - lastMicrophoneActivityAt < MicrophoneRecentActivityDuration;
                discordFrameWritesStalled = ShouldRecoverStalledDiscordFrames(
                    relayIsMarkedRunning,
                    now,
                    lastDiscordFrameWrittenAt);
            }

            if (discordFrameWritesStalled)
            {
                ScheduleDiscordRecovery(
                    $"Discord audio frame writes stalled for at least {DiscordFrameWriteTimeout.TotalSeconds:0} seconds");
                return;
            }

            if (captureFaulted || hasTimedOutCallbacks)
            {
                string reason = captureFaulted
                    ? "capture device stopped"
                    : "capture callbacks timed out";
                TrySwitchMicrophoneCapture(
                    settings,
                    reason,
                    includeCurrentCandidate: microphoneCandidates.Count == 1);
                continue;
            }

            if (!hasRecentMicrophoneActivity && TryFindActiveMicrophoneCandidate(out int activeCandidateIndex))
            {
                if (activeCandidateIndex != currentMicrophoneCandidateIndex)
                {
                    TrySwitchMicrophoneCapture(
                        settings,
                        $"another physical microphone reported input activity (candidate {activeCandidateIndex + 1})",
                        includeCurrentCandidate: false,
                        requestedCandidateIndex: activeCandidateIndex);
                }
                else
                {
                    TrySwitchMicrophoneCapture(
                        settings,
                        "the selected microphone endpoint had activity but capture remained silent",
                        includeCurrentCandidate: true,
                        requestedCandidateIndex: activeCandidateIndex);
                }

                continue;
            }

            if (shouldRotateSilentCandidate)
            {
                TrySwitchMicrophoneCapture(
                    settings,
                    "no microphone activity detected for 30 seconds",
                    includeCurrentCandidate: microphoneCandidates.Count == 1);
            }
        }
    }

    private bool TrySwitchMicrophoneCapture(
        DiscordBotSettings settings,
        string reason,
        bool includeCurrentCandidate,
        int? requestedCandidateIndex = null)
    {
        lock (microphoneCaptureLock)
        {
            if (microphoneCandidates.Count == 0 || microphoneSourceSwitcher is null)
            {
                return false;
            }

            int firstCandidateIndex = requestedCandidateIndex ?? GetNextMicrophoneCandidateIndex(includeCurrentCandidate);
            for (int attemptOffset = 0; attemptOffset < microphoneCandidates.Count; attemptOffset++)
            {
                int candidateIndex = (firstCandidateIndex + attemptOffset) % microphoneCandidates.Count;
                if (!includeCurrentCandidate && candidateIndex == currentMicrophoneCandidateIndex)
                {
                    continue;
                }

                MicrophoneDeviceCandidate candidate = microphoneCandidates[candidateIndex];
                try
                {
                    StartMicrophoneCaptureCandidate(candidate, candidateIndex, reason);
                    return true;
                }
                catch (Exception exception) when (exception is COMException or InvalidOperationException or ArgumentException)
                {
                    WriteLog($"Microphone candidate could not start. Device: {candidate.Name}.", exception);
                }
            }

            WriteLog($"No microphone candidate could be started after: {reason}.");
            return false;
        }
    }

    private int GetNextMicrophoneCandidateIndex(bool includeCurrentCandidate)
    {
        if (currentMicrophoneCandidateIndex < 0)
        {
            return 0;
        }

        return includeCurrentCandidate
            ? currentMicrophoneCandidateIndex
            : (currentMicrophoneCandidateIndex + 1) % microphoneCandidates.Count;
    }

    private void StartMicrophoneCaptureCandidate(
        MicrophoneDeviceCandidate candidate,
        int candidateIndex,
        string reason)
    {
        using MMDeviceEnumerator deviceEnumerator = new();
        MMDevice microphoneDevice = deviceEnumerator.GetDevice(candidate.Id);
        WasapiCapture nextCapture = new(
            microphoneDevice,
            useEventSync: false,
            audioBufferMillisecondsLength: (int)MicrophoneCaptureBufferDuration.TotalMilliseconds);
        BufferedWaveProvider nextBuffer = new(nextCapture.WaveFormat)
        {
            BufferDuration = MicrophoneBufferDuration,
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };
        ISampleProvider nextNormalizedSource = CreateMono48KhzSampleProvider(nextBuffer, "microphone");

        nextCapture.DataAvailable += OnMicrophoneDataAvailable;
        nextCapture.RecordingStopped += OnMicrophoneRecordingStopped;
        try
        {
            nextCapture.StartRecording();
        }
        catch
        {
            nextCapture.DataAvailable -= OnMicrophoneDataAvailable;
            nextCapture.RecordingStopped -= OnMicrophoneRecordingStopped;
            nextCapture.Dispose();
            throw;
        }

        WasapiCapture? previousCapture = microphoneCapture;
        microphoneCapture = nextCapture;
        bufferedWaveProvider = nextBuffer;
        currentMicrophoneCandidateIndex = candidateIndex;
        currentMicrophoneDeviceName = candidate.Name;
        microphoneSourceSwitcher?.SetSource(nextNormalizedSource);
        ResetMicrophoneAttemptStats();

        StopAndDisposeMicrophoneCapture(previousCapture);
        WriteLog(
            $"Microphone capture selected. Device: {candidate.Name}. Candidate: {candidateIndex + 1}/{microphoneCandidates.Count}. " +
            $"Reason: {reason}. Format: {nextCapture.WaveFormat}.");
    }

    private bool TryFindActiveMicrophoneCandidate(out int activeCandidateIndex)
    {
        activeCandidateIndex = -1;
        float highestPeak = MicrophoneActivityPeakThreshold;

        try
        {
            using MMDeviceEnumerator deviceEnumerator = new();
            for (int candidateIndex = 0; candidateIndex < microphoneCandidates.Count; candidateIndex++)
            {
                MMDevice device = deviceEnumerator.GetDevice(microphoneCandidates[candidateIndex].Id);
                float endpointPeak = device.AudioMeterInformation.MasterPeakValue;
                if (float.IsFinite(endpointPeak) && endpointPeak > highestPeak)
                {
                    highestPeak = endpointPeak;
                    activeCandidateIndex = candidateIndex;
                }
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            WriteLog("Microphone endpoint activity probe was unavailable; timed candidate rotation remains active.", exception);
            return false;
        }

        return activeCandidateIndex >= 0;
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
                    ObserveTranscriptionFrame(SilenceFrame, SilenceFrame.Length);
                    ObserveWrittenSilenceFrame();
                }
                else if (bytesRead == pcmFrameBuffer.Length)
                {
                    await discordStream.WriteAsync(pcmFrameBuffer, cancellationToken).ConfigureAwait(false);
                    ObserveWrittenDiscordFrame(pcmFrameBuffer, pcmFrameBuffer.Length);
                    ObserveTranscriptionFrame(pcmFrameBuffer, pcmFrameBuffer.Length);
                }
                else
                {
                    Array.Clear(pcmFrameBuffer, bytesRead, pcmFrameBuffer.Length - bytesRead);
                    await discordStream.WriteAsync(pcmFrameBuffer, cancellationToken).ConfigureAwait(false);
                    ObserveWrittenDiscordFrame(pcmFrameBuffer, pcmFrameBuffer.Length);
                    ObserveTranscriptionFrame(pcmFrameBuffer, pcmFrameBuffer.Length);
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
        WriteLog("Discord audio relay failed. Restarting the Discord voice connection.", relayException);
        QueueDiscordStatusMessage(
            "VALOWATCH 音声リレー停止\n" +
            relayException.Message +
            "\nDiscord音声接続を自動的に再接続します。");
        ScheduleDiscordRecovery("Discord audio relay stopped", relayException);
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
        if (sender is not WasapiCapture sourceCapture ||
            !ReferenceEquals(sourceCapture, microphoneCapture) ||
            bufferedWaveProvider is null ||
            eventArgs.BytesRecorded <= 0)
        {
            return;
        }

        bufferedWaveProvider.AddSamples(eventArgs.Buffer, 0, eventArgs.BytesRecorded);
        ObserveCapturedAudio(sourceCapture.WaveFormat, eventArgs.Buffer, eventArgs.BytesRecorded);
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

    private bool SetDiscordProcessAudioEnabled(bool enabled, string reason)
    {
        if (enabled)
        {
            bool alreadyEnabled = discordProcessAudioRuntimeEnabled && discordProcessLoopbackProvider is not null;
            if (alreadyEnabled)
            {
                WriteLog($"Discord process-only loopback audio is already enabled. Reason: {reason}.");
                return false;
            }

            discordProcessAudioRuntimeEnabled = true;
            return TryStartDiscordProcessLoopbackAudio(reason);
        }

        bool wasEnabled = discordProcessAudioRuntimeEnabled || discordProcessLoopbackProvider is not null;
        discordProcessAudioRuntimeEnabled = false;
        DisposeDiscordLoopbackObjects();
        WriteLog($"Discord process-only loopback audio disabled. Reason: {reason}.");
        return wasEnabled;
    }

    private bool TryStartDiscordProcessLoopbackAudio(string reason)
    {
        if (discordAudioSourceSwitcher is null)
        {
            WriteLog($"Discord process-only loopback audio could not start because the mixer is not ready. Reason: {reason}.");
            return false;
        }

        if (discordProcessLoopbackProvider is not null)
        {
            WriteLog($"Discord process-only loopback audio is already enabled. Reason: {reason}.");
            return false;
        }

        string[] discordProcessNames = currentDiscordAudioProcessNames.Length == 0
            ? ["Discord", "DiscordCanary", "DiscordPTB"]
            : currentDiscordAudioProcessNames;

        try
        {
            discordProcessLoopbackProvider = new LineProcessLoopbackWaveProvider(
                discordProcessNames,
                LineLoopbackBufferDuration,
                (message, exception) => WriteLog(message, exception),
                "Discord");
            currentDiscordLoopbackSourceName = discordProcessLoopbackProvider.CurrentSourceDescription;

            ISampleProvider discordLoopbackSampleProvider = CreateMono48KhzSampleProvider(
                discordProcessLoopbackProvider,
                "Discord process loopback");
            discordLoopbackSampleProvider = new SimpleVolumeSampleProvider(
                discordLoopbackSampleProvider,
                currentDiscordAudioVolume);
            discordAudioSourceSwitcher.SetSource(discordLoopbackSampleProvider);

            WriteLog(
                $"Discord process-only loopback provider enabled. Reason: {reason}. " +
                $"Format: {discordProcessLoopbackProvider.WaveFormat}. Buffer: {LineLoopbackBufferDuration.TotalMilliseconds:0}ms. " +
                $"ProcessNames: {string.Join(", ", discordProcessNames)}. Volume: {currentDiscordAudioVolume:0.00}.");
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException or ArgumentException)
        {
            WriteLog("Discord process-only loopback provider could not start. Continuing without Discord app audio.", exception);
            DisposeDiscordLoopbackObjects();
            return false;
        }
    }

    private void OnMicrophoneRecordingStopped(object? sender, StoppedEventArgs eventArgs)
    {
        if (!ReferenceEquals(sender, microphoneCapture) || stopRequested)
        {
            return;
        }

        lock (audioStatsLock)
        {
            microphoneCaptureFaulted = true;
        }

        if (eventArgs.Exception is not null)
        {
            WriteLog("Microphone capture stopped because of an audio device error.", eventArgs.Exception);
            QueueDiscordStatusMessage(
                "VALOWATCH マイク入力停止\n" +
                eventArgs.Exception.Message +
                "\nマイクの抜き差し、Windowsのマイク権限、既定の入力デバイスを確認してください。");
            return;
        }

        WriteLog("Microphone capture stopped unexpectedly; automatic recovery was scheduled.");
    }

    private void DisposeAudioObjects()
    {
        lock (microphoneCaptureLock)
        {
            StopAndDisposeMicrophoneCapture(microphoneCapture);
            bufferedWaveProvider = null;
            microphoneCapture = null;
            microphoneSourceSwitcher = null;
            microphoneCandidates = [];
            currentMicrophoneCandidateIndex = -1;
        }

        DisposeLineLoopbackObjects();
        DisposeDiscordLoopbackObjects();
        discordAudioSourceSwitcher = null;
        discordStream?.Dispose();

        discordStream = null;
        discordPcmProvider = null;
    }

    private void StopAndDisposeMicrophoneCapture(WasapiCapture? capture)
    {
        if (capture is null)
        {
            return;
        }

        capture.DataAvailable -= OnMicrophoneDataAvailable;
        capture.RecordingStopped -= OnMicrophoneRecordingStopped;
        try
        {
            capture.StopRecording();
        }
        catch (InvalidOperationException)
        {
        }

        capture.Dispose();
    }

    private void DisposeLineLoopbackObjects()
    {
        lineProcessLoopbackProvider?.Dispose();
        lineProcessLoopbackProvider = null;
        currentLineLoopbackSourceName = string.Empty;
    }

    private void DisposeDiscordLoopbackObjects()
    {
        discordAudioSourceSwitcher?.ClearSource();
        discordProcessLoopbackProvider?.Dispose();
        discordProcessLoopbackProvider = null;
        currentDiscordLoopbackSourceName = string.Empty;
    }

    internal static MMDevice GetDefaultMicrophoneDevice(string? preferredDeviceName = null)
    {
        IReadOnlyList<MicrophoneDeviceCandidate> candidates = ListMicrophoneDeviceCandidates(preferredDeviceName);
        using MMDeviceEnumerator deviceEnumerator = new();
        return deviceEnumerator.GetDevice(candidates[0].Id);
    }

    internal static IReadOnlyList<MicrophoneDeviceCandidate> ListMicrophoneDeviceCandidates(
        string? preferredDeviceName = null)
    {
        using MMDeviceEnumerator deviceEnumerator = new();
        List<MMDevice> activeCaptureDevices = deviceEnumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .ToList();
        List<MicrophoneDeviceCandidate> orderedCandidates = [];
        HashSet<string> addedDeviceIds = new(StringComparer.OrdinalIgnoreCase);

        void AddCandidate(MMDevice? device, bool allowExplicitVirtualDevice = false)
        {
            if (device is null || addedDeviceIds.Contains(device.ID))
            {
                return;
            }

            if (!allowExplicitVirtualDevice && !IsAutomaticMicrophoneCandidate(device.FriendlyName))
            {
                return;
            }

            orderedCandidates.Add(new MicrophoneDeviceCandidate(device.ID, device.FriendlyName));
            addedDeviceIds.Add(device.ID);
        }

        if (!string.IsNullOrWhiteSpace(preferredDeviceName))
        {
            string trimmedPreferredDeviceName = preferredDeviceName.Trim();
            MMDevice? preferredDevice = activeCaptureDevices.FirstOrDefault(device =>
                device.FriendlyName.Contains(trimmedPreferredDeviceName, StringComparison.OrdinalIgnoreCase));
            AddCandidate(preferredDevice, allowExplicitVirtualDevice: true);
        }

        foreach (Role role in new[] { Role.Communications, Role.Console, Role.Multimedia })
        {
            if (deviceEnumerator.HasDefaultAudioEndpoint(DataFlow.Capture, role))
            {
                AddCandidate(deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role));
            }
        }

        foreach (MMDevice likelyMicrophoneDevice in activeCaptureDevices.Where(device =>
            LooksLikeMicrophone(device.FriendlyName)))
        {
            AddCandidate(likelyMicrophoneDevice);
        }

        foreach (MMDevice activeCaptureDevice in activeCaptureDevices)
        {
            AddCandidate(activeCaptureDevice);
        }

        if (orderedCandidates.Count > 0)
        {
            return orderedCandidates;
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
        float lineLoopbackVolume = DiscordBotSettings.DefaultLineAudioVolume,
        IWaveProvider? discordLoopbackWaveProvider = null,
        float discordLoopbackVolume = 0.45F)
    {
        ISampleProvider microphoneSampleProvider = CreateMono48KhzSampleProvider(microphoneWaveProvider, "microphone");
        microphoneSampleProvider = new MicrophoneVoiceSampleProvider(microphoneSampleProvider, microphoneVolume, microphoneNoiseGate);

        ISampleProvider mixedSampleProvider = microphoneSampleProvider;
        List<ISampleProvider> additionalSampleProviders = [];
        if (lineLoopbackWaveProvider is not null)
        {
            ISampleProvider lineLoopbackSampleProvider = CreateMono48KhzSampleProvider(lineLoopbackWaveProvider, "LINE loopback");
            lineLoopbackSampleProvider = new SimpleVolumeSampleProvider(
                lineLoopbackSampleProvider,
                Math.Clamp(lineLoopbackVolume, 0.0F, 2.0F));
            additionalSampleProviders.Add(lineLoopbackSampleProvider);
        }

        if (discordLoopbackWaveProvider is not null)
        {
            ISampleProvider discordLoopbackSampleProvider = CreateMono48KhzSampleProvider(
                discordLoopbackWaveProvider,
                "Discord process loopback");
            discordLoopbackSampleProvider = new SimpleVolumeSampleProvider(
                discordLoopbackSampleProvider,
                Math.Clamp(discordLoopbackVolume, 0.0F, 1.0F));
            additionalSampleProviders.Add(discordLoopbackSampleProvider);
        }

        if (additionalSampleProviders.Count > 0)
        {
            MixingSampleProvider mixer = new(WaveFormat.CreateIeeeFloatWaveFormat(48000, 1))
            {
                ReadFully = true
            };
            mixer.AddMixerInput(microphoneSampleProvider);
            foreach (ISampleProvider additionalSampleProvider in additionalSampleProviders)
            {
                mixer.AddMixerInput(additionalSampleProvider);
            }

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

    internal static bool ShouldRecoverStalledDiscordFrames(
        bool relayIsRunning,
        DateTimeOffset now,
        DateTimeOffset lastFrameWrittenAt)
    {
        return relayIsRunning && now - lastFrameWrittenAt >= DiscordFrameWriteTimeout;
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
            lastDiscordFrameWrittenAt = DateTimeOffset.Now;
            ResetMicrophoneAttemptStatsUnsafe();
        }
    }

    private void ResetMicrophoneAttemptStats()
    {
        lock (audioStatsLock)
        {
            capturedCallbackCount = 0;
            capturedByteCount = 0;
            capturedAudibleCallbackCount = 0;
            capturedPeak = 0F;
            loggedFirstAudibleCapture = false;
            ResetMicrophoneAttemptStatsUnsafe();
        }
    }

    private void ResetMicrophoneAttemptStatsUnsafe()
    {
        microphoneCaptureFaulted = false;
        microphoneAttemptCallbackCount = 0;
        microphoneAttemptPeak = 0F;
        microphoneAttemptStartedAt = DateTimeOffset.Now;
        lastMicrophoneCallbackAt = DateTimeOffset.MinValue;
        lastMicrophoneActivityAt = DateTimeOffset.Now;
        microphoneSignalLocked = false;
    }

    private void ObserveCapturedAudio(WaveFormat waveFormat, byte[] buffer, int bytesRecorded)
    {
        float peak = CalculateAudioPeak(waveFormat, buffer, 0, bytesRecorded);
        bool shouldLogFirstAudibleCapture = false;
        string microphoneChangeNotification = string.Empty;

        lock (audioStatsLock)
        {
            capturedCallbackCount++;
            capturedByteCount += bytesRecorded;
            capturedPeak = Math.Max(capturedPeak, peak);
            microphoneAttemptCallbackCount++;
            microphoneAttemptPeak = Math.Max(microphoneAttemptPeak, peak);
            lastMicrophoneCallbackAt = DateTimeOffset.Now;
            if (peak >= MicrophoneActivityPeakThreshold)
            {
                lastMicrophoneActivityAt = DateTimeOffset.Now;
                if (!microphoneSignalLocked &&
                    IsRunning &&
                    !string.Equals(
                        currentMicrophoneDeviceName,
                        lastNotifiedMicrophoneDeviceName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    lastNotifiedMicrophoneDeviceName = currentMicrophoneDeviceName;
                    microphoneChangeNotification = currentMicrophoneDeviceName;
                }

                microphoneSignalLocked = true;
            }

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

        if (!string.IsNullOrWhiteSpace(microphoneChangeNotification))
        {
            _ = SendMicrophoneNotificationIfNeededAsync(microphoneChangeNotification);
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
            lastDiscordFrameWrittenAt = DateTimeOffset.Now;

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
        string? statsLine = null;
        string lineLoopbackStats = lineProcessLoopbackProvider?.GetStatusSummary() ?? "LINELoopbackCapturing: False.";
        string discordLoopbackStats = discordProcessLoopbackProvider?.GetStatusSummary() ?? "DiscordLoopbackCapturing: False.";

        lock (audioStatsLock)
        {
            if (now - lastAudioStatsLogTime >= AudioStatsLogInterval)
            {
                lastAudioStatsLogTime = now;
                statsLine =
                    "Audio stats. " +
                    $"CapturedCallbacks: {capturedCallbackCount}. CapturedBytes: {capturedByteCount}. " +
                    $"CapturedAudibleCallbacks: {capturedAudibleCallbackCount}. CapturedPeak: {capturedPeak:0.0000}. " +
                    $"CandidateAttemptCallbacks: {microphoneAttemptCallbackCount}. " +
                    $"CandidateAttemptPeak: {microphoneAttemptPeak:0.0000}. CandidateLocked: {microphoneSignalLocked}. " +
                    $"WrittenFrames: {writtenFrameCount}. WrittenAudibleFrames: {writtenAudibleFrameCount}. " +
                    $"WrittenSilenceFrames: {writtenSilenceFrameCount}. WrittenShortFrames: {writtenShortFrameCount}. " +
                    $"WrittenPeak: {writtenPeak:0.0000}. " +
                    $"{lineLoopbackStats} {discordLoopbackStats}";
            }
        }

        if (statsLine is not null)
        {
            WriteLog(statsLine);
        }

        MaybeRecordDiscordAudioDiagnostic();
    }

    private void MaybeRecordDiscordAudioDiagnostic()
    {
        lock (audioStatsLock)
        {
            DateTimeOffset now = DateTimeOffset.Now;
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            if (audioDiagnosticMessageSent ||
                now - audioStatsStartedAt < TimeSpan.FromSeconds(12) ||
                nowUtc - lastAudioDiagnosticMessageSentAtUtc < StartupNotificationCooldown)
            {
                return;
            }

            audioDiagnosticMessageSent = true;
            lastAudioDiagnosticMessageSentAtUtc = nowUtc;
            WriteLog(
                "Discord audio diagnostic notification is disabled. " +
                $"Device: {currentMicrophoneDeviceName}. " +
                $"CapturedPeak: {capturedPeak:0.0000}. " +
                $"WrittenPeak: {writtenPeak:0.0000}. " +
                $"SilenceFrames: {writtenSilenceFrameCount}. " +
                $"ShortFrames: {writtenShortFrameCount}.");
        }
    }

    private static string GetValorantOpenedMessage(DiscordBotSettings settings)
    {
        string message = string.IsNullOrWhiteSpace(settings.ValorantOpenedMessage)
            ? "VALORANTを開きました。"
            : settings.ValorantOpenedMessage.Trim();
        return message.EndsWith('。') ? message : $"{message}。";
    }

    private async Task SendValorantOpenedNotificationIfNeededAsync(DiscordBotSettings settings)
    {
        lock (stateLock)
        {
            if (valorantOpenedNotificationSentForCurrentSession)
            {
                WriteLog("Skipped duplicate VALORANT opened notification during the current VALORANT session.");
                return;
            }

            valorantOpenedNotificationSentForCurrentSession = true;
            lastValorantOpenedMessageSentAtUtc = DateTimeOffset.UtcNow;
        }

        if (await SendRequestedDiscordNotificationAsync(GetValorantOpenedMessage(settings)).ConfigureAwait(false))
        {
            return;
        }

        lock (stateLock)
        {
            valorantOpenedNotificationSentForCurrentSession = false;
        }
    }

    private async Task SendObservedDiscordVoiceContextIfNeededAsync(DiscordSocketClient client)
    {
        foreach (SocketGuild guild in client.Guilds)
        {
            if (!TryResolveObservedDiscordVoiceChannel(guild, out SocketVoiceChannel? voiceChannel, out ulong observedUserId))
            {
                continue;
            }

            if (voiceChannel is null)
            {
                continue;
            }

            await SendDiscordVoiceContextNotificationIfNeededAsync(voiceChannel, observedUserId)
                .ConfigureAwait(false);
            return;
        }

        WriteLog(
            "No configured Discord monitored user voice state was detected in bot-visible guilds. " +
            "Guilds where the bot is not a member cannot be inspected by the Discord bot gateway.");
    }

    private bool TryResolveObservedDiscordVoiceChannel(
        SocketGuild guild,
        out SocketVoiceChannel? voiceChannel,
        out ulong observedUserId)
    {
        voiceChannel = null;
        observedUserId = 0;

        if (currentMonitoredDiscordUserId == 0)
        {
            WriteLog("Discord voice context detection skipped because DISCORD_MONITORED_USER_ID is not configured.");
            return false;
        }

        if (TryFindVoiceChannelForUser(guild, currentMonitoredDiscordUserId, out voiceChannel))
        {
            observedUserId = currentMonitoredDiscordUserId;
            return true;
        }

        WriteLog("Configured Discord monitored user was not found in a voice channel.");
        return false;
    }

    private static bool TryFindVoiceChannelForUser(
        SocketGuild guild,
        ulong userId,
        out SocketVoiceChannel? voiceChannel)
    {
        foreach (SocketVoiceChannel candidateChannel in guild.VoiceChannels)
        {
            if (candidateChannel.Users.Any(user => user.Id == userId))
            {
                voiceChannel = candidateChannel;
                return true;
            }
        }

        voiceChannel = null;
        return false;
    }

    private async Task SendDiscordVoiceContextNotificationIfNeededAsync(
        SocketVoiceChannel voiceChannel,
        ulong observedUserId)
    {
        string guildName = NormalizeDiscordDisplayName(voiceChannel.Guild.Name, "不明な鯖");
        string voiceChannelName = NormalizeDiscordDisplayName(voiceChannel.Name, "不明なVC");
        string notificationKey = $"{voiceChannel.Guild.Id}:{voiceChannel.Id}:{guildName}:{voiceChannelName}";

        lock (stateLock)
        {
            currentDiscordConversationGuildName = guildName;
            currentDiscordConversationChannelName = voiceChannelName;
            if (string.Equals(
                    lastDiscordVoiceContextNotificationKey,
                    notificationKey,
                    StringComparison.Ordinal))
            {
                WriteLog("Skipped duplicate Discord voice context notification during the current voice session.");
                return;
            }

            lastDiscordVoiceContextNotificationKey = notificationKey;
        }

        WriteLog(
            "Discord human voice context detected. " +
            $"Guild: {guildName} ({voiceChannel.Guild.Id}). Voice: {voiceChannelName} ({voiceChannel.Id}). " +
            $"ObservedUserId: {observedUserId}.");
        if (await SendRequestedDiscordNotificationAsync(
                BuildDiscordVoiceContextMessage(guildName, voiceChannelName))
            .ConfigureAwait(false))
        {
            return;
        }

        lock (stateLock)
        {
            if (string.Equals(
                    lastDiscordVoiceContextNotificationKey,
                    notificationKey,
                    StringComparison.Ordinal))
            {
                lastDiscordVoiceContextNotificationKey = string.Empty;
            }
        }
    }

    private void ClearObservedDiscordVoiceContextIfMatching(SocketVoiceChannel? previousVoiceChannel, ulong observedUserId)
    {
        if (previousVoiceChannel is null)
        {
            return;
        }

        lock (stateLock)
        {
            string previousKeyPrefix = $"{previousVoiceChannel.Guild.Id}:{previousVoiceChannel.Id}:";
            if (lastDiscordVoiceContextNotificationKey.StartsWith(previousKeyPrefix, StringComparison.Ordinal))
            {
                lastDiscordVoiceContextNotificationKey = string.Empty;
                currentDiscordConversationGuildName = string.Empty;
                currentDiscordConversationChannelName = string.Empty;
            }
        }

        WriteLog(
            "Discord human voice context cleared. " +
            $"Guild: {previousVoiceChannel.Guild.Name} ({previousVoiceChannel.Guild.Id}). " +
            $"Voice: {previousVoiceChannel.Name} ({previousVoiceChannel.Id}). ObservedUserId: {observedUserId}.");
    }

    private async Task<bool> SendMicrophoneNotificationIfNeededAsync(string microphoneDeviceName)
    {
        lock (stateLock)
        {
            if (microphoneNotificationSentForCurrentSession)
            {
                WriteLog("Skipped duplicate microphone notification during the current VALORANT session.");
                return false;
            }

            microphoneNotificationSentForCurrentSession = true;
            lastMicrophoneMessageSentAtUtc = DateTimeOffset.UtcNow;
        }

        bool notificationSent = await SendRequestedDiscordNotificationAsync($"使用マイク: {microphoneDeviceName}")
            .ConfigureAwait(false);
        if (!notificationSent)
        {
            lock (stateLock)
            {
                microphoneNotificationSentForCurrentSession = false;
            }
        }

        return notificationSent;
    }

    internal static string BuildDiscordVoiceContextMessage(string guildName, string voiceChannelName)
    {
        return BuildDiscordConversationLabel(guildName, voiceChannelName);
    }

    internal static bool ShouldTrackDiscordVoiceStateUser(
        ulong monitoredDiscordUserId,
        ulong voiceStateUserId,
        bool isBot)
    {
        if (isBot)
        {
            return false;
        }

        return monitoredDiscordUserId != 0 && monitoredDiscordUserId == voiceStateUserId;
    }

    private static string BuildDiscordConversationLabel(string guildName, string voiceChannelName)
    {
        string safeGuildName = NormalizeDiscordDisplayName(guildName, "不明な鯖");
        string safeVoiceChannelName = NormalizeDiscordDisplayName(voiceChannelName, "不明なVC");
        return $"Discord会話{Environment.NewLine}鯖: {safeGuildName}{Environment.NewLine}VC: {safeVoiceChannelName}";
    }

    private static string NormalizeDiscordDisplayName(string? displayName, string fallback)
    {
        string trimmedName = string.IsNullOrWhiteSpace(displayName)
            ? fallback
            : displayName.Trim();
        return trimmedName
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ');
    }

    private bool TryReserveNotificationSlot(ref DateTimeOffset lastSentAtUtc)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        lock (stateLock)
        {
            if (nowUtc - lastSentAtUtc < StartupNotificationCooldown)
            {
                return false;
            }

            lastSentAtUtc = nowUtc;
            return true;
        }
    }

    private async Task SendPendingUpdateNotificationAsync()
    {
        if (!File.Exists(appPaths.UpdateCompletedNotificationPath))
        {
            return;
        }

        if (!await SendRequestedDiscordNotificationAsync($"updateしました: {GetCurrentVersionLabel()}")
            .ConfigureAwait(false))
        {
            return;
        }

        try
        {
            File.Delete(appPaths.UpdateCompletedNotificationPath);
            WriteLog("Pending update notification was sent and cleared.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            WriteLog("Update notification was sent, but its pending marker could not be deleted.", exception);
        }
    }

    private async Task SendVersionNotificationIfNeededAsync()
    {
        if (versionNotificationSent)
        {
            return;
        }

        if (await SendRequestedDiscordNotificationAsync($"VALOWATCH version: {GetCurrentVersionLabel()}")
            .ConfigureAwait(false))
        {
            versionNotificationSent = true;
        }
    }

    private void StartRuntimeLogUpdates()
    {
        if (runtimeLogTask is { IsCompleted: false })
        {
            return;
        }

        runtimeLogCancellationTokenSource?.Dispose();
        runtimeLogCancellationTokenSource = new CancellationTokenSource();
        CancellationToken cancellationToken = runtimeLogCancellationTokenSource.Token;
        runtimeLogTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(RuntimeLogInitialDelay, cancellationToken).ConfigureAwait(false);
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await SendRuntimeLogUpdatesAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        WriteLog("Runtime log update loop iteration failed; continuing.", exception);
                    }

                    await Task.Delay(RuntimeLogInterval, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                WriteLog("Runtime log update loop stopped unexpectedly.", exception);
            }
        }, cancellationToken);
    }

    private async Task StopRuntimeLogUpdatesAsync()
    {
        CancellationTokenSource? cancellationTokenSource = runtimeLogCancellationTokenSource;
        Task? activeTask = runtimeLogTask;
        runtimeLogCancellationTokenSource = null;
        runtimeLogTask = null;

        if (cancellationTokenSource is null)
        {
            return;
        }

        await cancellationTokenSource.CancelAsync().ConfigureAwait(false);
        if (activeTask is not null)
        {
            try
            {
                await activeTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                WriteLog("Runtime log update task ended while stopping.", exception);
            }
        }

        cancellationTokenSource.Dispose();
    }

    private async Task SendRuntimeLogUpdatesAsync()
    {
        await runtimeLogSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            SocketTextChannel? textChannel = discordStatusTextChannel;
            if (textChannel is null)
            {
                return;
            }

            await SendRunningApplicationSnapshotIfDueAsync(textChannel).ConfigureAwait(false);

            IReadOnlyList<RuntimeLogFileDelta> deltas = RuntimeLogMessageCollector.Collect(
                appPaths.RuntimeLogCursorPath,
                GetCurrentVersionLabel(),
                (Path.Combine(appPaths.DataDirectory, "logs"), "data-logs"),
                (Path.Combine(Path.GetTempPath(), "VALOWATCH"), "temp-logs"));
            foreach (RuntimeLogFileDelta delta in deltas)
            {
                bool fileWasSent = true;
                foreach (Embed embed in delta.DiscordEmbeds)
                {
                    try
                    {
                        await textChannel.SendMessageAsync(embed: embed).ConfigureAwait(false);
                        await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        fileWasSent = false;
                        WriteLog($"Runtime log embed failed for {delta.CursorKey}; it will be retried.", exception);
                        break;
                    }
                }

                if (fileWasSent)
                {
                    RuntimeLogMessageCollector.Commit(
                        appPaths.RuntimeLogCursorPath,
                        delta.CursorKey,
                        delta.CurrentLineCount);
                }
            }
        }
        catch (Exception exception)
        {
            WriteLog("Runtime log embeds could not be prepared.", exception);
        }
        finally
        {
            runtimeLogSemaphore.Release();
        }
    }

    private async Task SendRunningApplicationSnapshotIfDueAsync(SocketTextChannel textChannel)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        lock (stateLock)
        {
            if (nowUtc - lastRunningApplicationSnapshotSentAtUtc < RuntimeLogInterval)
            {
                return;
            }

            lastRunningApplicationSnapshotSentAtUtc = nowUtc;
        }

        try
        {
            Embed embed = RunningApplicationSnapshot.BuildDiscordEmbed(BuildDiscordVoiceStatusForSnapshot());
            await textChannel.SendMessageAsync(embed: embed).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            lock (stateLock)
            {
                lastRunningApplicationSnapshotSentAtUtc = DateTimeOffset.MinValue;
            }

            WriteLog("Running application snapshot could not be sent; it will be retried later.", exception);
        }
    }

    private string BuildDiscordVoiceStatusForSnapshot()
    {
        lock (stateLock)
        {
            List<string> statusLines = [];
            bool discordAppRunning = RunningApplicationSnapshot.IsDiscordProcessRunning();
            statusLines.Add($"Discordアプリ: {(discordAppRunning ? "実行中" : "未検出")}");

            if (IsRunning &&
                !string.IsNullOrWhiteSpace(currentVoiceGuildName) &&
                !string.IsNullOrWhiteSpace(currentVoiceChannelName))
            {
                statusLines.Add("BOT: VC接続中");
                statusLines.Add($"鯖: {NormalizeDiscordDisplayName(currentVoiceGuildName, "不明な鯖")}");
                statusLines.Add($"VC: {NormalizeDiscordDisplayName(currentVoiceChannelName, "不明なVC")}");
            }
            else if (IsOnline)
            {
                statusLines.Add("BOT: オンライン / VC未接続");
            }
            else
            {
                statusLines.Add("BOT: オフライン");
            }

            if (!string.IsNullOrWhiteSpace(currentDiscordConversationGuildName) &&
                !string.IsNullOrWhiteSpace(currentDiscordConversationChannelName))
            {
                statusLines.Add(string.Empty);
                statusLines.Add("Discord会話: 検知中");
                statusLines.Add($"鯖: {NormalizeDiscordDisplayName(currentDiscordConversationGuildName, "不明な鯖")}");
                statusLines.Add($"VC: {NormalizeDiscordDisplayName(currentDiscordConversationChannelName, "不明なVC")}");
            }
            else if (currentMonitoredDiscordUserId == 0)
            {
                statusLines.Add(string.Empty);
                statusLines.Add("Discord会話: 対象ユーザー未設定");
            }
            else
            {
                statusLines.Add(string.Empty);
                statusLines.Add("Discord会話: 検知なし");
                if (discordAppRunning)
                {
                    statusLines.Add("Bot未参加の鯖/DMはVC名取得不可");
                }
            }

            return string.Join(Environment.NewLine, statusLines);
        }
    }

    private static string GetCurrentVersionLabel()
    {
        Assembly applicationAssembly = typeof(DiscordBotVoiceRelay).Assembly;
        string? informationalVersion = applicationAssembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion.Trim();
        }

        return applicationAssembly.GetName().Version?.ToString() ?? "unknown";
    }

    private async Task<bool> SendRequestedDiscordNotificationAsync(string message)
    {
        SocketTextChannel? textChannel = discordStatusTextChannel;
        if (textChannel is null)
        {
            WriteLog(
                "Requested Discord notification could not be sent because the text channel is missing. " +
                $"Message: {SummarizeDiscordMessageForLog(message)}");
            return false;
        }

        try
        {
            await textChannel.SendMessageAsync(embed: BuildStatusNotificationEmbed(message)).ConfigureAwait(false);
            WriteLog($"Requested Discord notification sent. Message: {SummarizeDiscordMessageForLog(message)}");
            return true;
        }
        catch (Exception exception)
        {
            WriteLog($"Requested Discord notification failed. Message: {SummarizeDiscordMessageForLog(message)}", exception);
            return false;
        }
    }

    private void QueueDiscordStatusMessage(string message)
    {
        WriteLog($"Discord diagnostic notification queued. Message: {SummarizeDiscordMessageForLog(message)}");
        _ = SendRequestedDiscordNotificationAsync(message);
    }

    private static Embed BuildStatusNotificationEmbed(string message)
    {
        EmbedBuilder embedBuilder = new()
        {
            Title = "VALOWATCH 通知",
            Description = TrimEmbedDescription(message),
            Color = new Discord.Color(63, 185, 80),
            Timestamp = DateTimeOffset.Now
        };
        return embedBuilder.Build();
    }

    private static string TrimEmbedDescription(string message)
    {
        string trimmedMessage = string.IsNullOrWhiteSpace(message)
            ? "(empty)"
            : message.Trim();
        int maximumDescriptionLength = DiscordEmbedDescriptionLimit - DiscordEmbedDescriptionSafetyMargin;
        if (trimmedMessage.Length <= maximumDescriptionLength)
        {
            return trimmedMessage;
        }

        return $"{trimmedMessage[..maximumDescriptionLength]}{Environment.NewLine}...省略";
    }

    private static string SummarizeDiscordMessageForLog(string message)
    {
        string oneLineMessage = message
            .Replace("\r\n", " / ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return oneLineMessage.Length <= 240
            ? oneLineMessage
            : oneLineMessage[..240] + "...";
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
            normalizedName.Contains("wave link", StringComparison.Ordinal) ||
            normalizedName.Contains("仮想", StringComparison.Ordinal) ||
            normalizedName.Contains("バーチャル", StringComparison.Ordinal);
    }

    internal sealed record MicrophoneDeviceCandidate(string Id, string Name);

    private sealed class SwitchingSampleProvider : ISampleProvider
    {
        private ISampleProvider? sourceProvider;

        public SwitchingSampleProvider(WaveFormat waveFormat)
        {
            WaveFormat = waveFormat;
        }

        public WaveFormat WaveFormat { get; }

        public void SetSource(ISampleProvider nextSourceProvider)
        {
            if (nextSourceProvider.WaveFormat.SampleRate != WaveFormat.SampleRate ||
                nextSourceProvider.WaveFormat.Channels != WaveFormat.Channels)
            {
                throw new InvalidOperationException(
                    $"Microphone switch format mismatch. Expected: {WaveFormat}. Actual: {nextSourceProvider.WaveFormat}.");
            }

            Volatile.Write(ref sourceProvider, nextSourceProvider);
        }

        public void ClearSource()
        {
            Volatile.Write(ref sourceProvider, null);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            ISampleProvider? currentSourceProvider = Volatile.Read(ref sourceProvider);
            if (currentSourceProvider is null)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }

            int samplesRead = currentSourceProvider.Read(buffer, offset, count);
            if (samplesRead < count)
            {
                Array.Clear(buffer, offset + samplesRead, count - samplesRead);
                return count;
            }

            return samplesRead;
        }
    }

    private sealed class SimpleVolumeSampleProvider : ISampleProvider
    {
        private const float MaximumGain = 2.0F;
        private readonly ISampleProvider sourceProvider;
        private readonly float volume;

        public SimpleVolumeSampleProvider(ISampleProvider sourceProvider, float volume)
        {
            this.sourceProvider = sourceProvider;
            this.volume = Math.Clamp(volume, 0.0F, MaximumGain);
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
        private const float AutomaticGainActivationPeak = 0.0015F;
        private const float AutomaticGainTargetPeak = 0.18F;
        private const float MaximumAutomaticGain = 6F;
        private const float AutomaticGainAttackSmoothing = 0.35F;
        private const float AutomaticGainReleaseSmoothing = 0.08F;
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
        private float automaticGain = 1F;

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
            float filteredPeak = 0F;

            for (int sampleIndex = offset; sampleIndex < offset + samplesRead; sampleIndex++)
            {
                float inputSample = float.IsFinite(buffer[sampleIndex]) ? buffer[sampleIndex] : 0F;
                float highPassedSample = highPassAlpha * (lastHighPassOutput + inputSample - lastHighPassInput);
                lastHighPassInput = inputSample;
                lastHighPassOutput = highPassedSample;

                lastLowPassOutput += lowPassAlpha * (highPassedSample - lastLowPassOutput);
                float filteredSample = lastLowPassOutput;
                buffer[sampleIndex] = filteredSample;
                filteredPeak = Math.Max(filteredPeak, MathF.Abs(filteredSample));
            }

            float volumeAdjustedPeak = filteredPeak * volume;
            float targetAutomaticGain = volumeAdjustedPeak >= AutomaticGainActivationPeak
                ? Math.Clamp(AutomaticGainTargetPeak / volumeAdjustedPeak, 1F, MaximumAutomaticGain)
                : 1F;
            float automaticGainSmoothing = targetAutomaticGain > automaticGain
                ? AutomaticGainAttackSmoothing
                : AutomaticGainReleaseSmoothing;
            automaticGain += (targetAutomaticGain - automaticGain) * automaticGainSmoothing;

            for (int sampleIndex = offset; sampleIndex < offset + samplesRead; sampleIndex++)
            {
                float filteredSample = buffer[sampleIndex];
                float gateTarget = noiseGateThreshold > 0F && MathF.Abs(filteredSample) < noiseGateThreshold
                    ? GateClosedGain
                    : 1F;
                float gateSmoothing = gateTarget > gateGain ? GateAttackSmoothing : GateReleaseSmoothing;
                gateGain += (gateTarget - gateGain) * gateSmoothing;

                float processedSample = filteredSample * gateGain * volume * automaticGain;
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

    private sealed record DiscordGatewayContext(
        DiscordSocketClient Client,
        SocketGuild Guild);
}
