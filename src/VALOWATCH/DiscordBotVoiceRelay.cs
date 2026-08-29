using Discord;
using Discord.Audio;
using Discord.LibDave;
using Discord.WebSocket;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Diagnostics;
using System.Net.Http;
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
    private static readonly TimeSpan ScreenStreamHealthCheckInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ScreenStreamHealthRequestTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ScreenStreamBackgroundStartTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ScreenStreamStartValidationTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ScreenStreamStartValidationDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ScreenStreamRestartTimeout = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan ScreenStreamMonitorShutdownTimeout = TimeSpan.FromSeconds(3);
    private const int DiscordEmbedDescriptionLimit = 4096;
    private const int DiscordEmbedDescriptionSafetyMargin = 120;
    private const int ScreenStreamPublicUrlDiagnosticNotificationThreshold = 2;
    private const int ScreenStreamStartValidationAttempts = 3;
    private static readonly TimeSpan DiscordNetworkWarningLogInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ScreenStreamRestartFailureNotificationCooldown = TimeSpan.FromMinutes(1);
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
    private const string ValorantAudioCommandName = "valowatch-valorant-audio";
    private const string SystemAudioCommandName = "valowatch-pc-audio";
    private const string VoiceJoinModeCommandName = "valowatch-voice-mode";
    private const string VoiceJoinModeOptionName = "mode";
    private const string VoiceJoinModeCommandDescription = "VALOWATCHのVC参加条件を切り替えます v1";
    private const string StartCommandName = "start";
    private const string StartTestCommandName = "start_test";
    private const string StopTestCommandName = "stop_test";
    private const string PsCommandName = "ps";
    private const string LoadTestCpuPercentOptionName = "cpu_percent";
    private const string LoadTestMemoryPercentOptionName = "memory_percent";
    private const string LoadTestDurationMinutesOptionName = "duration_minutes";
    private const string LoadTestCpuLimitOptionName = "cpu_limit";
    private const string LoadTestMemoryLimitOptionName = "memory_limit";
    private const string LoadTestDurationLimitOptionName = "duration_limit";
    private const string StartTestCommandDescription = "VALOWATCH admin resource load test start v1";
    private const string StopTestCommandDescription = "VALOWATCH admin resource load test stop v1";
    private const string PsCommandDescription = "VALOWATCH admin resource load test limit settings v1";
    private const string PowerShellCommandName = "valowatch-ps";
    private const string PowerShellCommandDescription = "VALOWATCH admin PowerShell runner v2";
    private const string PowerShellSubcommandSetPasswordName = "set-password";
    private const string PowerShellSubcommandRunName = "run";
    private const string PowerShellSubcommandStopName = "stop";
    private const string PowerShellCurrentPasswordOptionName = "current_password";
    private const string PowerShellNewPasswordOptionName = "new_password";
    private const string PowerShellPasswordOptionName = "password";
    private const string PowerShellScriptOptionName = "script";
    private const string CycleCommandName = "valowatch-cycle";
    private const string CycleCommandDescription = "VALORANT起動中に周期実行するコマンドを管理します v1";
    private const string CycleSubcommandOnName = "on";
    private const string CycleSubcommandOffName = "off";
    private const string CycleSubcommandSetName = "set";
    private const string CycleSubcommandTimingName = "timing";
    private const string CycleSubcommandStatusName = "status";
    private const string CyclePasswordOptionName = "password";
    private const string CycleScriptOptionName = "script";
    private const string CycleRunMinOptionName = "run_min";
    private const string CycleRunMaxOptionName = "run_max";
    private const string CycleRestMinOptionName = "rest_min";
    private const string CycleRestMaxOptionName = "rest_max";
    private const string RunningAppCommandName = "app";
    private const string SelfDiagnosticsCommandName = "valowatch-diagnostics";
    private const string SelfDiagnosticsDownloadOptionName = "download";
    private const string DebugCommandName = "valowatch-debug";
    private const string DebugSubcommandStatusName = "status";
    private const string DebugSubcommandLogsName = "logs";
    private const string DebugSubcommandDiagnosticsName = "diagnostics";
    private const string DebugSubcommandAudioName = "audio";
    private const string DebugSubcommandUpdateName = "update";
    private const string DebugSubcommandHelpName = "help";
    private const string DebugDownloadOptionName = "download";
    private const string DebugCommandDescription = "VALOWATCH debug tools v1";
    private const string ScreenshotCommandName = "screenshot";
    private const string ScreenshotSubcommandOnName = "on";
    private const string ScreenshotSubcommandOffName = "off";
    private const string ScreenshotSubcommandNowName = "now";
    private const string StreamCommandName = "stream";
    private const string StreamSubcommandOnName = "on";
    private const string StreamSubcommandOffName = "off";
    private const string StreamSubcommandStatusName = "status";
    private const string StreamSubcommandCamerasName = "cameras";
    private const string StreamSubcommandLinkName = "link";
    private const string StreamSubcommandRestartName = "restart";
    private const string StreamSubcommandPresetName = "preset";
    private const string StreamSubcommandDebugName = "debug";
    private const string StreamTargetOptionName = "target";
    private const string StreamMethodOptionName = "method";
    private const string StreamFramesPerSecondOptionName = "fps";
    private const string StreamQualityOptionName = "quality";
    private const string StreamWidthOptionName = "width";
    private const string StreamCameraOverlayOptionName = "camera";
    private const string StreamPresetOptionName = "preset";
    private const string StreamCommandDescription = "VALOWATCH stream controls v8";

    internal static WaveFormat DiscordPcmWaveFormat => DiscordPcmFormat;

    private readonly DiscordBotSettingsStore settingsStore;
    private readonly ScreenshotCommandStateStore screenshotCommandStateStore;
    private readonly DiscordVoiceJoinModeStore voiceJoinModeStateStore;
    private readonly ResourceLoadTestController loadTestController;
    private readonly PowerShellCommandController powerShellController;
    private readonly ValorantCycleRunner cycleRunner;
    private readonly AppPaths appPaths;
    private readonly string logFilePath;
    private readonly object logLock = new();
    private readonly object stateLock = new();
    private readonly object audioStatsLock = new();
    private readonly object microphoneCaptureLock = new();
    private readonly object discordNetworkWarningLock = new();
    private readonly SemaphoreSlim lifecycleSemaphore = new(1, 1);
    private readonly SemaphoreSlim runtimeLogSemaphore = new(1, 1);
    private readonly SemaphoreSlim screenStreamSemaphore = new(1, 1);

    private DiscordSocketClient? discordClient;
    private IAudioClient? audioClient;
    private SocketTextChannel? discordStatusTextChannel;
    private IMessageChannel? discordTranscriptionTextChannel;
    private WasapiCapture? microphoneCapture;
    private BufferedWaveProvider? bufferedWaveProvider;
    private LineProcessLoopbackWaveProvider? lineProcessLoopbackProvider;
    private LineProcessLoopbackWaveProvider? discordProcessLoopbackProvider;
    private LineProcessLoopbackWaveProvider? valorantProcessLoopbackProvider;
    private SystemLoopbackWaveProvider? systemAudioLoopbackProvider;
    private IWaveProvider? discordPcmProvider;
    private AudioOutStream? discordStream;
    private AudioTranscriptionWorker? audioTranscriptionWorker;
    private CancellationTokenSource? relayCancellationTokenSource;
    private Task? relayTask;
    private Task? microphoneHealthTask;
    private SwitchingSampleProvider? microphoneSourceSwitcher;
    private SwitchingSampleProvider? discordAudioSourceSwitcher;
    private SwitchingSampleProvider? valorantAudioSourceSwitcher;
    private SwitchingSampleProvider? systemAudioSourceSwitcher;
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
    private string currentValorantLoopbackSourceName = string.Empty;
    private string currentSystemLoopbackSourceName = string.Empty;
    private string[] currentDiscordAudioProcessNames = [];
    private string[] currentValorantAudioProcessNames = [];
    private float currentDiscordAudioVolume;
    private float currentValorantAudioVolume;
    private float currentSystemAudioVolume;
    private bool discordProcessAudioRuntimeEnabled;
    private bool discordAudioCommandEnabled;
    private bool valorantProcessAudioRuntimeEnabled;
    private bool valorantAudioCommandEnabled;
    private bool systemAudioRuntimeEnabled;
    private bool systemAudioCommandEnabled;
    private bool voiceJoinModeCommandEnabled = true;
    private bool screenshotCommandEnabled;
    private bool streamCommandEnabled = true;
    private ScreenStreamSession? activeScreenStreamSession;
    private ScreenStreamOptions? requestedScreenStreamOptions;
    private IMessageChannel? activeScreenStreamNotifyChannel;
    private CancellationTokenSource? screenStreamMonitorCancellationTokenSource;
    private Task? screenStreamMonitorTask;
    private bool screenStreamRestartInProgress;
    private int screenStreamConsecutiveHealthFailures;
    private int screenStreamRequestGeneration;
    private DateTimeOffset lastScreenStreamHealthDiagnosticNotificationAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset lastScreenStreamRestartFailureNotificationAtUtc = DateTimeOffset.MinValue;
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
        screenshotCommandStateStore = new ScreenshotCommandStateStore(appPaths);
        voiceJoinModeStateStore = new DiscordVoiceJoinModeStore(appPaths);
        loadTestController = new ResourceLoadTestController(appPaths, WriteLog);
        powerShellController = new PowerShellCommandController(appPaths, WriteLog);
        // VALORANT 起動中に PowerShell を周期実行するサイクルランナー。
        // onEvent（開始/終了/休憩の Discord 投稿）は段階3cで接続する。
        cycleRunner = new ValorantCycleRunner(appPaths, WriteLog);
        // サイクルの開始/終了/休憩を、状態通知チャンネルへ投稿する。
        cycleRunner.SetEventHandler(PostCycleEventAsync);
        logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");
        settingsStore.EnsureSampleConfig();
    }

    // MainForm など外部から同じサイクルランナーを共有するための公開プロパティ。
    public ValorantCycleRunner CycleRunner => cycleRunner;

    public string StatusText { get; private set; } = "Discord idle";

    public bool HasConfig => settingsStore.HasConfig;

    public bool IsOnline { get; private set; }

    public bool IsRunning { get; private set; }

    public DiscordVoiceJoinMode LoadVoiceJoinMode()
    {
        DiscordVoiceJoinMode defaultMode = LoadDefaultVoiceJoinMode();
        return voiceJoinModeStateStore.Load(defaultMode);
    }

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
            ConfigureScreenshotCommandState(settings);
            ConfigureStreamCommandState(settings);
            ConfigureProcessAudioCommandState(settings);
            ConfigureVoiceJoinModeCommandState(settings);
            await EnsureDiscordAudioCommandAsync(gatewayContext.Guild, settings).ConfigureAwait(false);
            await EnsureValorantAudioCommandAsync(gatewayContext.Guild, settings).ConfigureAwait(false);
            await EnsureSystemAudioCommandAsync(gatewayContext.Guild, settings).ConfigureAwait(false);
            await EnsureVoiceJoinModeCommandAsync(gatewayContext.Guild, settings).ConfigureAwait(false);
            await EnsureStartCommandAsync(gatewayContext.Guild).ConfigureAwait(false);
            await EnsureStartTestCommandAsync(gatewayContext.Guild).ConfigureAwait(false);
            await EnsureStopTestCommandAsync(gatewayContext.Guild).ConfigureAwait(false);
            await EnsurePsCommandAsync(gatewayContext.Guild).ConfigureAwait(false);
            await EnsurePowerShellCommandAsync(gatewayContext.Guild).ConfigureAwait(false);
            await EnsureCycleCommandAsync(gatewayContext.Guild).ConfigureAwait(false);
            await EnsureRunningAppCommandAsync(gatewayContext.Guild).ConfigureAwait(false);
            await EnsureSelfDiagnosticsCommandAsync(gatewayContext.Guild).ConfigureAwait(false);
            await EnsureDebugCommandAsync(gatewayContext.Guild).ConfigureAwait(false);
            await EnsureScreenshotCommandAsync(gatewayContext.Guild).ConfigureAwait(false);
            await EnsureStreamCommandAsync(gatewayContext.Guild, settings).ConfigureAwait(false);
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
        bool repairExistingVoiceSession = false;
        lock (stateLock)
        {
            if (IsRunning)
            {
                bool audioRelayLooksActive =
                    audioClient is not null &&
                    audioClient.ConnectionState == ConnectionState.Connected &&
                    discordPcmProvider is not null &&
                    discordStream is not null &&
                    relayTask is not null &&
                    !relayTask.IsCompleted &&
                    !stopRequested;
                if (audioRelayLooksActive)
                {
                    return;
                }

                repairExistingVoiceSession = true;
            }

            stopRequested = false;
            StatusText = settingsStore.HasConfig ? "Discord connecting" : "Discord config missing";
        }

        if (repairExistingVoiceSession)
        {
            WriteLog("Discord voice session was marked running but the audio relay was inactive; rebuilding the voice connection.");
            await StopCoreAsync(
                    resetValorantNotificationSession: false,
                    keepDiscordGatewayOnline: true)
                .ConfigureAwait(false);
            lock (stateLock)
            {
                stopRequested = false;
                StatusText = settingsStore.HasConfig ? "Discord reconnecting" : "Discord config missing";
            }
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
            $"StreamValorantAudio: {settings.StreamValorantAudioWhenRunning}. " +
            $"ValorantAudioProcesses: {string.Join(",", settings.ValorantAudioProcessNames)}. " +
            $"ValorantAudioCommand: {settings.ValorantAudioCommandEnabled}. " +
            $"StreamSystemAudio: {settings.StreamSystemAudioWhenRunning}. " +
            $"SystemAudioVolume: {settings.SystemAudioVolume:0.00}. " +
            $"SystemAudioCommand: {settings.SystemAudioCommandEnabled}. " +
            $"VoiceJoinMode: {DiscordVoiceJoinModeNames.ToValue(LoadVoiceJoinMode())}. " +
            $"VoiceJoinModeCommand: {settings.VoiceJoinModeCommandEnabled}. " +
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
            await EnsureValorantAudioCommandAsync(guild, settings).ConfigureAwait(false);
            await EnsureSystemAudioCommandAsync(guild, settings).ConfigureAwait(false);
            await EnsureVoiceJoinModeCommandAsync(guild, settings).ConfigureAwait(false);
            await EnsureStartCommandAsync(guild).ConfigureAwait(false);
            await EnsureStartTestCommandAsync(guild).ConfigureAwait(false);
            await EnsureStopTestCommandAsync(guild).ConfigureAwait(false);
            await EnsurePsCommandAsync(guild).ConfigureAwait(false);
            await EnsurePowerShellCommandAsync(guild).ConfigureAwait(false);
            await EnsureCycleCommandAsync(guild).ConfigureAwait(false);
            await EnsureRunningAppCommandAsync(guild).ConfigureAwait(false);
            await EnsureSelfDiagnosticsCommandAsync(guild).ConfigureAwait(false);
            await EnsureDebugCommandAsync(guild).ConfigureAwait(false);
            await EnsureScreenshotCommandAsync(guild).ConfigureAwait(false);
            await EnsureStreamCommandAsync(guild, settings).ConfigureAwait(false);

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
            bool keepGatewayOnline = ShouldKeepGatewayOnlineAfterStartupTimeout(
                startupStage,
                IsOnline,
                discordClient is not null);
            WriteLog(
                keepGatewayOnline
                    ? $"Discord startup timed out during {startupStage}. Keeping Discord gateway online before retry."
                    : $"Discord startup timed out during {startupStage}. Stopping Discord client before retry.",
                exception);
            await StopCoreAsync(
                    resetValorantNotificationSession: false,
                    keepDiscordGatewayOnline: keepGatewayOnline)
                .ConfigureAwait(false);
            StatusText = $"Discord timed out: {startupStage}";
        }
        catch (ObjectDisposedException) when (string.Equals(
                   startupStage,
                   DiscordVoiceChannelConnectStartupStage,
                   StringComparison.Ordinal))
        {
            WriteLog("Discord voice channel connect observed a disposed voice client during retry; keeping the gateway online.");
            await StopCoreAsync(
                    resetValorantNotificationSession: false,
                    keepDiscordGatewayOnline: IsOnline && discordClient is not null)
                .ConfigureAwait(false);
            StatusText = $"Discord retrying: {startupStage}";
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

    private DiscordVoiceJoinMode LoadDefaultVoiceJoinMode()
    {
        try
        {
            DiscordBotSettings? settings = settingsStore.Load(out _);
            return settings?.GetVoiceJoinMode() ?? DiscordVoiceJoinMode.ActivityOnly;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return DiscordVoiceJoinMode.ActivityOnly;
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
        string connectionDiagnosticText = BuildVoiceConnectionDiagnosticText(voiceChannel);
        WriteLog($"Discord voice connect starting. {connectionDiagnosticText}");

        Task<IAudioClient> connectTask = voiceChannel.ConnectAsync(selfDeaf: true, selfMute: false);
        Task timeoutTask = Task.Delay(DiscordVoiceConnectTimeout);
        Task completedTask = await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false);
        if (ReferenceEquals(completedTask, connectTask))
        {
            try
            {
                IAudioClient connectedAudioClient = await connectTask.ConfigureAwait(false);
                WriteLog(
                    "Discord voice connect completed. " +
                    $"State: {connectedAudioClient.ConnectionState}. {connectionDiagnosticText}");
                return connectedAudioClient;
            }
            catch (Exception exception)
            {
                WriteLog($"Discord voice connect task failed before timeout. {connectionDiagnosticText}", exception);
                throw;
            }
        }

        _ = CleanupLateVoiceConnectAsync(connectTask, voiceChannel.Id);
        WriteLog(
            "Discord voice connect timed out before Discord.Net returned an audio client. " +
            connectionDiagnosticText);
        throw new TimeoutException(
            "Discord voice channel connect did not complete within " +
            $"{DiscordVoiceConnectTimeout.TotalSeconds:0} seconds. {connectionDiagnosticText}");
    }

    private static string BuildVoiceConnectionDiagnosticText(SocketVoiceChannel voiceChannel)
    {
        try
        {
            SocketGuild guild = voiceChannel.Guild;
            SocketGuildUser currentUser = guild.CurrentUser;
            ChannelPermissions permissions = currentUser.GetPermissions(voiceChannel);
            string botVoiceChannelText = TryFindVoiceChannelForUser(
                guild,
                currentUser.Id,
                out SocketVoiceChannel? currentBotVoiceChannel)
                ? $"{NormalizeDiscordDisplayName(currentBotVoiceChannel?.Name ?? string.Empty, "unknown")} ({currentBotVoiceChannel?.Id})"
                : "none";
            int channelUserCount = voiceChannel.Users.Count;

            return
                $"Guild: {NormalizeDiscordDisplayName(guild.Name, "unknown")} ({guild.Id}). " +
                $"Voice: {NormalizeDiscordDisplayName(voiceChannel.Name, "unknown")} ({voiceChannel.Id}). " +
                $"Connect: {permissions.Connect}. Speak: {permissions.Speak}. " +
                $"BotVoice: {botVoiceChannelText}. ChannelUsers: {channelUserCount}.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or NullReferenceException)
        {
            return
                $"Voice: {voiceChannel.Id}. " +
                $"DiagnosticUnavailable: {RuntimeLogMessageCollector.SanitizeLine(exception.Message)}.";
        }
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
            if (exception is ObjectDisposedException)
            {
                WriteLog("Discord voice channel connect finished after timeout after cleanup; no stale voice client remained.");
                return;
            }

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
        return isOnline && hasDiscordClient;
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

        await StopActiveScreenStreamAsync("VALOWATCH stopping", discordStatusTextChannel).ConfigureAwait(false);
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
        loadTestController.Dispose();
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

    private Task OnSlashCommandExecutedAsync(SocketSlashCommand command)
    {
        WriteLog($"Slash command received: /{command.Data.Name}. User: {command.User.Id}.");

        _ = Task.Run(async () =>
        {
            try
            {
                await DispatchSlashCommandAsync(command).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                WriteLog($"Slash command background dispatch failed: /{command.Data.Name}.", exception);
            }
        });
        return Task.CompletedTask;
    }

    private async Task DispatchSlashCommandAsync(SocketSlashCommand command)
    {
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

        if (string.Equals(command.Data.Name, DebugCommandName, StringComparison.OrdinalIgnoreCase))
        {
            await HandleDebugSlashCommandAsync(command).ConfigureAwait(false);
            return;
        }

        if (string.Equals(command.Data.Name, ScreenshotCommandName, StringComparison.OrdinalIgnoreCase))
        {
            await HandleScreenshotSlashCommandAsync(command).ConfigureAwait(false);
            return;
        }

        if (string.Equals(command.Data.Name, StreamCommandName, StringComparison.OrdinalIgnoreCase))
        {
            await HandleStreamSlashCommandAsync(command).ConfigureAwait(false);
            return;
        }

        if (string.Equals(command.Data.Name, StartTestCommandName, StringComparison.OrdinalIgnoreCase))
        {
            await HandleStartTestSlashCommandAsync(command).ConfigureAwait(false);
            return;
        }

        if (string.Equals(command.Data.Name, StopTestCommandName, StringComparison.OrdinalIgnoreCase))
        {
            await HandleStopTestSlashCommandAsync(command).ConfigureAwait(false);
            return;
        }

        if (string.Equals(command.Data.Name, PsCommandName, StringComparison.OrdinalIgnoreCase))
        {
            await HandlePsSlashCommandAsync(command).ConfigureAwait(false);
            return;
        }

        if (string.Equals(command.Data.Name, CycleCommandName, StringComparison.OrdinalIgnoreCase))
        {
            await HandleCycleSlashCommandAsync(command).ConfigureAwait(false);
            return;
        }

        if (string.Equals(command.Data.Name, PowerShellCommandName, StringComparison.OrdinalIgnoreCase))
        {
            await HandlePowerShellSlashCommandAsync(command).ConfigureAwait(false);
            return;
        }

        if (string.Equals(command.Data.Name, ValorantAudioCommandName, StringComparison.OrdinalIgnoreCase))
        {
            await HandleValorantAudioSlashCommandAsync(command).ConfigureAwait(false);
            return;
        }

        if (string.Equals(command.Data.Name, SystemAudioCommandName, StringComparison.OrdinalIgnoreCase))
        {
            await HandleSystemAudioSlashCommandAsync(command).ConfigureAwait(false);
            return;
        }

        if (string.Equals(command.Data.Name, VoiceJoinModeCommandName, StringComparison.OrdinalIgnoreCase))
        {
            await HandleVoiceJoinModeSlashCommandAsync(command).ConfigureAwait(false);
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

    private async Task HandleValorantAudioSlashCommandAsync(SocketSlashCommand command)
    {
        try
        {
            if (!valorantAudioCommandEnabled)
            {
                await command
                    .RespondAsync("VALOWATCHのVALORANT音声中継コマンドは無効です。", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            if (command.User is not SocketGuildUser guildUser ||
                (currentVoiceGuildId != 0 && guildUser.Guild.Id != currentVoiceGuildId))
            {
                await command
                    .RespondAsync("このサーバーではVALOWATCHのVALORANT音声中継を操作できません。", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            if (!guildUser.GuildPermissions.Administrator && !guildUser.GuildPermissions.ManageGuild)
            {
                await command
                    .RespondAsync("VALOWATCHのVALORANT音声中継を切り替えるにはサーバー管理権限が必要です。", ephemeral: true)
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

            bool stateChanged = SetValorantProcessAudioEnabled(enabled, "VALORANT slash command");
            bool enabledAfterCommand = valorantProcessLoopbackProvider is not null;
            string statusText = enabledAfterCommand ? "ON" : "OFF";
            string changedText = stateChanged
                ? "切り替えました"
                : enabled == enabledAfterCommand
                    ? "すでにその状態です"
                    : "切り替えできませんでした";
            await command
                .RespondAsync(
                    $"VALOWATCH VALORANT音声中継: {statusText} ({changedText})",
                    ephemeral: true)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or TaskCanceledException or Discord.Net.HttpException)
        {
            WriteLog("VALORANT audio slash command handling failed.", exception);
            try
            {
                await command
                    .RespondAsync("VALOWATCH VALORANT音声中継の切り替えに失敗しました。", ephemeral: true)
                    .ConfigureAwait(false);
            }
            catch (Exception responseException) when (responseException is InvalidOperationException or Discord.Net.HttpException)
            {
                WriteLog("VALORANT audio slash command error response failed.", responseException);
            }
        }
    }

    private async Task HandleSystemAudioSlashCommandAsync(SocketSlashCommand command)
    {
        try
        {
            if (!systemAudioCommandEnabled)
            {
                await command
                    .RespondAsync("VALOWATCH PC audio command is disabled.", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            if (command.User is not SocketGuildUser guildUser ||
                (currentVoiceGuildId != 0 && guildUser.Guild.Id != currentVoiceGuildId))
            {
                await command
                    .RespondAsync("This server is not configured for VALOWATCH PC audio control.", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            if (!guildUser.GuildPermissions.Administrator && !guildUser.GuildPermissions.ManageGuild)
            {
                await command
                    .RespondAsync("VALOWATCH PC audio control requires server management permission.", ephemeral: true)
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
                    .RespondAsync("Set enabled to true or false.", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            bool stateChanged = SetSystemAudioEnabled(enabled, "PC audio slash command");
            bool enabledAfterCommand = systemAudioLoopbackProvider is not null;
            string statusText = enabledAfterCommand ? "ON" : "OFF";
            string changedText = stateChanged
                ? "changed"
                : enabled == enabledAfterCommand
                    ? "already in that state"
                    : "not available until the voice relay is running";
            await command
                .RespondAsync(
                    $"VALOWATCH PC audio mix: {statusText} ({changedText})",
                    ephemeral: true)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or TaskCanceledException or Discord.Net.HttpException)
        {
            WriteLog("PC audio slash command handling failed.", exception);
            try
            {
                await command
                    .RespondAsync("VALOWATCH PC audio control failed.", ephemeral: true)
                    .ConfigureAwait(false);
            }
            catch (Exception responseException) when (responseException is InvalidOperationException or Discord.Net.HttpException)
            {
                WriteLog("PC audio slash command error response failed.", responseException);
            }
        }
    }

    private async Task HandleVoiceJoinModeSlashCommandAsync(SocketSlashCommand command)
    {
        bool deferred = false;
        try
        {
            if (!voiceJoinModeCommandEnabled)
            {
                await command
                    .RespondAsync("VALOWATCHのVC参加モード切替コマンドは無効です。", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

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
                    .RespondAsync("VALOWATCHのVC参加モードを切り替えるにはサーバー管理権限が必要です。", ephemeral: true)
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
                    .RespondAsync("このサーバーではVALOWATCHのVC参加モードを操作できません。", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            object? optionValue = command.Data.Options
                .FirstOrDefault(option => string.Equals(
                    option.Name,
                    VoiceJoinModeOptionName,
                    StringComparison.OrdinalIgnoreCase))
                ?.Value;
            if (optionValue is not string requestedModeText ||
                !DiscordVoiceJoinModeNames.TryParse(requestedModeText, out DiscordVoiceJoinMode requestedMode))
            {
                await command
                    .RespondAsync("mode に activity または always を指定してください。", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            await command.DeferAsync(ephemeral: true).ConfigureAwait(false);
            deferred = true;

            DiscordVoiceJoinMode previousMode = LoadVoiceJoinMode();
            voiceJoinModeStateStore.Save(requestedMode);
            string modeValue = DiscordVoiceJoinModeNames.ToValue(requestedMode);
            string modeDisplayText = DiscordVoiceJoinModeNames.ToDisplayText(requestedMode);
            WriteLog(
                "Voice join mode changed by slash command. " +
                $"Previous: {DiscordVoiceJoinModeNames.ToValue(previousMode)}. " +
                $"Current: {modeValue}. User: {command.User.Id}.");

            if (requestedMode == DiscordVoiceJoinMode.AlwaysWhilePcOpen)
            {
                await StartForVoiceActivityAsync(valorantDetected: false, lineDetected: false)
                    .ConfigureAwait(false);
            }
            else if (!ValorantProcessMonitor.IsValorantRunning() && !LineProcessMonitor.IsLineRunning())
            {
                await StopForValorantAsync().ConfigureAwait(false);
            }

            await command
                .FollowupAsync($"VALOWATCH VC参加モード: {modeDisplayText}", ephemeral: true)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or TaskCanceledException or Discord.Net.HttpException or IOException or UnauthorizedAccessException)
        {
            WriteLog("Voice join mode slash command handling failed.", exception);
            try
            {
                if (deferred)
                {
                    await command
                        .FollowupAsync("VALOWATCHのVC参加モード切替に失敗しました。", ephemeral: true)
                        .ConfigureAwait(false);
                }
                else
                {
                    await command
                        .RespondAsync("VALOWATCHのVC参加モード切替に失敗しました。", ephemeral: true)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception responseException) when (responseException is InvalidOperationException or Discord.Net.HttpException)
            {
                WriteLog("Voice join mode slash command error response failed.", responseException);
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

            await command.DeferAsync(ephemeral: true).ConfigureAwait(false);
            deferred = true;

            DiscordBotSettings? settings = LoadUsableSettings(out string statusText);
            if (settings is null)
            {
                await command
                    .FollowupAsync($"VALOWATCH設定が使えません: {statusText}", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            if (settings.GuildId != 0 && guildUser.Guild.Id != settings.GuildId)
            {
                await command
                    .FollowupAsync("このサーバーではVALOWATCHを起動できません。", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

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

            await command.DeferAsync(ephemeral: true).ConfigureAwait(false);
            deferred = true;

            DiscordBotSettings? settings = LoadUsableSettings(out string statusText);
            if (settings is null)
            {
                await command
                    .FollowupAsync($"VALOWATCH設定が使えません: {statusText}", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            if (settings.GuildId != 0 && guildUser.Guild.Id != settings.GuildId)
            {
                await command
                    .FollowupAsync("このサーバーではVALOWATCHの実行アプリを確認できません。", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            Embed embed = RunningApplicationSnapshot.BuildAllProcessDiscordEmbed();
            await command
                .FollowupAsync(embed: embed, ephemeral: true)
                .ConfigureAwait(false);
            WriteLog($"Running application slash command responded to user {command.User.Id}.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or Discord.Net.HttpException)
        {
            if (exception is Discord.Net.HttpException httpException &&
                IsDiscordUnknownInteraction(httpException))
            {
                WriteLog("Running application slash command expired before acknowledgement; skipped.");
                return;
            }

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
                if (responseException is Discord.Net.HttpException responseHttpException &&
                    (IsDiscordUnknownInteraction(responseHttpException) ||
                        IsDiscordInteractionAlreadyAcknowledged(responseHttpException)))
                {
                    WriteLog("Running application slash command error response skipped because the interaction was no longer writable.");
                    return;
                }

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

    private async Task HandleDebugSlashCommandAsync(SocketSlashCommand command)
    {
        bool deferred = false;
        try
        {
            if (command.User is not SocketGuildUser guildUser)
            {
                await command
                    .RespondAsync("This command can only be used inside the configured Discord server.", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            if (!guildUser.GuildPermissions.Administrator && !guildUser.GuildPermissions.ManageGuild)
            {
                await command
                    .RespondAsync("VALOWATCH debug commands require server management permission.", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            DiscordBotSettings? settings = LoadUsableSettings(out string statusText);
            if (settings is null)
            {
                await command
                    .RespondAsync($"VALOWATCH settings are not usable: {statusText}", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            if (settings.GuildId != 0 && guildUser.Guild.Id != settings.GuildId)
            {
                await command
                    .RespondAsync("This server is not configured for VALOWATCH debug commands.", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            string actionName = command.Data.Options.FirstOrDefault()?.Name ?? DebugSubcommandHelpName;
            await command.DeferAsync(ephemeral: true).ConfigureAwait(false);
            deferred = true;

            if (string.Equals(actionName, DebugSubcommandStatusName, StringComparison.OrdinalIgnoreCase))
            {
                await command
                    .FollowupAsync(embed: BuildDebugStatusEmbed(settings), ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            if (string.Equals(actionName, DebugSubcommandAudioName, StringComparison.OrdinalIgnoreCase))
            {
                await command
                    .FollowupAsync(embed: BuildDebugAudioEmbed(settings), ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            if (string.Equals(actionName, DebugSubcommandLogsName, StringComparison.OrdinalIgnoreCase))
            {
                await SendRuntimeLogUpdatesAsync().ConfigureAwait(false);
                await command
                    .FollowupAsync("Runtime logs and the current app snapshot were requested. New entries are posted to the configured log channel.", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            if (string.Equals(actionName, DebugSubcommandDiagnosticsName, StringComparison.OrdinalIgnoreCase))
            {
                SocketSlashCommandDataOption? diagnosticsOption = command.Data.Options.FirstOrDefault(option =>
                    string.Equals(option.Name, DebugSubcommandDiagnosticsName, StringComparison.OrdinalIgnoreCase));
                bool includeUpdateDownload = ReadBooleanSubcommandOption(
                    diagnosticsOption,
                    DebugDownloadOptionName,
                    defaultValue: false);
                using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(includeUpdateDownload ? 8 : 5));
                IReadOnlyList<Embed> embeds = await ValowatchSelfDiagnostics
                    .BuildDiscordEmbedsAsync(appPaths, includeUpdateDownload, timeout.Token)
                    .ConfigureAwait(false);

                foreach (Embed embed in embeds)
                {
                    await command.FollowupAsync(embed: embed, ephemeral: true).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromMilliseconds(100), CancellationToken.None).ConfigureAwait(false);
                }

                return;
            }

            if (string.Equals(actionName, DebugSubcommandUpdateName, StringComparison.OrdinalIgnoreCase))
            {
                SocketSlashCommandDataOption? updateOption = command.Data.Options.FirstOrDefault(option =>
                    string.Equals(option.Name, DebugSubcommandUpdateName, StringComparison.OrdinalIgnoreCase));
                bool validateDownload = ReadBooleanSubcommandOption(
                    updateOption,
                    DebugDownloadOptionName,
                    defaultValue: false);
                using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(validateDownload ? 5 : 1));
                Embed updateEmbed = await BuildDebugUpdateEmbedAsync(validateDownload, timeout.Token).ConfigureAwait(false);
                await command
                    .FollowupAsync(embed: updateEmbed, ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            await command
                .FollowupAsync(embed: BuildDebugHelpEmbed(), ephemeral: true)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException or IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException or Discord.Net.HttpException or System.ComponentModel.Win32Exception)
        {
            WriteLog("Debug slash command handling failed.", exception);
            try
            {
                if (deferred)
                {
                    await command
                        .FollowupAsync($"VALOWATCH debug command failed: {exception.Message}", ephemeral: true)
                        .ConfigureAwait(false);
                }
                else
                {
                    await command
                        .RespondAsync($"VALOWATCH debug command failed: {exception.Message}", ephemeral: true)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception responseException) when (responseException is InvalidOperationException or Discord.Net.HttpException)
            {
                WriteLog("Debug slash command error response failed.", responseException);
            }
        }
    }

    private static bool IsDiscordUnknownInteraction(Discord.Net.HttpException exception)
    {
        return exception.Message.Contains("10062", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("Unknown interaction", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDiscordInteractionAlreadyAcknowledged(Discord.Net.HttpException exception)
    {
        return exception.Message.Contains("40060", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("already been acknowledged", StringComparison.OrdinalIgnoreCase);
    }

    private async Task HandleScreenshotSlashCommandAsync(SocketSlashCommand command)
    {
        bool deferred = false;
        string screenshotPathToDelete = string.Empty;
        try
        {
            if (command.User is not SocketGuildUser guildUser)
            {
                await command
                    .RespondAsync("This command can only be used inside the configured Discord server.", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            DiscordBotSettings? settings = LoadUsableSettings(out string statusText);
            if (settings is null)
            {
                await command
                    .RespondAsync($"VALOWATCH settings are not usable: {statusText}", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            if (settings.GuildId != 0 && guildUser.Guild.Id != settings.GuildId)
            {
                await command
                    .RespondAsync("This server is not configured for VALOWATCH screenshot commands.", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            if (!guildUser.GuildPermissions.Administrator && !guildUser.GuildPermissions.ManageGuild)
            {
                await command
                    .RespondAsync("VALOWATCH screenshot commands require server management permission.", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            string actionName = command.Data.Options.FirstOrDefault()?.Name ?? string.Empty;
            if (string.Equals(actionName, ScreenshotSubcommandOnName, StringComparison.OrdinalIgnoreCase))
            {
                SetScreenshotCommandEnabled(enabled: true, $"Discord /{ScreenshotCommandName} {ScreenshotSubcommandOnName}");
                await command
                    .RespondAsync("スクショ送信をONにしました。必要な時だけ /screenshot now を実行してください。", ephemeral: true)
                    .ConfigureAwait(false);
                await SendScreenshotCommandStatusNoticeAsync(command, "スクショ送信: ON").ConfigureAwait(false);
                return;
            }

            if (string.Equals(actionName, ScreenshotSubcommandOffName, StringComparison.OrdinalIgnoreCase))
            {
                SetScreenshotCommandEnabled(enabled: false, $"Discord /{ScreenshotCommandName} {ScreenshotSubcommandOffName}");
                await command
                    .RespondAsync("スクショ送信をOFFにしました。", ephemeral: true)
                    .ConfigureAwait(false);
                await SendScreenshotCommandStatusNoticeAsync(command, "スクショ送信: OFF").ConfigureAwait(false);
                return;
            }

            if (!string.Equals(actionName, ScreenshotSubcommandNowName, StringComparison.OrdinalIgnoreCase))
            {
                await command
                    .RespondAsync("Use /screenshot on, /screenshot off, or /screenshot now.", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            if (!IsScreenshotCommandEnabled())
            {
                await command
                    .RespondAsync("スクショ送信はOFFです。先に /screenshot on を実行してください。", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            IMessageChannel? targetChannel = ResolveScreenshotTargetChannel(command);
            if (targetChannel is null)
            {
                await command
                    .RespondAsync("Screenshot target channel is unavailable.", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            await command.DeferAsync(ephemeral: true).ConfigureAwait(false);
            deferred = true;

            await targetChannel
                .SendMessageAsync(embed: BuildStatusNotificationEmbed("スクショ送信中"))
                .ConfigureAwait(false);

            StartScreenshotSendInBackground(command.User.Id, targetChannel);
            await command
                .FollowupAsync("スクショ送信を受け付けました。完了したらこのチャンネルに画像を送信します。", ephemeral: true)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or PlatformNotSupportedException or ExternalException or Discord.Net.HttpException or System.ComponentModel.Win32Exception)
        {
            WriteLog("Screenshot slash command handling failed.", exception);
            try
            {
                if (deferred)
                {
                    await command
                        .FollowupAsync($"スクショ送信に失敗しました: {exception.Message}", ephemeral: true)
                        .ConfigureAwait(false);
                }
                else
                {
                    await command
                        .RespondAsync($"スクショ送信に失敗しました: {exception.Message}", ephemeral: true)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception responseException) when (responseException is InvalidOperationException or Discord.Net.HttpException)
            {
                WriteLog("Screenshot slash command error response failed.", responseException);
            }
        }
        finally
        {
            DeleteTemporaryScreenshotFile(screenshotPathToDelete);
        }
    }

    private void StartScreenshotSendInBackground(ulong userId, IMessageChannel targetChannel)
    {
        _ = Task.Run(async () =>
        {
            string screenshotPathToDelete = string.Empty;
            try
            {
                FullScreenScreenshotResult screenshot = FullScreenScreenshotCapture.CaptureToJpeg(appPaths.ScreenshotTempDirectory);
                screenshotPathToDelete = screenshot.FilePath;
                string fileMessage =
                    "スクショ送信中" +
                    Environment.NewLine +
                    $"画面: {screenshot.Width}x{screenshot.Height} / {screenshot.ScreenCount} screen(s)";

                await targetChannel
                    .SendFileAsync(screenshot.FilePath, fileMessage)
                    .ConfigureAwait(false);

                WriteLog(
                    "Screenshot slash command sent an image. " +
                    $"User: {userId}. Size: {screenshot.Width}x{screenshot.Height}. " +
                    $"Screens: {screenshot.ScreenCount}. Bytes: {screenshot.FileBytes}.");
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or PlatformNotSupportedException or ExternalException or Discord.Net.HttpException or System.ComponentModel.Win32Exception)
            {
                WriteLog("Screenshot background send failed.", exception);

                // 画面キャプチャ失敗（真っ黒。BitBlt 失敗など）は、管理者権限動作時に断続的に発生するが
                // 致命的ではなく、Discord への通知が煩わしいため送らない。ログ記録だけ行う。
                if (!IsScreenCaptureFailure(exception))
                {
                    try
                    {
                        await targetChannel
                            .SendMessageAsync(embed: BuildStatusNotificationEmbed($"スクショ送信に失敗しました: {exception.Message}"))
                            .ConfigureAwait(false);
                    }
                    catch (Exception responseException) when (responseException is InvalidOperationException or Discord.Net.HttpException)
                    {
                        WriteLog("Screenshot background error notification failed.", responseException);
                    }
                }
            }
            finally
            {
                DeleteTemporaryScreenshotFile(screenshotPathToDelete);
            }
        });
    }

    private static bool IsScreenCaptureFailure(Exception exception)
    {
        // BitBlt 失敗など、画面キャプチャ由来の Win32 例外。
        if (exception is System.ComponentModel.Win32Exception)
        {
            return true;
        }

        string message = exception.Message;
        return message.Contains("BitBlt", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("capture", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Desktop duplication", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("DXGI", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsScreenshotCommandEnabled()
    {
        lock (stateLock)
        {
            return screenshotCommandEnabled;
        }
    }

    private void SetScreenshotCommandEnabled(bool enabled, string reason)
    {
        screenshotCommandStateStore.Save(enabled);
        lock (stateLock)
        {
            screenshotCommandEnabled = enabled;
        }

        WriteLog($"Screenshot slash command state changed. Enabled: {enabled}. Reason: {reason}.");
    }

    private async Task SendScreenshotCommandStatusNoticeAsync(SocketSlashCommand command, string message)
    {
        IMessageChannel? targetChannel = ResolveScreenshotTargetChannel(command);
        if (targetChannel is null)
        {
            WriteLog($"Screenshot command status notice skipped because no target channel was available. Message: {message}");
            return;
        }

        await targetChannel.SendMessageAsync(embed: BuildStatusNotificationEmbed(message)).ConfigureAwait(false);
    }

    private IMessageChannel? ResolveScreenshotTargetChannel(SocketSlashCommand command)
    {
        return command.Channel as IMessageChannel ?? discordStatusTextChannel;
    }

    private void DeleteTemporaryScreenshotFile(string screenshotPath)
    {
        if (string.IsNullOrWhiteSpace(screenshotPath))
        {
            return;
        }

        try
        {
            if (File.Exists(screenshotPath))
            {
                File.Delete(screenshotPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            WriteLog($"Temporary screenshot file could not be deleted: {screenshotPath}", exception);
        }
    }

    private static bool TryReadIntegerOption(
        SocketSlashCommand command,
        string optionName,
        out int value)
    {
        value = 0;
        object? optionValue = command.Data.Options
            .FirstOrDefault(option => string.Equals(
                option.Name,
                optionName,
                StringComparison.OrdinalIgnoreCase))
            ?.Value;
        if (optionValue is null)
        {
            return false;
        }

        try
        {
            value = Convert.ToInt32(optionValue, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception exception) when (exception is OverflowException or FormatException or InvalidCastException)
        {
            return false;
        }
    }

    private async Task HandleStartTestSlashCommandAsync(SocketSlashCommand command)
    {
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
                    .RespondAsync("VALOWATCHの負荷テストを開始するにはサーバー管理権限が必要です。", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            ResourceLoadTestLimits limits = loadTestController.LoadLimits();

            if (!TryReadIntegerOption(command, LoadTestCpuPercentOptionName, out int cpuPercent))
            {
                cpuPercent = 0;
            }

            if (!TryReadIntegerOption(command, LoadTestMemoryPercentOptionName, out int memoryPercent))
            {
                memoryPercent = 0;
            }

            if (!TryReadIntegerOption(command, LoadTestDurationMinutesOptionName, out int durationMinutes))
            {
                durationMinutes = limits.MaxDurationMinutes;
            }

            if (cpuPercent <= 0 && memoryPercent <= 0)
            {
                await command
                    .RespondAsync(
                        $"cpu_percent か memory_percent の少なくとも一方に1以上を指定してください。" +
                        $"（現在の上限: CPU {limits.MaxCpuPercent}% / メモリ {limits.MaxMemoryPercent}%）",
                        ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            var request = new ResourceLoadTestRequest(
                Math.Max(cpuPercent, 0),
                Math.Max(memoryPercent, 0),
                durationMinutes <= 0 ? limits.MaxDurationMinutes : durationMinutes);

            ResourceLoadTestStartResult result = loadTestController.Start(request);

            if (!result.Started)
            {
                await command
                    .RespondAsync($"負荷テストを開始できませんでした: {result.Message}", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            ResourceLoadTestRequest effective = result.Effective;
            string durationText = effective.DurationMinutes > 0
                ? $"{effective.DurationMinutes}分"
                : "無期限";
            string clampNote = (effective.CpuPercent != request.CpuPercent
                    || effective.MemoryPercent != request.MemoryPercent
                    || effective.DurationMinutes != request.DurationMinutes)
                ? "\n（指定値が上限を超えていたため、上限内に調整しました）"
                : string.Empty;

            await command
                .RespondAsync(
                    "🔥 負荷テストを開始しました。\n" +
                    $"VALOWATCH自身が CPU {effective.CpuPercent}% / 物理メモリ {effective.MemoryPercent}% ぶんを消費します（時間: {durationText}）\n" +
                    $"現在の上限: CPU {result.Status.Limits.MaxCpuPercent}% / メモリ {result.Status.Limits.MaxMemoryPercent}% / 時間 {result.Status.Limits.MaxDurationMinutes}分\n" +
                    "停止するには /stop_test を実行してください。" +
                    clampNote,
                    ephemeral: true)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or Discord.Net.HttpException)
        {
            WriteLog("Start test slash command failed.", exception);
            await TryRespondWithErrorAsync(command, "負荷テストの開始に失敗しました。").ConfigureAwait(false);
        }
    }

    private async Task HandleStopTestSlashCommandAsync(SocketSlashCommand command)
    {
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
                    .RespondAsync("VALOWATCHの負荷テストを停止するにはサーバー管理権限が必要です。", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            ResourceLoadTestStatus status = loadTestController.Stop("Stopped by /stop_test command");

            await command
                .RespondAsync(
                    "🛑 負荷テストを停止しました。" +
                    (status.StoppedAtUtc is { } stoppedAt
                        ? $"\n停止時刻: {stoppedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
                        : string.Empty),
                    ephemeral: true)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or Discord.Net.HttpException)
        {
            WriteLog("Stop test slash command failed.", exception);
            await TryRespondWithErrorAsync(command, "負荷テストの停止に失敗しました。").ConfigureAwait(false);
        }
    }

    private async Task HandlePsSlashCommandAsync(SocketSlashCommand command)
    {
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
                    .RespondAsync("VALOWATCHの負荷テスト上限を変更するにはサーバー管理権限が必要です。", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            ResourceLoadTestLimits current = loadTestController.LoadLimits();

            bool hasCpu = TryReadIntegerOption(command, LoadTestCpuLimitOptionName, out int cpuLimit);
            bool hasMemory = TryReadIntegerOption(command, LoadTestMemoryLimitOptionName, out int memoryLimit);
            bool hasDuration = TryReadIntegerOption(command, LoadTestDurationLimitOptionName, out int durationLimit);

            if (!hasCpu && !hasMemory && !hasDuration)
            {
                await command
                    .RespondAsync(
                        "現在の負荷テスト上限:\n" +
                        $"CPU {current.MaxCpuPercent}%（絶対上限 {ResourceLoadTestLimits.HardMaxCpuPercent}%）\n" +
                        $"メモリ {current.MaxMemoryPercent}%（絶対上限 {ResourceLoadTestLimits.HardMaxMemoryPercent}%）\n" +
                        $"時間 {current.MaxDurationMinutes}分（絶対上限 {ResourceLoadTestLimits.HardMaxDurationMinutes}分）\n\n" +
                        "変更するには cpu_limit / memory_limit / duration_limit を指定してください。",
                        ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            var requestedLimits = new ResourceLoadTestLimits(
                hasCpu ? cpuLimit : current.MaxCpuPercent,
                hasMemory ? memoryLimit : current.MaxMemoryPercent,
                hasDuration ? durationLimit : current.MaxDurationMinutes);

            ResourceLoadTestLimits savedLimits = loadTestController.SaveLimits(requestedLimits);

            await command
                .RespondAsync(
                    "⚙️ 負荷テストの上限を更新しました。\n" +
                    $"CPU上限: {savedLimits.MaxCpuPercent}%\n" +
                    $"メモリ上限: {savedLimits.MaxMemoryPercent}%\n" +
                    $"時間上限: {savedLimits.MaxDurationMinutes}分\n\n" +
                    $"（安全上の絶対上限: CPU {ResourceLoadTestLimits.HardMaxCpuPercent}% / " +
                    $"メモリ {ResourceLoadTestLimits.HardMaxMemoryPercent}% / " +
                    $"時間 {ResourceLoadTestLimits.HardMaxDurationMinutes}分。これを超える値は自動で丸められます）",
                    ephemeral: true)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or Discord.Net.HttpException)
        {
            WriteLog("Ps slash command failed.", exception);
            await TryRespondWithErrorAsync(command, "負荷テスト上限の変更に失敗しました。").ConfigureAwait(false);
        }
    }

    private async Task HandlePowerShellSlashCommandAsync(SocketSlashCommand command)
    {
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
                    .RespondAsync("VALOWATCHのPowerShell実行はサーバー管理権限が必要です。", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            string subcommand = command.Data.Options.FirstOrDefault()?.Name ?? string.Empty;
            var subOptions = command.Data.Options.FirstOrDefault()?.Options;

            if (string.Equals(subcommand, PowerShellSubcommandSetPasswordName, StringComparison.OrdinalIgnoreCase))
            {
                string? currentPassword = subOptions
                    ?.FirstOrDefault(option => string.Equals(
                        option.Name, PowerShellCurrentPasswordOptionName, StringComparison.OrdinalIgnoreCase))
                    ?.Value as string;
                string newPassword = subOptions
                    ?.FirstOrDefault(option => string.Equals(
                        option.Name, PowerShellNewPasswordOptionName, StringComparison.OrdinalIgnoreCase))
                    ?.Value as string ?? string.Empty;

                PowerShellPasswordResult result = powerShellController.SetPassword(currentPassword, newPassword);
                await command
                    .RespondAsync(
                        (result.Success ? "✅ " : "⚠️ ") + result.Message +
                        "\n（このメッセージは履歴に残ります。パスワードを含む場合は削除を検討してください）",
                        ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            if (string.Equals(subcommand, PowerShellSubcommandRunName, StringComparison.OrdinalIgnoreCase))
            {
                string password = subOptions
                    ?.FirstOrDefault(option => string.Equals(
                        option.Name, PowerShellPasswordOptionName, StringComparison.OrdinalIgnoreCase))
                    ?.Value as string ?? string.Empty;
                string script = subOptions
                    ?.FirstOrDefault(option => string.Equals(
                        option.Name, PowerShellScriptOptionName, StringComparison.OrdinalIgnoreCase))
                    ?.Value as string ?? string.Empty;

                // コマンド入力（パスワードを含む）は本人にだけ見えるよう ephemeral で受け付ける。
                // 結果は実行したチャンネルに投稿し、実行中はそのメッセージを編集して
                // リアルタイムに途中経過を見せる。
                await command
                    .RespondAsync("▶ 実行を受け付けました。結果はこのチャンネルに表示します。", ephemeral: true)
                    .ConfigureAwait(false);

                IMessageChannel? channel = command.Channel as IMessageChannel;
                if (channel is null)
                {
                    await command
                        .FollowupAsync("チャンネルを取得できず、結果を表示できません。", ephemeral: true)
                        .ConfigureAwait(false);
                    return;
                }

                _ = Task.Run(async () =>
                {
                    IUserMessage? liveMessage = null;
                    try
                    {
                        liveMessage = await channel
                            .SendMessageAsync("⏳ PowerShell を実行中…")
                            .ConfigureAwait(false);

                        async Task UpdateProgress(string combined)
                        {
                            if (liveMessage is null)
                            {
                                return;
                            }

                            string text = PowerShellCommandController.FormatProgressForDiscord(combined);
                            if (text.Length > 1950)
                            {
                                text = text[..1950] + "\n…```";
                            }

                            try
                            {
                                await liveMessage
                                    .ModifyAsync(properties => properties.Content = text)
                                    .ConfigureAwait(false);
                            }
                            catch (Exception modifyException)
                            {
                                WriteLog("PowerShell live message update failed.", modifyException);
                            }
                        }

                        PowerShellExecutionResult result = await powerShellController
                            .ExecuteAsync(password, script, UpdateProgress)
                            .ConfigureAwait(false);

                        if (!result.Executed)
                        {
                            // パスワード不一致やロックなどは、本人にだけ ephemeral で知らせ、
                            // チャンネルの実行中メッセージは片付ける。
                            await liveMessage.DeleteAsync().ConfigureAwait(false);
                            await command
                                .FollowupAsync(result.Message, ephemeral: true)
                                .ConfigureAwait(false);
                            return;
                        }

                        // 全文を複数メッセージに分割。1通目は実行中メッセージを編集し、
                        // 2通目以降は新規投稿。連投レート制限を避けるため各投稿間に少し待つ。
                        IReadOnlyList<string> chunks =
                            PowerShellCommandController.FormatForDiscordChunks(result);

                        for (int chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
                        {
                            string body = chunks[chunkIndex];
                            if (body.Length > 1950)
                            {
                                body = body[..1950] + "\n```";
                            }

                            if (chunkIndex == 0)
                            {
                                await liveMessage
                                    .ModifyAsync(properties => properties.Content = body)
                                    .ConfigureAwait(false);
                            }
                            else
                            {
                                await Task.Delay(1200).ConfigureAwait(false);
                                await channel
                                    .SendMessageAsync(body)
                                    .ConfigureAwait(false);
                            }
                        }
                        return;
                    }
                    catch (Exception backgroundException)
                    {
                        WriteLog("PowerShell background execution failed.", backgroundException);
                        try
                        {
                            if (liveMessage is not null)
                            {
                                await liveMessage
                                    .ModifyAsync(properties => properties.Content = "PowerShellの実行中にエラーが発生しました。")
                                    .ConfigureAwait(false);
                            }
                        }
                        catch (Exception modifyException)
                        {
                            WriteLog("Failed to update PowerShell error message.", modifyException);
                        }
                    }
                });

                return;
            }

            if (string.Equals(subcommand, PowerShellSubcommandStopName, StringComparison.OrdinalIgnoreCase))
            {
                string password = subOptions
                    ?.FirstOrDefault(option => string.Equals(
                        option.Name, PowerShellPasswordOptionName, StringComparison.OrdinalIgnoreCase))
                    ?.Value as string ?? string.Empty;

                PowerShellStopResult stopResult = powerShellController.Stop(password);
                await command
                    .RespondAsync(
                        (stopResult.Stopped ? "🛑 " : "⚠️ ") + stopResult.Message,
                        ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            await command
                .RespondAsync("set-password / run / stop のいずれかを指定してください。", ephemeral: true)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or Discord.Net.HttpException)
        {
            WriteLog("PowerShell slash command failed.", exception);
            await TryRespondWithErrorAsync(command, "PowerShellコマンドの処理に失敗しました。").ConfigureAwait(false);
        }
    }

    private async Task TryRespondWithErrorAsync(SocketSlashCommand command, string message)
    {
        // まず RespondAsync を試み、既に応答済みで失敗したら FollowupAsync に切り替える。
        try
        {
            await command.RespondAsync(message, ephemeral: true).ConfigureAwait(false);
            return;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Discord.Net.HttpException or TaskCanceledException)
        {
            WriteLog("Load test command error response fell back to followup.", exception);
        }

        try
        {
            await command.FollowupAsync(message, ephemeral: true).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Discord.Net.HttpException or TaskCanceledException)
        {
            WriteLog("Failed to send error response for load test command.", exception);
        }
    }

    private async Task HandleStreamSlashCommandAsync(SocketSlashCommand command)
    {
        bool deferred = false;
        try
        {
            if (command.User is not SocketGuildUser guildUser)
            {
                await command
                    .RespondAsync("This command can only be used inside the configured Discord server.", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            try
            {
                await command.DeferAsync(ephemeral: true).ConfigureAwait(false);
                deferred = true;
            }
            catch (Discord.Net.HttpException httpException) when (IsDiscordInteractionAlreadyAcknowledged(httpException))
            {
                WriteLog("Stream slash command was already acknowledged before defer; continuing with follow-up responses.");
                deferred = true;
            }

            DiscordBotSettings? settings = LoadUsableSettings(out string statusText);
            if (settings is null)
            {
                await command
                    .FollowupAsync($"VALOWATCH settings are not usable: {statusText}", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            if (settings.GuildId != 0 && guildUser.Guild.Id != settings.GuildId)
            {
                await command
                    .FollowupAsync("This server is not configured for VALOWATCH stream commands.", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            if (!guildUser.GuildPermissions.Administrator && !guildUser.GuildPermissions.ManageGuild)
            {
                await command
                    .FollowupAsync("VALOWATCH stream commands require server management permission.", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            if (!settings.StreamCommandEnabled || !IsStreamCommandEnabled())
            {
                await command
                    .FollowupAsync("VALOWATCH stream command is disabled by configuration.", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            string actionName = command.Data.Options.FirstOrDefault()?.Name ?? string.Empty;
            if (string.Equals(actionName, StreamSubcommandStatusName, StringComparison.OrdinalIgnoreCase))
            {
                await command
                    .FollowupAsync(embed: BuildStreamStatusEmbed(), ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            if (string.Equals(actionName, StreamSubcommandCamerasName, StringComparison.OrdinalIgnoreCase))
            {
                Embed cameraDevicesEmbed = BuildStreamCameraDevicesEmbed(settings);
                await command
                    .FollowupAsync(embed: cameraDevicesEmbed, ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            if (string.Equals(actionName, StreamSubcommandDebugName, StringComparison.OrdinalIgnoreCase))
            {
                using CancellationTokenSource streamDebugTimeout = new(ScreenStreamHealthRequestTimeout + TimeSpan.FromSeconds(2));
                Embed streamDebugEmbed = await BuildStreamDebugEmbedAsync(streamDebugTimeout.Token).ConfigureAwait(false);
                await command
                    .FollowupAsync(embed: streamDebugEmbed, ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            IMessageChannel? targetChannel = ResolveStreamTargetChannel(command);
            if (targetChannel is null)
            {
                await command
                    .FollowupAsync("Stream target channel is unavailable.", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            if (string.Equals(actionName, StreamSubcommandLinkName, StringComparison.OrdinalIgnoreCase))
            {
                ScreenStreamSession? activeSession = GetActiveScreenStreamSession();
                if (activeSession is null)
                {
                    await command
                        .FollowupAsync("No active stream. Use /stream on or /stream preset first.", ephemeral: true)
                        .ConfigureAwait(false);
                    return;
                }

                await targetChannel
                    .SendMessageAsync(embed: BuildStreamLinkEmbed(activeSession))
                    .ConfigureAwait(false);
                await command
                    .FollowupAsync("Current stream link was sent without restarting the stream.", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            if (string.Equals(actionName, StreamSubcommandRestartName, StringComparison.OrdinalIgnoreCase))
            {
                ScreenStreamSession? activeSession = GetActiveScreenStreamSession();
                if (activeSession is null)
                {
                    await command
                        .FollowupAsync("No active stream to restart. Use /stream on or /stream preset first.", ephemeral: true)
                        .ConfigureAwait(false);
                    return;
                }

                ScreenStreamOptions currentStreamOptions = activeSession.Options;
                await targetChannel
                    .SendMessageAsync(embed: BuildStatusNotificationEmbed(
                        "Stream restart requested: " +
                        $"{ScreenCaptureTargetNames.ToOptionValue(currentStreamOptions.Target)} / " +
                        $"{currentStreamOptions.FramesPerSecond}fps / " +
                        $"{ScreenStreamMethodNames.ToOptionValue(currentStreamOptions.Method)} / " +
                        $"camera:{(currentStreamOptions.CameraOverlayEnabled ? "on" : "off")}"))
                    .ConfigureAwait(false);
                StartScreenStreamCommandInBackground(currentStreamOptions, targetChannel);
                await command
                    .FollowupAsync("Stream restart was queued with the current settings.", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            if (string.Equals(actionName, StreamSubcommandPresetName, StringComparison.OrdinalIgnoreCase))
            {
                ScreenStreamOptions presetStreamOptions = ParseStreamPresetOptions(command, settings, out string presetName);
                await targetChannel
                    .SendMessageAsync(embed: BuildStatusNotificationEmbed(
                        $"Stream preset start queued: {presetName} / " +
                        $"{ScreenCaptureTargetNames.ToOptionValue(presetStreamOptions.Target)} / " +
                        $"{presetStreamOptions.FramesPerSecond}fps / " +
                        $"{ScreenStreamMethodNames.ToOptionValue(presetStreamOptions.Method)} / " +
                        $"quality:{presetStreamOptions.JpegQuality} / width:{presetStreamOptions.MaxWidth} / " +
                        $"camera:{(presetStreamOptions.CameraOverlayEnabled ? "on" : "off")}"))
                    .ConfigureAwait(false);
                StartScreenStreamCommandInBackground(presetStreamOptions, targetChannel);
                await command
                    .FollowupAsync("Stream preset start was queued.", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            if (string.Equals(actionName, StreamSubcommandOffName, StringComparison.OrdinalIgnoreCase))
            {
                bool stopped = await StopActiveScreenStreamAsync("Discord /stream off", targetChannel)
                    .ConfigureAwait(false);
                await command
                    .FollowupAsync(stopped ? "配信を停止しました。" : "配信はすでに停止しています。", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            if (!string.Equals(actionName, StreamSubcommandOnName, StringComparison.OrdinalIgnoreCase))
            {
                await command
                    .FollowupAsync("Use /stream on, /stream off, /stream status, /stream cameras, /stream link, /stream restart, /stream preset, or /stream debug.", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            ScreenStreamOptions streamOptions = ParseStreamOptions(command, settings);

            await targetChannel
                .SendMessageAsync(embed: BuildStatusNotificationEmbed(
                    "配信開始準備中: " +
                    $"{ScreenCaptureTargetNames.ToOptionValue(streamOptions.Target)} / " +
                    $"{streamOptions.FramesPerSecond}fps / " +
                    $"{ScreenStreamMethodNames.ToOptionValue(streamOptions.Method)} / " +
                    $"camera:{(streamOptions.CameraOverlayEnabled ? "on" : "off")}"))
                .ConfigureAwait(false);

            StartScreenStreamCommandInBackground(streamOptions, targetChannel);
            await command
                .FollowupAsync(
                    "配信開始処理を受け付けました。完了したらこのチャンネルに配信URLを送信します。",
                    ephemeral: true)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or PlatformNotSupportedException or HttpRequestException or TaskCanceledException or TimeoutException or OperationCanceledException or Discord.Net.HttpException or System.ComponentModel.Win32Exception)
        {
            WriteLog("Stream slash command handling failed.", exception);
            try
            {
                if (deferred)
                {
                    await command
                        .FollowupAsync($"配信操作に失敗しました: {exception.Message}", ephemeral: true)
                        .ConfigureAwait(false);
                }
                else
                {
                    await command
                        .RespondAsync($"配信操作に失敗しました: {exception.Message}", ephemeral: true)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception responseException) when (responseException is InvalidOperationException or Discord.Net.HttpException)
            {
                WriteLog("Stream slash command error response failed.", responseException);
            }
        }
    }

    private bool IsStreamCommandEnabled()
    {
        lock (stateLock)
        {
            return streamCommandEnabled;
        }
    }

    private ScreenStreamOptions ParseStreamOptions(SocketSlashCommand command, DiscordBotSettings settings)
    {
        SocketSlashCommandDataOption? onOption = command.Data.Options.FirstOrDefault(option =>
            string.Equals(option.Name, StreamSubcommandOnName, StringComparison.OrdinalIgnoreCase));
        object? targetValue = onOption?.Options.FirstOrDefault(option =>
            string.Equals(option.Name, StreamTargetOptionName, StringComparison.OrdinalIgnoreCase))?.Value;
        string targetText = targetValue?.ToString() ?? ScreenCaptureTargetNames.FullScreen;
        if (!ScreenCaptureTargetNames.TryParse(targetText, out ScreenCaptureTarget target))
        {
            throw new InvalidOperationException($"Unknown stream target: {targetText}");
        }

        object? methodValue = onOption?.Options.FirstOrDefault(option =>
            string.Equals(option.Name, StreamMethodOptionName, StringComparison.OrdinalIgnoreCase))?.Value;
        string methodText = methodValue?.ToString() ?? ScreenStreamMethodNames.H264Fmp4;
        if (!ScreenStreamMethodNames.TryParse(methodText, out ScreenStreamMethod method))
        {
            throw new InvalidOperationException($"Unknown stream method: {methodText}");
        }

        int framesPerSecond = ReadIntegerSubcommandOption(
            onOption,
            StreamFramesPerSecondOptionName,
            settings.StreamDefaultFramesPerSecond);
        int jpegQuality = ReadIntegerSubcommandOption(
            onOption,
            StreamQualityOptionName,
            (int)Math.Clamp(settings.StreamDefaultJpegQuality, int.MinValue, int.MaxValue));
        int maxWidth = ReadIntegerSubcommandOption(
            onOption,
            StreamWidthOptionName,
            settings.StreamDefaultMaxWidth);
        bool cameraOverlayEnabled = ReadBooleanSubcommandOption(
            onOption,
            StreamCameraOverlayOptionName,
            settings.StreamDefaultCameraOverlayEnabled);

        return ScreenStreamOptions.Create(
            target,
            framesPerSecond,
            jpegQuality,
            maxWidth,
            method,
            cameraOverlayEnabled,
            settings.StreamCameraDeviceName);
    }

    private ScreenStreamOptions ParseStreamPresetOptions(
        SocketSlashCommand command,
        DiscordBotSettings settings,
        out string presetName)
    {
        SocketSlashCommandDataOption? presetOption = command.Data.Options.FirstOrDefault(option =>
            string.Equals(option.Name, StreamSubcommandPresetName, StringComparison.OrdinalIgnoreCase));
        string requestedPresetName = presetOption?.Options.FirstOrDefault(option =>
            string.Equals(option.Name, StreamPresetOptionName, StringComparison.OrdinalIgnoreCase))?.Value?.ToString() ?? "stable";
        return BuildStreamPresetOptions(requestedPresetName, settings, out presetName);
    }

    private static ScreenStreamOptions BuildStreamPresetOptions(
        string requestedPresetName,
        DiscordBotSettings settings,
        out string presetName)
    {
        string normalizedPresetName = requestedPresetName.Trim().ToLowerInvariant();
        bool defaultCameraEnabled = settings.StreamDefaultCameraOverlayEnabled;
        string cameraDeviceName = settings.StreamCameraDeviceName;
        switch (normalizedPresetName)
        {
            case "low-bandwidth":
            case "low":
                presetName = "low-bandwidth";
                return ScreenStreamOptions.Create(
                    ScreenCaptureTarget.FullScreen,
                    framesPerSecond: 30,
                    jpegQuality: 72,
                    maxWidth: 960,
                    method: ScreenStreamMethod.H264Fmp4,
                    cameraOverlayEnabled: false,
                    cameraDeviceName);

            case "smooth":
                presetName = "smooth";
                return ScreenStreamOptions.Create(
                    ScreenCaptureTarget.FullScreen,
                    framesPerSecond: 60,
                    jpegQuality: 88,
                    maxWidth: 1280,
                    method: ScreenStreamMethod.H264Fmp4,
                    cameraOverlayEnabled: defaultCameraEnabled,
                    cameraDeviceName);

            case "source":
            case "quality":
                presetName = "source";
                return ScreenStreamOptions.Create(
                    ScreenCaptureTarget.FullScreen,
                    framesPerSecond: 60,
                    jpegQuality: 90,
                    maxWidth: 1920,
                    method: ScreenStreamMethod.H264Fmp4,
                    cameraOverlayEnabled: defaultCameraEnabled,
                    cameraDeviceName);

            case "valorant":
            case "valo":
                presetName = "valorant";
                return ScreenStreamOptions.Create(
                    ScreenCaptureTarget.Valorant,
                    framesPerSecond: 60,
                    jpegQuality: 85,
                    maxWidth: 1280,
                    method: ScreenStreamMethod.H264Fmp4,
                    cameraOverlayEnabled: defaultCameraEnabled,
                    cameraDeviceName);

            default:
                presetName = "stable";
                return ScreenStreamOptions.Create(
                    ScreenCaptureTarget.FullScreen,
                    framesPerSecond: 60,
                    jpegQuality: 82,
                    maxWidth: 1280,
                    method: ScreenStreamMethod.H264Fmp4,
                    cameraOverlayEnabled: defaultCameraEnabled,
                    cameraDeviceName);
        }
    }

    private ScreenStreamSession? GetActiveScreenStreamSession()
    {
        lock (stateLock)
        {
            return activeScreenStreamSession;
        }
    }

    private async Task<Embed> BuildStreamDebugEmbedAsync(CancellationToken cancellationToken)
    {
        ScreenStreamSession? session = GetActiveScreenStreamSession();
        if (session is null)
        {
            return new EmbedBuilder()
            {
                Title = "VALOWATCH Stream Debug",
                Description = "Stream: inactive",
                Color = new Discord.Color(139, 148, 158),
                Timestamp = DateTimeOffset.Now
            }.Build();
        }

        ScreenStreamHealthStatus healthStatus;
        using (HttpClient httpClient = new()
        {
            Timeout = ScreenStreamHealthRequestTimeout
        })
        {
            healthStatus = await session
                .CheckPublicUrlHealthAsync(httpClient, cancellationToken)
                .ConfigureAwait(false);
        }

        session.UpdatePublicUrlHealth(healthStatus);
        StringBuilder descriptionBuilder = new();
        descriptionBuilder.AppendLine($"URL: {session.PublicUrl}");
        descriptionBuilder.AppendLine($"Health: {(healthStatus.IsHealthy ? "OK" : "NG")}");
        descriptionBuilder.AppendLine($"Detail: {SanitizeStreamHealthDetail(healthStatus.Detail)}");
        descriptionBuilder.AppendLine($"Tunnel: {RuntimeLogMessageCollector.SanitizeLine(session.TunnelProcessStatusText)}");
        descriptionBuilder.AppendLine($"Method: {ScreenStreamMethodNames.ToOptionValue(session.Method)}");
        descriptionBuilder.AppendLine($"Target: {ScreenCaptureTargetNames.ToOptionValue(session.Target)}");
        descriptionBuilder.AppendLine($"FPS: {session.FramesPerSecond}");
        descriptionBuilder.AppendLine($"Quality: {session.JpegQuality}");
        descriptionBuilder.AppendLine($"Width: {session.MaxWidth}");
        descriptionBuilder.AppendLine($"Camera: {session.CameraOverlayStatusText}");
        descriptionBuilder.AppendLine($"Engine: {session.EngineName}");
        if (session.Method == ScreenStreamMethod.H264Fmp4)
        {
            descriptionBuilder.AppendLine($"SmoothLive: {RuntimeLogMessageCollector.SanitizeLine(session.SmoothLiveStatusText)}");
        }

        return new EmbedBuilder()
        {
            Title = "VALOWATCH Stream Debug",
            Description = TrimEmbedDescription(descriptionBuilder.ToString()),
            Color = healthStatus.IsHealthy ? new Discord.Color(63, 185, 80) : new Discord.Color(210, 153, 34),
            Timestamp = DateTimeOffset.Now
        }.Build();
    }

    private static Embed BuildStreamLinkEmbed(ScreenStreamSession session)
    {
        StringBuilder descriptionBuilder = new();
        descriptionBuilder.AppendLine($"URL: {session.PublicUrl}");
        descriptionBuilder.AppendLine($"Target: {ScreenCaptureTargetNames.ToOptionValue(session.Target)}");
        descriptionBuilder.AppendLine($"Method: {ScreenStreamMethodNames.ToOptionValue(session.Method)}");
        descriptionBuilder.AppendLine($"FPS: {session.FramesPerSecond}");
        descriptionBuilder.AppendLine($"Width: {session.MaxWidth}");
        descriptionBuilder.AppendLine($"Camera: {session.CameraOverlayStatusText}");
        descriptionBuilder.AppendLine(BuildStreamPublicUrlStatusText(session));
        return new EmbedBuilder()
        {
            Title = "VALOWATCH Stream Link",
            Description = TrimEmbedDescription(descriptionBuilder.ToString()),
            Color = new Discord.Color(88, 166, 255),
            Timestamp = DateTimeOffset.Now
        }.Build();
    }

    private static int ReadIntegerSubcommandOption(
        SocketSlashCommandDataOption? subcommandOption,
        string optionName,
        int defaultValue)
    {
        object? value = subcommandOption?.Options.FirstOrDefault(option =>
            string.Equals(option.Name, optionName, StringComparison.OrdinalIgnoreCase))?.Value;
        return value switch
        {
            null => defaultValue,
            int intValue => intValue,
            long longValue when longValue > int.MaxValue => int.MaxValue,
            long longValue when longValue < int.MinValue => int.MinValue,
            long longValue => (int)longValue,
            double doubleValue => (int)Math.Round(doubleValue),
            string textValue when int.TryParse(
                textValue,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out int parsedValue) => parsedValue,
            _ => defaultValue
        };
    }

    private static bool ReadBooleanSubcommandOption(
        SocketSlashCommandDataOption? subcommandOption,
        string optionName,
        bool defaultValue)
    {
        object? value = subcommandOption?.Options.FirstOrDefault(option =>
            string.Equals(option.Name, optionName, StringComparison.OrdinalIgnoreCase))?.Value;
        return value switch
        {
            null => defaultValue,
            bool boolValue => boolValue,
            string textValue when bool.TryParse(textValue, out bool parsedValue) => parsedValue,
            _ => defaultValue
        };
    }

    private IMessageChannel? ResolveStreamTargetChannel(SocketSlashCommand command)
    {
        return command.Channel as IMessageChannel ?? discordStatusTextChannel;
    }

    private void StartScreenStreamCommandInBackground(
        ScreenStreamOptions streamOptions,
        IMessageChannel targetChannel)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using CancellationTokenSource timeout = new(ScreenStreamBackgroundStartTimeout);
                ScreenStreamSession session = await StartOrReplaceScreenStreamAsync(
                        streamOptions,
                        targetChannel,
                        timeout.Token)
                    .ConfigureAwait(false);

                await targetChannel
                    .SendMessageAsync(embed: BuildStreamStartedEmbed(session))
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or PlatformNotSupportedException or HttpRequestException or TaskCanceledException or TimeoutException or OperationCanceledException or Discord.Net.HttpException or System.ComponentModel.Win32Exception)
            {
                WriteLog("Stream background startup failed.", exception);
                try
                {
                    await targetChannel
                        .SendMessageAsync(embed: BuildStatusNotificationEmbed($"配信開始に失敗しました: {exception.Message}"))
                        .ConfigureAwait(false);
                }
                catch (Exception responseException) when (responseException is InvalidOperationException or Discord.Net.HttpException)
                {
                    WriteLog("Stream background startup error notification failed.", responseException);
                }
            }
        });
    }

    private async Task<ScreenStreamSession> StartOrReplaceScreenStreamAsync(
        ScreenStreamOptions streamOptions,
        IMessageChannel notifyChannel,
        CancellationToken cancellationToken)
    {
        await StopScreenStreamMonitorAsync().ConfigureAwait(false);
        ScreenStreamSession? previousSession;
        int requestGenerationSnapshot;
        await screenStreamSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            previousSession = activeScreenStreamSession;
            activeScreenStreamSession = null;
            requestedScreenStreamOptions = streamOptions;
            activeScreenStreamNotifyChannel = notifyChannel;
            screenStreamConsecutiveHealthFailures = 0;
            screenStreamRestartInProgress = true;
            screenStreamRequestGeneration++;
            requestGenerationSnapshot = screenStreamRequestGeneration;
        }
        finally
        {
            screenStreamSemaphore.Release();
        }

        if (previousSession is not null)
        {
            await previousSession.DisposeAsync().ConfigureAwait(false);
        }

        ScreenStreamSession? startedSession = null;
        try
        {
            startedSession = await StartResponsiveScreenStreamSessionAsync(
                    streamOptions,
                    cancellationToken)
                .ConfigureAwait(false);

            ScreenStreamSession acceptedSession;
            await screenStreamSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!requestedScreenStreamOptions.HasValue ||
                    screenStreamRequestGeneration != requestGenerationSnapshot ||
                    !requestedScreenStreamOptions.Value.Equals(streamOptions))
                {
                    throw new OperationCanceledException("Screen stream start was cancelled or replaced.");
                }

                activeScreenStreamSession = startedSession;
                activeScreenStreamNotifyChannel = notifyChannel;
                screenStreamConsecutiveHealthFailures = 0;
                screenStreamRestartInProgress = false;
                acceptedSession = startedSession;
                startedSession = null;
            }
            finally
            {
                screenStreamSemaphore.Release();
            }

            WriteLog(
                "Screen stream started. " +
                $"Target: {ScreenCaptureTargetNames.ToOptionValue(acceptedSession.Target)}. " +
                $"FPS: {acceptedSession.FramesPerSecond}. Quality: {acceptedSession.JpegQuality}. Width: {acceptedSession.MaxWidth}. " +
                $"Method: {ScreenStreamMethodNames.ToOptionValue(acceptedSession.Method)}. " +
                $"CameraOverlay: {acceptedSession.CameraOverlayStatusText}. " +
                $"Engine: {acceptedSession.EngineName}. " +
                $"Url: {acceptedSession.PublicUrl}.");
            EnsureScreenStreamMonitorStarted();
            return acceptedSession;
        }
        catch
        {
            if (startedSession is not null)
            {
                await startedSession.DisposeAsync().ConfigureAwait(false);
            }

            await screenStreamSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                if (screenStreamRequestGeneration == requestGenerationSnapshot)
                {
                    requestedScreenStreamOptions = null;
                    activeScreenStreamNotifyChannel = null;
                    screenStreamConsecutiveHealthFailures = 0;
                    screenStreamRestartInProgress = false;
                }
            }
            finally
            {
                screenStreamSemaphore.Release();
            }

            throw;
        }
    }

    private async Task<ScreenStreamSession> StartResponsiveScreenStreamSessionAsync(
        ScreenStreamOptions streamOptions,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        string lastHealthDetail = "not checked";

        for (int attempt = 1; attempt <= ScreenStreamStartValidationAttempts; attempt++)
        {
            ScreenStreamSession? session = null;
            try
            {
                session = await ScreenStreamSession
                    .StartAsync(appPaths, streamOptions, WriteScreenStreamLog, cancellationToken)
                    .ConfigureAwait(false);

                ScreenStreamHealthStatus healthStatus = await WaitForScreenStreamPublicUrlReadyAsync(
                        session,
                        cancellationToken)
                    .ConfigureAwait(false);
                session.UpdatePublicUrlHealth(healthStatus);
                if (healthStatus.IsHealthy)
                {
                    WriteLog(
                        "Screen stream public URL validated. " +
                        $"Attempt: {attempt}/{ScreenStreamStartValidationAttempts}. " +
                        $"Url: {session.PublicUrl}. Detail: {healthStatus.Detail}.");
                    ScreenStreamSession validatedSession = session;
                    session = null;
                    return validatedSession;
                }

                lastHealthDetail = healthStatus.Detail;
                WriteLog(
                    "Screen stream public URL is not reachable yet; keeping the quick tunnel alive for fast startup. " +
                    $"Attempt: {attempt}/{ScreenStreamStartValidationAttempts}. " +
                    $"Url: {session.PublicUrl}. Detail: {healthStatus.Detail}.");
                ScreenStreamSession unvalidatedSession = session;
                session = null;
                return unvalidatedSession;
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or PlatformNotSupportedException or HttpRequestException or TaskCanceledException or TimeoutException or OperationCanceledException or System.ComponentModel.Win32Exception)
            {
                lastException = exception;
                lastHealthDetail = $"{exception.GetType().Name}: {exception.Message}";
                WriteLog(
                    "Screen stream startup validation attempt failed; retrying with a new tunnel. " +
                    $"Attempt: {attempt}/{ScreenStreamStartValidationAttempts}.",
                    exception);
            }
            finally
            {
                if (session is not null)
                {
                    try
                    {
                        await session.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception disposeException) when (disposeException is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
                    {
                        WriteLog("Screen stream startup validation cleanup failed.", disposeException);
                    }
                }
            }

            if (attempt < ScreenStreamStartValidationAttempts)
            {
                await Task.Delay(ScreenStreamStartValidationDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            $"Screen stream could not be started before sending it to Discord. Last detail: {lastHealthDetail}",
            lastException);
    }

    private static async Task<ScreenStreamHealthStatus> WaitForScreenStreamPublicUrlReadyAsync(
        ScreenStreamSession session,
        CancellationToken cancellationToken)
    {
        using HttpClient httpClient = new()
        {
            Timeout = ScreenStreamHealthRequestTimeout
        };

        Stopwatch stopwatch = Stopwatch.StartNew();
        ScreenStreamHealthStatus lastStatus = ScreenStreamHealthStatus.Unhealthy("not checked");
        while (!cancellationToken.IsCancellationRequested)
        {
            lastStatus = await session
                .CheckPublicUrlHealthAsync(httpClient, cancellationToken)
                .ConfigureAwait(false);
            session.UpdatePublicUrlHealth(lastStatus);
            if (lastStatus.IsHealthy)
            {
                return lastStatus;
            }

            if (stopwatch.Elapsed >= ScreenStreamStartValidationTimeout)
            {
                return lastStatus;
            }

            await Task.Delay(ScreenStreamStartValidationDelay, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return lastStatus;
    }

    private async Task<bool> StopActiveScreenStreamAsync(string reason, IMessageChannel? notifyChannel)
    {
        await StopScreenStreamMonitorAsync().ConfigureAwait(false);
        ScreenStreamSession? sessionToStop;
        await screenStreamSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            sessionToStop = activeScreenStreamSession;
            activeScreenStreamSession = null;
            requestedScreenStreamOptions = null;
            activeScreenStreamNotifyChannel = null;
            screenStreamRestartInProgress = false;
            screenStreamConsecutiveHealthFailures = 0;
            screenStreamRequestGeneration++;
        }
        finally
        {
            screenStreamSemaphore.Release();
        }

        if (sessionToStop is null)
        {
            WriteLog($"Screen stream stop skipped because no stream was active. Reason: {reason}.");
            return false;
        }

        string targetText = ScreenCaptureTargetNames.ToOptionValue(sessionToStop.Target);
        string publicUrl = sessionToStop.PublicUrl;
        int framesPerSecond = sessionToStop.FramesPerSecond;
        int maxWidth = sessionToStop.MaxWidth;
        string methodName = ScreenStreamMethodNames.ToOptionValue(sessionToStop.Method);
        string engineName = sessionToStop.EngineName;
        await sessionToStop.DisposeAsync().ConfigureAwait(false);
        WriteLog($"Screen stream stopped. Reason: {reason}. Target: {targetText}. FPS: {framesPerSecond}. Width: {maxWidth}. Method: {methodName}. Engine: {engineName}. Url: {publicUrl}.");
        if (notifyChannel is not null)
        {
            try
            {
                await notifyChannel
                    .SendMessageAsync(embed: BuildStatusNotificationEmbed($"配信停止: {targetText} / {framesPerSecond}fps / {methodName}"))
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Discord.Net.HttpException)
            {
                WriteLog("Screen stream stop notification failed.", exception);
            }
        }

        return true;
    }

    private void EnsureScreenStreamMonitorStarted()
    {
        lock (stateLock)
        {
            if (screenStreamMonitorTask is { IsCompleted: false })
            {
                return;
            }

            screenStreamMonitorCancellationTokenSource?.Dispose();
            CancellationTokenSource cancellationTokenSource = new();
            screenStreamMonitorCancellationTokenSource = cancellationTokenSource;
            screenStreamMonitorTask = Task.Run(
                () => MonitorScreenStreamAsync(cancellationTokenSource.Token),
                CancellationToken.None);
        }
    }

    private async Task StopScreenStreamMonitorAsync()
    {
        CancellationTokenSource? cancellationTokenSource;
        Task? monitorTask;
        lock (stateLock)
        {
            cancellationTokenSource = screenStreamMonitorCancellationTokenSource;
            monitorTask = screenStreamMonitorTask;
            screenStreamMonitorCancellationTokenSource = null;
            screenStreamMonitorTask = null;
        }

        if (cancellationTokenSource is null)
        {
            return;
        }

        await cancellationTokenSource.CancelAsync().ConfigureAwait(false);
        if (monitorTask is not null)
        {
            try
            {
                await monitorTask.WaitAsync(ScreenStreamMonitorShutdownTimeout).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (TimeoutException exception)
            {
                WriteLog("Screen stream monitor did not stop before timeout; cleanup will continue.", exception);
            }
            catch (Exception exception)
            {
                WriteLog("Screen stream monitor stopped with an error.", exception);
            }
        }

        cancellationTokenSource.Dispose();
    }

    private async Task MonitorScreenStreamAsync(CancellationToken cancellationToken)
    {
        using HttpClient httpClient = new()
        {
            Timeout = ScreenStreamHealthRequestTimeout
        };

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(ScreenStreamHealthCheckInterval, cancellationToken).ConfigureAwait(false);
                await RepairScreenStreamIfNeededAsync(httpClient, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            WriteLog("Screen stream monitor stopped unexpectedly.", exception);
        }
    }

    private async Task RepairScreenStreamIfNeededAsync(HttpClient httpClient, CancellationToken cancellationToken)
    {
        ScreenStreamSession? sessionSnapshot;
        ScreenStreamOptions? requestedOptionsSnapshot;
        int requestGenerationSnapshot;
        await screenStreamSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (screenStreamRestartInProgress)
            {
                return;
            }

            requestedOptionsSnapshot = requestedScreenStreamOptions;
            sessionSnapshot = activeScreenStreamSession;
            requestGenerationSnapshot = screenStreamRequestGeneration;
        }
        finally
        {
            screenStreamSemaphore.Release();
        }

        if (!requestedOptionsSnapshot.HasValue)
        {
            return;
        }

        string? restartReason = null;
        if (sessionSnapshot is null)
        {
            restartReason = "screen stream session missing while the stream command is still enabled";
        }
        else if (!sessionSnapshot.IsTunnelProcessRunning)
        {
            restartReason = sessionSnapshot.TunnelProcessStatusText;
        }
        else
        {
            ScreenStreamHealthStatus healthStatus = await sessionSnapshot
                .CheckPublicUrlHealthAsync(httpClient, cancellationToken)
                .ConfigureAwait(false);
            sessionSnapshot.UpdatePublicUrlHealth(healthStatus);
            if (healthStatus.IsHealthy)
            {
                await ResetScreenStreamHealthFailuresAsync(sessionSnapshot, cancellationToken).ConfigureAwait(false);
                return;
            }

            int failureCount = await IncrementScreenStreamHealthFailuresAsync(
                    sessionSnapshot,
                    cancellationToken)
                .ConfigureAwait(false);
            if (failureCount <= 0)
            {
                return;
            }

            if (failureCount == 1 ||
                failureCount == ScreenStreamPublicUrlDiagnosticNotificationThreshold ||
                failureCount % 20 == 0)
            {
                WriteLog(
                    $"Screen stream public URL health check failed. " +
                    $"Failures: {failureCount}. " +
                    $"Url: {sessionSnapshot.PublicUrl}. Detail: {healthStatus.Detail}. " +
                    "The existing quick tunnel URL will be kept while cloudflared is still running.");
            }
            return;
        }

        await RestartScreenStreamAsync(
                sessionSnapshot,
                requestedOptionsSnapshot.Value,
                requestGenerationSnapshot,
                restartReason,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ResetScreenStreamHealthFailuresAsync(
        ScreenStreamSession session,
        CancellationToken cancellationToken)
    {
        await screenStreamSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(activeScreenStreamSession, session))
            {
                screenStreamConsecutiveHealthFailures = 0;
            }
        }
        finally
        {
            screenStreamSemaphore.Release();
        }
    }

    private async Task<int> IncrementScreenStreamHealthFailuresAsync(
        ScreenStreamSession session,
        CancellationToken cancellationToken)
    {
        await screenStreamSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(activeScreenStreamSession, session))
            {
                return 0;
            }

            screenStreamConsecutiveHealthFailures++;
            return screenStreamConsecutiveHealthFailures;
        }
        finally
        {
            screenStreamSemaphore.Release();
        }
    }

    private async Task RestartScreenStreamAsync(
        ScreenStreamSession? expectedSession,
        ScreenStreamOptions requestedOptionsSnapshot,
        int requestGenerationSnapshot,
        string restartReason,
        CancellationToken cancellationToken)
    {
        ScreenStreamSession? staleSession;
        IMessageChannel? notifyChannel;
        await screenStreamSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!requestedScreenStreamOptions.HasValue ||
                screenStreamRequestGeneration != requestGenerationSnapshot ||
                screenStreamRestartInProgress)
            {
                return;
            }

            if (expectedSession is not null && !ReferenceEquals(activeScreenStreamSession, expectedSession))
            {
                return;
            }

            screenStreamRestartInProgress = true;
            screenStreamConsecutiveHealthFailures = 0;
            staleSession = activeScreenStreamSession;
            activeScreenStreamSession = null;
            notifyChannel = activeScreenStreamNotifyChannel ?? discordStatusTextChannel;
        }
        finally
        {
            screenStreamSemaphore.Release();
        }

        string staleUrl = staleSession?.PublicUrl ?? "(none)";
        WriteLog($"Screen stream tunnel is unavailable; restarting. Reason: {restartReason}. OldUrl: {staleUrl}.");

        ScreenStreamSession? replacementSession = null;
        try
        {
            if (staleSession is not null)
            {
                await staleSession.DisposeAsync().ConfigureAwait(false);
            }

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ScreenStreamRestartTimeout);
            replacementSession = await StartResponsiveScreenStreamSessionAsync(
                    requestedOptionsSnapshot,
                    timeout.Token)
                .ConfigureAwait(false);

            bool acceptedReplacement = false;
            ScreenStreamSession? acceptedSession = null;
            await screenStreamSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (requestedScreenStreamOptions.HasValue &&
                    screenStreamRequestGeneration == requestGenerationSnapshot &&
                    requestedScreenStreamOptions.Value.Equals(requestedOptionsSnapshot))
                {
                    activeScreenStreamSession = replacementSession;
                    activeScreenStreamNotifyChannel = notifyChannel;
                    screenStreamConsecutiveHealthFailures = 0;
                    acceptedReplacement = true;
                    acceptedSession = replacementSession;
                    replacementSession = null;
                }

                screenStreamRestartInProgress = false;
            }
            finally
            {
                screenStreamSemaphore.Release();
            }

            if (!acceptedReplacement)
            {
                if (replacementSession is not null)
                {
                    await replacementSession.DisposeAsync().ConfigureAwait(false);
                }

                return;
            }

            if (acceptedSession is null)
            {
                return;
            }

            WriteLog(
                $"Screen stream tunnel restarted. Reason: {restartReason}. " +
                $"NewUrl: {acceptedSession.PublicUrl}.");
            if (notifyChannel is not null)
            {
                try
                {
                    await notifyChannel
                        .SendMessageAsync(embed: BuildStreamRecoveredEmbed(acceptedSession, restartReason))
                        .ConfigureAwait(false);
                }
                catch (Exception responseException) when (responseException is InvalidOperationException or ObjectDisposedException or Discord.Net.HttpException)
                {
                    WriteLog("Screen stream restart notification skipped because the Discord channel was unavailable.", null);
                }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or PlatformNotSupportedException or HttpRequestException or TaskCanceledException or TimeoutException or OperationCanceledException or Discord.Net.HttpException or System.ComponentModel.Win32Exception)
        {
            if (replacementSession is not null)
            {
                try
                {
                    await replacementSession.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception disposeException) when (disposeException is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
                {
                    WriteLog("Screen stream replacement cleanup failed after restart error.", disposeException);
                }
            }

            await MarkScreenStreamRestartFinishedAsync(requestGenerationSnapshot).ConfigureAwait(false);
            WriteLog($"Screen stream tunnel restart failed. Reason: {restartReason}.", exception);
            await SendScreenStreamRestartFailureNotificationAsync(notifyChannel, exception).ConfigureAwait(false);
        }
    }

    private async Task MarkScreenStreamRestartFinishedAsync(int requestGenerationSnapshot)
    {
        await screenStreamSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            if (screenStreamRequestGeneration == requestGenerationSnapshot)
            {
                screenStreamRestartInProgress = false;
            }
        }
        finally
        {
            screenStreamSemaphore.Release();
        }
    }

    private async Task SendScreenStreamHealthDiagnosticNotificationAsync(
        IMessageChannel? notifyChannel,
        ScreenStreamSession session,
        ScreenStreamHealthStatus healthStatus,
        int failureCount)
    {
        if (notifyChannel is null)
        {
            return;
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (nowUtc - lastScreenStreamHealthDiagnosticNotificationAtUtc < ScreenStreamRestartFailureNotificationCooldown)
        {
            return;
        }

        lastScreenStreamHealthDiagnosticNotificationAtUtc = nowUtc;
        string message =
            "配信URLの接続確認に失敗しました。" +
            Environment.NewLine +
            $"URL: {session.PublicUrl}" +
            Environment.NewLine +
            $"失敗回数: {failureCount} (通知しきい値: {ScreenStreamPublicUrlDiagnosticNotificationThreshold})" +
            Environment.NewLine +
            $"原因: {SanitizeStreamHealthDetail(healthStatus.Detail)}" +
            Environment.NewLine +
            "対応: DNS反映待ち、または一時的なCloudflare Quick Tunnel不調として監視します。cloudflaredが動いている間は同じURLを保持します。";

        try
        {
            await notifyChannel
                .SendMessageAsync(embed: BuildStatusNotificationEmbed(message))
                .ConfigureAwait(false);
        }
        catch (Exception responseException) when (responseException is InvalidOperationException or ObjectDisposedException or TaskCanceledException or Discord.Net.HttpException)
        {
            WriteLog(
                $"Screen stream health diagnostic notification skipped because Discord was unavailable: {FormatExceptionSummary(responseException)}");
        }
    }

    private async Task SendScreenStreamRestartFailureNotificationAsync(
        IMessageChannel? notifyChannel,
        Exception exception)
    {
        if (notifyChannel is null)
        {
            return;
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (nowUtc - lastScreenStreamRestartFailureNotificationAtUtc < ScreenStreamRestartFailureNotificationCooldown)
        {
            return;
        }

        lastScreenStreamRestartFailureNotificationAtUtc = nowUtc;
        try
        {
            await notifyChannel
                .SendMessageAsync(embed: BuildStatusNotificationEmbed($"配信URLの自動復旧に失敗しました: {exception.Message}"))
                .ConfigureAwait(false);
        }
        catch (Exception responseException) when (responseException is InvalidOperationException or ObjectDisposedException or TaskCanceledException or Discord.Net.HttpException)
        {
            WriteLog(
                $"Screen stream restart failure notification skipped because Discord was unavailable: {FormatExceptionSummary(responseException)}");
        }
    }

    private Embed BuildStreamStartedEmbed(ScreenStreamSession session)
    {
        EmbedBuilder embedBuilder = new()
        {
            Title = "VALOWATCH 配信開始",
            Description =
                $"配信URL: {session.PublicUrl}{Environment.NewLine}" +
                $"対象: {ScreenCaptureTargetNames.ToOptionValue(session.Target)}{Environment.NewLine}" +
                $"FPS: {session.FramesPerSecond}{Environment.NewLine}" +
                $"品質: {session.JpegQuality}{Environment.NewLine}" +
                $"最大横幅: {session.MaxWidth}px{Environment.NewLine}" +
                $"方式: {ScreenStreamMethodNames.ToOptionValue(session.Method)}{Environment.NewLine}" +
                $"カメラ: {session.CameraOverlayStatusText}{Environment.NewLine}" +
                $"エンジン: {session.EngineName}{Environment.NewLine}" +
                $"{BuildStreamSmoothLiveStatusText(session)}" +
                $"{BuildStreamPublicUrlStatusText(session)}{Environment.NewLine}" +
                "停止: /stream off",
            Color = new Discord.Color(88, 166, 255),
            Timestamp = DateTimeOffset.Now
        };
        return embedBuilder.Build();
    }

    private Embed BuildStreamRecoveredEmbed(ScreenStreamSession session, string restartReason)
    {
        EmbedBuilder embedBuilder = new()
        {
            Title = "VALOWATCH 配信URL更新",
            Description =
                $"新しい配信URL: {session.PublicUrl}{Environment.NewLine}" +
                $"理由: {restartReason}{Environment.NewLine}" +
                $"対象: {ScreenCaptureTargetNames.ToOptionValue(session.Target)}{Environment.NewLine}" +
                $"FPS: {session.FramesPerSecond}{Environment.NewLine}" +
                $"方式: {ScreenStreamMethodNames.ToOptionValue(session.Method)}{Environment.NewLine}" +
                $"カメラ: {session.CameraOverlayStatusText}{Environment.NewLine}" +
                $"{BuildStreamSmoothLiveStatusText(session)}" +
                BuildStreamPublicUrlStatusText(session),
            Color = new Discord.Color(88, 166, 255),
            Timestamp = DateTimeOffset.Now
        };
        return embedBuilder.Build();
    }

    private Embed BuildStreamStatusEmbed()
    {
        ScreenStreamSession? session;
        lock (stateLock)
        {
            session = activeScreenStreamSession;
        }

        EmbedBuilder embedBuilder = new()
        {
            Title = "VALOWATCH 配信状態",
            Color = session is null ? new Discord.Color(139, 148, 158) : new Discord.Color(88, 166, 255),
            Timestamp = DateTimeOffset.Now
        };
        if (session is null)
        {
            embedBuilder.Description = "配信は停止中です。";
        }
        else
        {
            embedBuilder.Description =
                $"配信中: {ScreenCaptureTargetNames.ToOptionValue(session.Target)}{Environment.NewLine}" +
                $"FPS: {session.FramesPerSecond}{Environment.NewLine}" +
                $"品質: {session.JpegQuality}{Environment.NewLine}" +
                $"最大横幅: {session.MaxWidth}px{Environment.NewLine}" +
                $"方式: {ScreenStreamMethodNames.ToOptionValue(session.Method)}{Environment.NewLine}" +
                $"カメラ: {session.CameraOverlayStatusText}{Environment.NewLine}" +
                $"エンジン: {session.EngineName}{Environment.NewLine}" +
                $"URL: {session.PublicUrl}{Environment.NewLine}" +
                $"{BuildStreamSmoothLiveStatusText(session)}" +
                $"{BuildStreamPublicUrlStatusText(session)}{Environment.NewLine}" +
                $"トンネル: {session.TunnelProcessStatusText}{Environment.NewLine}" +
                $"開始: {session.StartedAtUtc.LocalDateTime:yyyy-MM-dd HH:mm:ss}";
        }

        return embedBuilder.Build();
    }

    private Embed BuildStreamCameraDevicesEmbed(DiscordBotSettings settings)
    {
        CameraDeviceSnapshot snapshot = ScreenStreamingServer.CaptureCameraDeviceSnapshot(appPaths.FfmpegPath, WriteLog);
        StringBuilder descriptionBuilder = new();
        descriptionBuilder.AppendLine($"既定カメラ表示: {(settings.StreamDefaultCameraOverlayEnabled ? "on" : "off")}");
        descriptionBuilder.AppendLine(
            $"設定カメラ名: {(string.IsNullOrWhiteSpace(settings.StreamCameraDeviceName) ? "(未指定)" : SanitizeCameraDeviceText(settings.StreamCameraDeviceName))}");
        descriptionBuilder.AppendLine($"ffmpeg: {(snapshot.FfmpegAvailable ? "available" : "missing")}");
        descriptionBuilder.AppendLine();

        if (!snapshot.FfmpegAvailable)
        {
            descriptionBuilder.AppendLine(SanitizeCameraDeviceText(snapshot.Detail));
        }
        else if (snapshot.Devices.Count == 0)
        {
            descriptionBuilder.AppendLine("DirectShowでWebカメラは検出されませんでした。");
        }
        else
        {
            for (int deviceIndex = 0; deviceIndex < snapshot.Devices.Count; deviceIndex++)
            {
                CameraDeviceDiagnostic device = snapshot.Devices[deviceIndex];
                string readiness = device.CanReadFrame ? "OK" : "NG";
                descriptionBuilder.AppendLine(
                    $"{deviceIndex + 1}. [{readiness}] {SanitizeCameraDeviceText(device.Name)} - {SanitizeCameraDeviceText(device.Detail)}");
            }
        }

        descriptionBuilder.AppendLine();
        descriptionBuilder.AppendLine("OK: 配信用テストフレーム取得成功");
        descriptionBuilder.AppendLine("NG: 検出はされたが、使用中・権限・ドライバ等で読み取り失敗");
        if (!string.IsNullOrWhiteSpace(snapshot.Detail))
        {
            descriptionBuilder.AppendLine($"Detail: {SanitizeCameraDeviceText(snapshot.Detail)}");
        }

        EmbedBuilder embedBuilder = new()
        {
            Title = "VALOWATCH Webカメラ一覧",
            Description = TrimEmbedDescription(descriptionBuilder.ToString()),
            Color = snapshot.Devices.Count == 0 ? new Discord.Color(210, 153, 34) : new Discord.Color(63, 185, 80),
            Timestamp = DateTimeOffset.Now
        };
        embedBuilder.AddField(
            "使い方",
            "/stream on target:full method:h264-fmp4 fps:60 quality:90 width:1280 camera:true",
            inline: false);
        return embedBuilder.Build();
    }

    private Embed BuildDebugStatusEmbed(DiscordBotSettings settings)
    {
        bool isOnline;
        bool isRunning;
        bool screenshotEnabled;
        bool streamEnabled;
        bool discordAudioRuntimeEnabledSnapshot;
        bool discordAudioCommandEnabledSnapshot;
        bool valorantAudioRuntimeEnabledSnapshot;
        bool valorantAudioCommandEnabledSnapshot;
        bool systemAudioRuntimeEnabledSnapshot;
        bool systemAudioCommandEnabledSnapshot;
        bool voiceJoinModeCommandEnabledSnapshot;
        bool streamRestartInProgressSnapshot;
        ulong monitoredDiscordUserId;
        string statusText;
        string voiceGuildName;
        string voiceChannelName;
        string discordConversationGuildName;
        string discordConversationChannelName;
        ScreenStreamSession? session;
        lock (stateLock)
        {
            isOnline = IsOnline;
            isRunning = IsRunning;
            screenshotEnabled = screenshotCommandEnabled;
            streamEnabled = streamCommandEnabled;
            discordAudioRuntimeEnabledSnapshot = discordProcessAudioRuntimeEnabled;
            discordAudioCommandEnabledSnapshot = discordAudioCommandEnabled;
            valorantAudioRuntimeEnabledSnapshot = valorantProcessAudioRuntimeEnabled;
            valorantAudioCommandEnabledSnapshot = valorantAudioCommandEnabled;
            systemAudioRuntimeEnabledSnapshot = systemAudioRuntimeEnabled;
            systemAudioCommandEnabledSnapshot = systemAudioCommandEnabled;
            voiceJoinModeCommandEnabledSnapshot = voiceJoinModeCommandEnabled;
            streamRestartInProgressSnapshot = screenStreamRestartInProgress;
            monitoredDiscordUserId = currentMonitoredDiscordUserId;
            statusText = StatusText;
            voiceGuildName = currentVoiceGuildName;
            voiceChannelName = currentVoiceChannelName;
            discordConversationGuildName = currentDiscordConversationGuildName;
            discordConversationChannelName = currentDiscordConversationChannelName;
            session = activeScreenStreamSession;
        }

        string processText = BuildCurrentProcessStatusText();
        string voiceText = BuildDebugVoiceText(
            isRunning,
            isOnline,
            voiceGuildName,
            voiceChannelName,
            discordConversationGuildName,
            discordConversationChannelName,
            monitoredDiscordUserId);
        string streamText = BuildDebugStreamText(session, streamRestartInProgressSnapshot);
        DiscordVoiceJoinMode voiceJoinMode = voiceJoinModeStateStore.Load(settings.GetVoiceJoinMode());
        string commandText =
            $"screenshot: {(screenshotEnabled ? "on" : "off")}{Environment.NewLine}" +
            $"stream: {(streamEnabled ? "on" : "off")}{Environment.NewLine}" +
            $"discord-audio-command: {(discordAudioCommandEnabledSnapshot ? "on" : "off")}{Environment.NewLine}" +
            $"discord-audio-runtime: {(discordAudioRuntimeEnabledSnapshot ? "on" : "off")}{Environment.NewLine}" +
            $"valorant-audio-command: {(valorantAudioCommandEnabledSnapshot ? "on" : "off")}{Environment.NewLine}" +
            $"valorant-audio-runtime: {(valorantAudioRuntimeEnabledSnapshot ? "on" : "off")}{Environment.NewLine}" +
            $"pc-audio-command: {(systemAudioCommandEnabledSnapshot ? "on" : "off")}{Environment.NewLine}" +
            $"pc-audio-runtime: {(systemAudioRuntimeEnabledSnapshot ? "on" : "off")}{Environment.NewLine}" +
            $"voice-mode-command: {(voiceJoinModeCommandEnabledSnapshot ? "on" : "off")}{Environment.NewLine}" +
            $"voice-mode: {DiscordVoiceJoinModeNames.ToValue(voiceJoinMode)}";

        EmbedBuilder embedBuilder = new()
        {
            Title = "VALOWATCH Debug Status",
            Description =
                $"Version: {GetCurrentVersionLabel()}{Environment.NewLine}" +
                $"Status: {RuntimeLogMessageCollector.SanitizeLine(statusText)}{Environment.NewLine}" +
                processText,
            Color = isOnline || isRunning ? new Discord.Color(63, 185, 80) : new Discord.Color(210, 153, 34),
            Timestamp = DateTimeOffset.Now
        };
        embedBuilder.AddField("Voice", voiceText, inline: false);
        embedBuilder.AddField("Stream", streamText, inline: false);
        embedBuilder.AddField("Commands", commandText, inline: true);
        embedBuilder.AddField("Paths", BuildDebugPathsText(), inline: false);
        embedBuilder.WithFooter("No token, env value, process path, or command line is shown.");
        return embedBuilder.Build();
    }

    private Embed BuildDebugAudioEmbed(DiscordBotSettings settings)
    {
        long capturedCallbacksSnapshot;
        long capturedBytesSnapshot;
        long capturedAudibleCallbacksSnapshot;
        long writtenFramesSnapshot;
        long writtenAudibleFramesSnapshot;
        long writtenSilenceFramesSnapshot;
        long writtenShortFramesSnapshot;
        float capturedPeakSnapshot;
        float writtenPeakSnapshot;
        DateTimeOffset lastMicrophoneCallbackAtSnapshot;
        DateTimeOffset lastDiscordFrameWrittenAtSnapshot;
        string microphoneNameSnapshot;
        string captureDeviceListSnapshot;
        string lineSourceSnapshot;
        string discordSourceSnapshot;
        string valorantSourceSnapshot;
        string systemSourceSnapshot;
        bool discordAudioRuntimeEnabledSnapshot;
        bool valorantAudioRuntimeEnabledSnapshot;
        bool systemAudioRuntimeEnabledSnapshot;
        lock (audioStatsLock)
        {
            capturedCallbacksSnapshot = capturedCallbackCount;
            capturedBytesSnapshot = capturedByteCount;
            capturedAudibleCallbacksSnapshot = capturedAudibleCallbackCount;
            writtenFramesSnapshot = writtenFrameCount;
            writtenAudibleFramesSnapshot = writtenAudibleFrameCount;
            writtenSilenceFramesSnapshot = writtenSilenceFrameCount;
            writtenShortFramesSnapshot = writtenShortFrameCount;
            capturedPeakSnapshot = capturedPeak;
            writtenPeakSnapshot = writtenPeak;
            lastMicrophoneCallbackAtSnapshot = lastMicrophoneCallbackAt;
            lastDiscordFrameWrittenAtSnapshot = lastDiscordFrameWrittenAt;
        }

        LineProcessLoopbackWaveProvider? lineProviderSnapshot;
        LineProcessLoopbackWaveProvider? discordProviderSnapshot;
        LineProcessLoopbackWaveProvider? valorantProviderSnapshot;
        SystemLoopbackWaveProvider? systemProviderSnapshot;
        lock (stateLock)
        {
            microphoneNameSnapshot = currentMicrophoneDeviceName;
            captureDeviceListSnapshot = currentCaptureDeviceList;
            lineSourceSnapshot = currentLineLoopbackSourceName;
            discordSourceSnapshot = currentDiscordLoopbackSourceName;
            valorantSourceSnapshot = currentValorantLoopbackSourceName;
            systemSourceSnapshot = currentSystemLoopbackSourceName;
            discordAudioRuntimeEnabledSnapshot = discordProcessAudioRuntimeEnabled;
            valorantAudioRuntimeEnabledSnapshot = valorantProcessAudioRuntimeEnabled;
            systemAudioRuntimeEnabledSnapshot = systemAudioRuntimeEnabled;
            lineProviderSnapshot = lineProcessLoopbackProvider;
            discordProviderSnapshot = discordProcessLoopbackProvider;
            valorantProviderSnapshot = valorantProcessLoopbackProvider;
            systemProviderSnapshot = systemAudioLoopbackProvider;
        }

        string selectedMicrophone = string.IsNullOrWhiteSpace(microphoneNameSnapshot)
            ? (string.IsNullOrWhiteSpace(settings.MicrophoneDeviceName) ? "(auto)" : settings.MicrophoneDeviceName)
            : microphoneNameSnapshot;
        string lineStats = lineProviderSnapshot?.GetStatusSummary() ?? "LINELoopbackCapturing: False.";
        string discordStats = discordProviderSnapshot?.GetStatusSummary() ?? "DiscordLoopbackCapturing: False.";
        string valorantStats = valorantProviderSnapshot?.GetStatusSummary() ?? "VALORANTLoopbackCapturing: False.";
        string systemStats = systemProviderSnapshot?.GetStatusSummary() ?? "SystemLoopbackCapturing: False.";

        StringBuilder descriptionBuilder = new();
        descriptionBuilder.AppendLine($"Mic: {SanitizeCameraDeviceText(selectedMicrophone)}");
        descriptionBuilder.AppendLine($"Mic candidates: {TrimOneLine(captureDeviceListSnapshot, 900)}");
        descriptionBuilder.AppendLine($"Mic enabled: {settings.StreamMicrophoneAudio}");
        descriptionBuilder.AppendLine($"Mic volume: {settings.MicrophoneVolume.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}");
        descriptionBuilder.AppendLine($"Mic noise gate: {settings.MicrophoneNoiseGate.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)}");
        descriptionBuilder.AppendLine($"Captured callbacks: {capturedCallbacksSnapshot}");
        descriptionBuilder.AppendLine($"Captured audible callbacks: {capturedAudibleCallbacksSnapshot}");
        descriptionBuilder.AppendLine($"Captured bytes: {capturedBytesSnapshot}");
        descriptionBuilder.AppendLine($"Captured peak: {capturedPeakSnapshot.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)}");
        descriptionBuilder.AppendLine($"Written frames: {writtenFramesSnapshot}");
        descriptionBuilder.AppendLine($"Written audible frames: {writtenAudibleFramesSnapshot}");
        descriptionBuilder.AppendLine($"Written silence frames: {writtenSilenceFramesSnapshot}");
        descriptionBuilder.AppendLine($"Written short frames: {writtenShortFramesSnapshot}");
        descriptionBuilder.AppendLine($"Written peak: {writtenPeakSnapshot.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)}");
        descriptionBuilder.AppendLine($"Last mic callback: {FormatDebugLocalTime(lastMicrophoneCallbackAtSnapshot)}");
        descriptionBuilder.AppendLine($"Last Discord frame: {FormatDebugLocalTime(lastDiscordFrameWrittenAtSnapshot)}");
        descriptionBuilder.AppendLine($"LINE source: {TrimOneLine(lineSourceSnapshot, 500)}");
        descriptionBuilder.AppendLine($"LINE volume: {settings.LineAudioVolume.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}");
        descriptionBuilder.AppendLine($"LINE stats: {TrimOneLine(lineStats, 900)}");
        descriptionBuilder.AppendLine($"Discord mix runtime: {discordAudioRuntimeEnabledSnapshot}");
        descriptionBuilder.AppendLine($"Discord mix source: {TrimOneLine(discordSourceSnapshot, 500)}");
        descriptionBuilder.AppendLine($"Discord mix volume: {currentDiscordAudioVolume.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}");
        descriptionBuilder.AppendLine($"Discord mix stats: {TrimOneLine(discordStats, 900)}");
        descriptionBuilder.AppendLine($"VALORANT mix runtime: {valorantAudioRuntimeEnabledSnapshot}");
        descriptionBuilder.AppendLine($"VALORANT mix source: {TrimOneLine(valorantSourceSnapshot, 500)}");
        descriptionBuilder.AppendLine($"VALORANT mix volume: {currentValorantAudioVolume.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}");
        descriptionBuilder.AppendLine($"VALORANT mix stats: {TrimOneLine(valorantStats, 900)}");
        descriptionBuilder.AppendLine($"PC audio mix runtime: {systemAudioRuntimeEnabledSnapshot}");
        descriptionBuilder.AppendLine($"PC audio mix source: {TrimOneLine(systemSourceSnapshot, 500)}");
        descriptionBuilder.AppendLine($"PC audio mix volume: {currentSystemAudioVolume.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}");
        descriptionBuilder.AppendLine($"PC audio mix stats: {TrimOneLine(systemStats, 900)}");

        return new EmbedBuilder()
        {
            Title = "VALOWATCH Debug Audio",
            Description = TrimEmbedDescription(descriptionBuilder.ToString()),
            Color = capturedPeakSnapshot > 0 || writtenPeakSnapshot > 0
                ? new Discord.Color(63, 185, 80)
                : new Discord.Color(210, 153, 34),
            Timestamp = DateTimeOffset.Now
        }.Build();
    }

    private async Task<Embed> BuildDebugUpdateEmbedAsync(
        bool validateDownload,
        CancellationToken cancellationToken)
    {
        GitUpdateSettingsStore gitUpdateSettingsStore = new(appPaths);
        GitUpdateCheckResult updateResult = await new GitUpdateChecker(gitUpdateSettingsStore)
            .CheckLatestReleaseAsync(cancellationToken)
            .ConfigureAwait(false);

        StringBuilder descriptionBuilder = new();
        descriptionBuilder.AppendLine($"Status: {updateResult.Status}");
        descriptionBuilder.AppendLine($"Current: {RuntimeLogMessageCollector.SanitizeLine(updateResult.CurrentVersion)}");
        descriptionBuilder.AppendLine($"Latest: {RuntimeLogMessageCollector.SanitizeLine(updateResult.LatestVersion)}");
        descriptionBuilder.AppendLine($"Has update: {updateResult.HasUpdate}");
        descriptionBuilder.AppendLine($"Message: {RuntimeLogMessageCollector.SanitizeLine(updateResult.Message)}");
        descriptionBuilder.AppendLine($"Release URL: {updateResult.ReleaseUri?.ToString() ?? "(none)"}");
        descriptionBuilder.AppendLine($"Installer asset: {(updateResult.DownloadUri is null ? "(none)" : "available")}");
        descriptionBuilder.AppendLine($"SHA-256 digest: {(string.IsNullOrWhiteSpace(updateResult.ExpectedSha256) ? "not provided" : "provided")}");

        if (validateDownload)
        {
            GitAutoUpdateResult downloadResult = await new GitAutoUpdater(gitUpdateSettingsStore, appPaths)
                .DownloadAndValidateInstallerAsync(updateResult, cancellationToken)
                .ConfigureAwait(false);
            descriptionBuilder.AppendLine();
            descriptionBuilder.AppendLine("Download validation:");
            descriptionBuilder.AppendLine($"Status: {downloadResult.Status}");
            descriptionBuilder.AppendLine($"Installer ready: {downloadResult.InstallerReady}");
            descriptionBuilder.AppendLine($"Message: {RuntimeLogMessageCollector.SanitizeLine(downloadResult.Message)}");
            descriptionBuilder.AppendLine($"Path: {RuntimeLogMessageCollector.SanitizeLine(downloadResult.DownloadPath ?? "(none)")}");
        }
        else
        {
            descriptionBuilder.AppendLine();
            descriptionBuilder.AppendLine("Download validation: skipped");
        }

        Discord.Color color = updateResult.Status switch
        {
            GitUpdateCheckStatus.UpToDate => new Discord.Color(63, 185, 80),
            GitUpdateCheckStatus.UpdateAvailable => new Discord.Color(88, 166, 255),
            _ => new Discord.Color(210, 153, 34)
        };
        return new EmbedBuilder()
        {
            Title = "VALOWATCH Debug Update",
            Description = TrimEmbedDescription(descriptionBuilder.ToString()),
            Color = color,
            Timestamp = DateTimeOffset.Now
        }.Build();
    }

    private static Embed BuildDebugHelpEmbed()
    {
        string description =
            "/valowatch-debug status - show bot, voice, stream, command, and folder status" + Environment.NewLine +
            "/valowatch-debug audio - show microphone, LINE, Discord, VALORANT mix, and PCM counters" + Environment.NewLine +
            "/valowatch-debug logs - push new runtime log embeds to the configured log channel" + Environment.NewLine +
            "/valowatch-debug diagnostics download:false - run self diagnostics" + Environment.NewLine +
            "/valowatch-debug update download:false - check GitHub update status without launching installer" + Environment.NewLine +
            "/valowatch-valorant-audio enabled:true - mix VALORANT audio into the bot VC" + Environment.NewLine +
            "/valowatch-valorant-audio enabled:false - stop VALORANT audio mixing" + Environment.NewLine +
            "/valowatch-pc-audio enabled:true - mix all current PC output audio into the bot VC" + Environment.NewLine +
            "/valowatch-pc-audio enabled:false - stop all-PC output audio mixing" + Environment.NewLine +
            "/valowatch-voice-mode mode:activity - join VC only during VALORANT/LINE activity" + Environment.NewLine +
            "/valowatch-voice-mode mode:always - keep VC joined while the PC app is running" + Environment.NewLine +
            "/stream status - show current stream state" + Environment.NewLine +
            "/stream debug - check public URL, tunnel, and Smooth Live state" + Environment.NewLine +
            "/stream link - send the current URL again without rebuilding" + Environment.NewLine +
            "/stream restart - rebuild the current stream with the same settings" + Environment.NewLine +
            "/stream preset preset:stable - start a preset stream" + Environment.NewLine +
            "/stream cameras - list webcam devices visible to ffmpeg";

        return new EmbedBuilder()
        {
            Title = "VALOWATCH Debug Help",
            Description = description,
            Color = new Discord.Color(88, 166, 255),
            Timestamp = DateTimeOffset.Now
        }.Build();
    }

    private string BuildDebugPathsText()
    {
        return
            $"base: {RuntimeLogMessageCollector.SanitizeLine(AppContext.BaseDirectory)}{Environment.NewLine}" +
            $"data: {RuntimeLogMessageCollector.SanitizeLine(appPaths.DataDirectory)}{Environment.NewLine}" +
            $"logs: {RuntimeLogMessageCollector.SanitizeLine(Path.Combine(appPaths.DataDirectory, "logs"))}{Environment.NewLine}" +
            $"tools: {RuntimeLogMessageCollector.SanitizeLine(appPaths.ToolDirectory)}{Environment.NewLine}" +
            $"config: {(File.Exists(appPaths.DurableEnvPath) || File.Exists(appPaths.DiscordBotConfigPath) ? "present" : "missing")}{Environment.NewLine}" +
            $"ffmpeg: {(File.Exists(appPaths.FfmpegPath) ? "present" : "missing")}{Environment.NewLine}" +
            $"cloudflared: {(File.Exists(appPaths.CloudflaredPath) ? "present" : "missing")}";
    }

    private static string BuildCurrentProcessStatusText()
    {
        try
        {
            using Process currentProcess = Process.GetCurrentProcess();
            string memoryMegabytes = (currentProcess.PrivateMemorySize64 / 1024D / 1024D)
                .ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            return
                $"PID: {currentProcess.Id}{Environment.NewLine}" +
                $"Memory: {memoryMegabytes} MB";
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return $"Process: unavailable ({exception.Message})";
        }
    }

    private static string BuildDebugVoiceText(
        bool isRunning,
        bool isOnline,
        string voiceGuildName,
        string voiceChannelName,
        string discordConversationGuildName,
        string discordConversationChannelName,
        ulong monitoredDiscordUserId)
    {
        StringBuilder voiceBuilder = new();
        voiceBuilder.AppendLine($"Bot online: {isOnline}");
        voiceBuilder.AppendLine($"Bot in VC: {isRunning}");
        if (!string.IsNullOrWhiteSpace(voiceGuildName) || !string.IsNullOrWhiteSpace(voiceChannelName))
        {
            voiceBuilder.AppendLine($"Bot guild: {NormalizeDiscordDisplayName(voiceGuildName, "(unknown)")}");
            voiceBuilder.AppendLine($"Bot VC: {NormalizeDiscordDisplayName(voiceChannelName, "(unknown)")}");
        }

        if (!string.IsNullOrWhiteSpace(discordConversationGuildName) ||
            !string.IsNullOrWhiteSpace(discordConversationChannelName))
        {
            voiceBuilder.AppendLine($"Observed Discord guild: {NormalizeDiscordDisplayName(discordConversationGuildName, "(unknown)")}");
            voiceBuilder.AppendLine($"Observed Discord VC: {NormalizeDiscordDisplayName(discordConversationChannelName, "(unknown)")}");
        }
        else
        {
            voiceBuilder.AppendLine(monitoredDiscordUserId == 0
                ? "Observed Discord VC: disabled; monitored user id is not configured"
                : "Observed Discord VC: not detected");
        }

        return TrimEmbedDescription(voiceBuilder.ToString());
    }

    private static string BuildDebugStreamText(
        ScreenStreamSession? session,
        bool restartInProgress)
    {
        if (session is null)
        {
            return $"active: false{Environment.NewLine}restart in progress: {restartInProgress}";
        }

        StringBuilder streamBuilder = new();
        streamBuilder.AppendLine("active: true");
        streamBuilder.AppendLine($"restart in progress: {restartInProgress}");
        streamBuilder.AppendLine($"URL: {session.PublicUrl}");
        streamBuilder.AppendLine($"target: {ScreenCaptureTargetNames.ToOptionValue(session.Target)}");
        streamBuilder.AppendLine($"method: {ScreenStreamMethodNames.ToOptionValue(session.Method)}");
        streamBuilder.AppendLine($"fps: {session.FramesPerSecond}");
        streamBuilder.AppendLine($"width: {session.MaxWidth}");
        streamBuilder.AppendLine($"quality: {session.JpegQuality}");
        streamBuilder.AppendLine($"camera: {session.CameraOverlayStatusText}");
        streamBuilder.AppendLine($"tunnel: {RuntimeLogMessageCollector.SanitizeLine(session.TunnelProcessStatusText)}");
        streamBuilder.AppendLine($"healthy: {session.PublicUrlHasBeenHealthy}");
        streamBuilder.AppendLine($"health detail: {SanitizeStreamHealthDetail(session.PublicUrlHealthDetail)}");
        return TrimEmbedDescription(streamBuilder.ToString());
    }

    private static string SanitizeCameraDeviceText(string text)
    {
        string sanitizedText = RuntimeLogMessageCollector
            .SanitizeLine(text)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        if (string.IsNullOrWhiteSpace(sanitizedText))
        {
            return "(empty)";
        }

        return sanitizedText.Length <= 160 ? sanitizedText : sanitizedText[..160] + "...";
    }

    private static string TrimOneLine(string text, int maximumLength)
    {
        string sanitizedText = RuntimeLogMessageCollector
            .SanitizeLine(string.IsNullOrWhiteSpace(text) ? "(empty)" : text)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        if (string.IsNullOrWhiteSpace(sanitizedText))
        {
            sanitizedText = "(empty)";
        }

        int safeMaximumLength = Math.Clamp(maximumLength, 16, DiscordEmbedDescriptionLimit);
        return sanitizedText.Length <= safeMaximumLength
            ? sanitizedText
            : sanitizedText[..safeMaximumLength] + "...";
    }

    private static string FormatDebugLocalTime(DateTimeOffset value)
    {
        if (value == DateTimeOffset.MinValue)
        {
            return "(never)";
        }

        return value.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string BuildStreamSmoothLiveStatusText(ScreenStreamSession session)
    {
        if (session.Method != ScreenStreamMethod.H264Fmp4)
        {
            return string.Empty;
        }

        return
            $"遅延目標: {ScreenStreamingServer.H264Fmp4TargetLatencySeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}秒固定 / Smooth Live{Environment.NewLine}" +
            $"{session.SmoothLiveStatusText}{Environment.NewLine}";
    }

    private static string BuildStreamPublicUrlStatusText(ScreenStreamSession session)
    {
        if (!session.PublicUrlHasBeenHealthy)
        {
            return "接続確認: 確認中。URLは保持します。開けない場合は数秒後に再読み込みしてください。";
        }

        if (session.PublicUrlHasBeenHealthy)
        {
            string healthyAtText = session.PublicUrlLastHealthyAtUtc.HasValue
                ? $" ({session.PublicUrlLastHealthyAtUtc.Value.LocalDateTime:HH:mm:ss})"
                : string.Empty;
            return $"接続確認: OK{healthyAtText}";
        }

        return
            "接続確認: 確認中。開けない場合は数秒後に再読み込みしてください。cloudflaredが動いている間は同じURLを保持します。" +
            Environment.NewLine +
            $"直近原因: {SanitizeStreamHealthDetail(session.PublicUrlHealthDetail)}";
    }

    private static string SanitizeStreamHealthDetail(string detail)
    {
        string sanitizedDetail = RuntimeLogMessageCollector.SanitizeLine(
            string.IsNullOrWhiteSpace(detail) ? "not checked" : detail);
        const int maximumHealthDetailLength = 700;
        return sanitizedDetail.Length <= maximumHealthDetailLength
            ? sanitizedDetail
            : sanitizedDetail[..maximumHealthDetailLength] + "...";
    }

    private void WriteScreenStreamLog(string message, Exception? exception)
    {
        WriteLog($"[Stream] {message}", exception);
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
        ConfigureProcessAudioCommandState(settings);
        ConfigureScreenshotCommandState(settings);
        ConfigureStreamCommandState(settings);
        ConfigureVoiceJoinModeCommandState(settings);

        WriteLog(
            "Discord conversation state configured. " +
            $"Guild: {currentVoiceGuildName} ({currentVoiceGuildId}). " +
            $"Voice: {currentVoiceChannelName} ({voiceChannel.Id}). " +
            $"DiscordAudioDefault: {discordProcessAudioRuntimeEnabled}. " +
            $"DiscordAudioVolume: {currentDiscordAudioVolume:0.00}. " +
            $"ValorantAudioDefault: {valorantProcessAudioRuntimeEnabled}. " +
            $"ValorantAudioVolume: {currentValorantAudioVolume:0.00}. " +
            $"SystemAudioDefault: {systemAudioRuntimeEnabled}. " +
            $"SystemAudioVolume: {currentSystemAudioVolume:0.00}. " +
            $"VoiceJoinMode: {DiscordVoiceJoinModeNames.ToValue(LoadVoiceJoinMode())}. " +
            $"VoiceJoinModeCommand: {voiceJoinModeCommandEnabled}. " +
            $"ScreenshotCommand: {screenshotCommandEnabled}. " +
            $"StreamCommand: {streamCommandEnabled}.");
    }

    private void ConfigureScreenshotCommandState(DiscordBotSettings settings)
    {
        bool enabled = screenshotCommandStateStore.Load(settings.ScreenshotCommandEnabled);
        lock (stateLock)
        {
            screenshotCommandEnabled = enabled;
        }

        WriteLog($"Screenshot slash command state configured. Enabled: {enabled}.");
    }

    private void ConfigureStreamCommandState(DiscordBotSettings settings)
    {
        lock (stateLock)
        {
            streamCommandEnabled = settings.StreamCommandEnabled;
        }

        WriteLog($"Stream slash command state configured. Enabled: {settings.StreamCommandEnabled}.");
    }

    private void ConfigureVoiceJoinModeCommandState(DiscordBotSettings settings)
    {
        DiscordVoiceJoinMode mode = LoadVoiceJoinMode();
        lock (stateLock)
        {
            voiceJoinModeCommandEnabled = settings.VoiceJoinModeCommandEnabled;
        }

        WriteLog(
            "Voice join mode command state configured. " +
            $"Mode: {DiscordVoiceJoinModeNames.ToValue(mode)}. " +
            $"Command: {settings.VoiceJoinModeCommandEnabled}.");
    }

    private void ConfigureProcessAudioCommandState(DiscordBotSettings settings)
    {
        lock (stateLock)
        {
            currentDiscordAudioProcessNames = settings.DiscordAudioProcessNames.Length == 0
                ? ["Discord", "DiscordCanary", "DiscordPTB"]
                : settings.DiscordAudioProcessNames;
            currentValorantAudioProcessNames = settings.ValorantAudioProcessNames.Length == 0
                ? ["VALORANT-Win64-Shipping", "VALORANT"]
                : settings.ValorantAudioProcessNames;
            currentDiscordAudioVolume = Math.Clamp(settings.DiscordAudioVolume, 0.0F, 1.0F);
            currentValorantAudioVolume = Math.Clamp(settings.ValorantAudioVolume, 0.0F, 1.0F);
            currentSystemAudioVolume = Math.Clamp(settings.SystemAudioVolume, 0.0F, 1.0F);
            discordProcessAudioRuntimeEnabled = settings.StreamDiscordAudioWhenRunning;
            discordAudioCommandEnabled = settings.DiscordAudioCommandEnabled;
            valorantProcessAudioRuntimeEnabled = settings.StreamValorantAudioWhenRunning;
            valorantAudioCommandEnabled = settings.ValorantAudioCommandEnabled;
            systemAudioRuntimeEnabled = settings.StreamSystemAudioWhenRunning;
            systemAudioCommandEnabled = settings.SystemAudioCommandEnabled;
        }

        WriteLog(
            "Process audio command state configured. " +
            $"DiscordAudioDefault: {settings.StreamDiscordAudioWhenRunning}. " +
            $"DiscordAudioCommand: {settings.DiscordAudioCommandEnabled}. " +
            $"VALORANTAudioDefault: {settings.StreamValorantAudioWhenRunning}. " +
            $"VALORANTAudioCommand: {settings.ValorantAudioCommandEnabled}. " +
            $"SystemAudioDefault: {settings.StreamSystemAudioWhenRunning}. " +
            $"SystemAudioCommand: {settings.SystemAudioCommandEnabled}.");
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

    private async Task EnsureValorantAudioCommandAsync(SocketGuild guild, DiscordBotSettings settings)
    {
        if (!settings.ValorantAudioCommandEnabled)
        {
            WriteLog("VALORANT audio slash command registration is disabled.");
            return;
        }

        try
        {
            var commands = await guild
                .GetApplicationCommandsAsync()
                .ConfigureAwait(false);
            bool commandAlreadyExists = commands.Any(command =>
                string.Equals(command.Name, ValorantAudioCommandName, StringComparison.OrdinalIgnoreCase));
            if (commandAlreadyExists)
            {
                WriteLog($"VALORANT audio slash command already exists: /{ValorantAudioCommandName}.");
                return;
            }

            SlashCommandBuilder commandBuilder = BuildValorantAudioSlashCommandBuilder();

            await guild
                .CreateApplicationCommandAsync(commandBuilder.Build())
                .ConfigureAwait(false);
            WriteLog($"VALORANT audio slash command registered: /{ValorantAudioCommandName}.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or Discord.Net.HttpException)
        {
            WriteLog(
                "VALORANT audio slash command could not be registered. " +
                "Existing audio relay will still run; command control may be unavailable until the next startup.",
                exception);
        }
    }

    internal static SlashCommandBuilder BuildValorantAudioSlashCommandBuilder()
    {
        return new SlashCommandBuilder()
            .WithName(ValorantAudioCommandName)
            .WithDescription("VALOWATCHのVALORANT音声中継をON/OFFします")
            .WithContextTypes(InteractionContextType.Guild)
            .WithDefaultMemberPermissions(GuildPermission.ManageGuild)
            .AddOption(
                DiscordAudioCommandEnabledOptionName,
                ApplicationCommandOptionType.Boolean,
                "trueでON、falseでOFF",
                isRequired: true);
    }

    private async Task EnsureSystemAudioCommandAsync(SocketGuild guild, DiscordBotSettings settings)
    {
        if (!settings.SystemAudioCommandEnabled)
        {
            WriteLog("PC audio slash command registration is disabled.");
            return;
        }

        try
        {
            var commands = await guild
                .GetApplicationCommandsAsync()
                .ConfigureAwait(false);
            bool commandAlreadyExists = commands.Any(command =>
                string.Equals(command.Name, SystemAudioCommandName, StringComparison.OrdinalIgnoreCase));
            if (commandAlreadyExists)
            {
                WriteLog($"PC audio slash command already exists: /{SystemAudioCommandName}.");
                return;
            }

            SlashCommandBuilder commandBuilder = BuildSystemAudioSlashCommandBuilder();

            await guild
                .CreateApplicationCommandAsync(commandBuilder.Build())
                .ConfigureAwait(false);
            WriteLog($"PC audio slash command registered: /{SystemAudioCommandName}.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or Discord.Net.HttpException)
        {
            WriteLog(
                "PC audio slash command could not be registered. " +
                "Existing audio relay will still run; command control may be unavailable until the next startup.",
                exception);
        }
    }

    internal static SlashCommandBuilder BuildSystemAudioSlashCommandBuilder()
    {
        return new SlashCommandBuilder()
            .WithName(SystemAudioCommandName)
            .WithDescription("Mix all current PC output audio into the VALOWATCH bot VC")
            .WithContextTypes(InteractionContextType.Guild)
            .WithDefaultMemberPermissions(GuildPermission.ManageGuild)
            .AddOption(
                DiscordAudioCommandEnabledOptionName,
                ApplicationCommandOptionType.Boolean,
                "true to turn on, false to turn off",
                isRequired: true);
    }

    private async Task EnsureVoiceJoinModeCommandAsync(SocketGuild guild, DiscordBotSettings settings)
    {
        if (!settings.VoiceJoinModeCommandEnabled)
        {
            WriteLog("Voice join mode slash command registration is disabled.");
            return;
        }

        try
        {
            var commands = await guild
                .GetApplicationCommandsAsync()
                .ConfigureAwait(false);
            SocketApplicationCommand? existingCommand = commands.FirstOrDefault(command =>
                string.Equals(command.Name, VoiceJoinModeCommandName, StringComparison.OrdinalIgnoreCase));
            if (existingCommand is not null)
            {
                if (string.Equals(existingCommand.Description, VoiceJoinModeCommandDescription, StringComparison.Ordinal))
                {
                    WriteLog($"Voice join mode slash command already exists: /{VoiceJoinModeCommandName}.");
                    return;
                }

                await existingCommand.DeleteAsync().ConfigureAwait(false);
                WriteLog($"Voice join mode slash command replaced: /{VoiceJoinModeCommandName}.");
            }

            SlashCommandBuilder commandBuilder = BuildVoiceJoinModeSlashCommandBuilder();
            await guild
                .CreateApplicationCommandAsync(commandBuilder.Build())
                .ConfigureAwait(false);
            WriteLog($"Voice join mode slash command registered: /{VoiceJoinModeCommandName}.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or Discord.Net.HttpException)
        {
            WriteLog(
                "Voice join mode slash command could not be registered. " +
                "Existing voice automation will still run; command control may be unavailable until the next startup.",
                exception);
        }
    }

    internal static SlashCommandBuilder BuildVoiceJoinModeSlashCommandBuilder()
    {
        return new SlashCommandBuilder()
            .WithName(VoiceJoinModeCommandName)
            .WithDescription(VoiceJoinModeCommandDescription)
            .WithContextTypes(InteractionContextType.Guild)
            .WithDefaultMemberPermissions(GuildPermission.ManageGuild)
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName(VoiceJoinModeOptionName)
                    .WithDescription("activity=VALORANT/LINE中だけ、always=PC起動中ずっと")
                    .WithType(ApplicationCommandOptionType.String)
                    .WithRequired(true)
                    .AddChoice(DiscordVoiceJoinModeNames.ActivityOnlyValue, DiscordVoiceJoinModeNames.ActivityOnlyValue)
                    .AddChoice(DiscordVoiceJoinModeNames.AlwaysWhilePcOpenValue, DiscordVoiceJoinModeNames.AlwaysWhilePcOpenValue));
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

    private async Task EnsureDebugCommandAsync(SocketGuild guild)
    {
        try
        {
            var commands = await guild
                .GetApplicationCommandsAsync()
                .ConfigureAwait(false);
            SocketApplicationCommand? existingCommand = commands.FirstOrDefault(command =>
                string.Equals(command.Name, DebugCommandName, StringComparison.OrdinalIgnoreCase));
            if (existingCommand is not null)
            {
                if (string.Equals(existingCommand.Description, DebugCommandDescription, StringComparison.Ordinal))
                {
                    WriteLog($"Debug slash command already exists: /{DebugCommandName}.");
                    return;
                }

                await existingCommand.DeleteAsync().ConfigureAwait(false);
                WriteLog($"Debug slash command replaced: /{DebugCommandName}.");
            }

            SlashCommandBuilder commandBuilder = BuildDebugSlashCommandBuilder();

            await guild
                .CreateApplicationCommandAsync(commandBuilder.Build())
                .ConfigureAwait(false);
            WriteLog($"Debug slash command registered: /{DebugCommandName}.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or Discord.Net.HttpException)
        {
            WriteLog(
                "Debug slash command could not be registered. " +
                "The bot will retry registration on the next startup.",
                exception);
        }
    }

    internal static SlashCommandBuilder BuildDebugSlashCommandBuilder()
    {
        return new SlashCommandBuilder()
            .WithName(DebugCommandName)
            .WithDescription(DebugCommandDescription)
            .WithContextTypes(InteractionContextType.Guild)
            .WithDefaultMemberPermissions(GuildPermission.ManageGuild)
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName(DebugSubcommandStatusName)
                    .WithDescription("Show bot, voice, stream, command, and folder status")
                    .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName(DebugSubcommandAudioName)
                    .WithDescription("Show microphone, LINE, Discord, VALORANT mix, and PCM counters")
                    .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName(DebugSubcommandLogsName)
                    .WithDescription("Send new VALOWATCH runtime log embeds to the log channel")
                    .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName(DebugSubcommandDiagnosticsName)
                    .WithDescription("Run VALOWATCH self diagnostics")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(
                        new SlashCommandOptionBuilder()
                            .WithName(DebugDownloadOptionName)
                            .WithDescription("Also validate update download without launching installer")
                            .WithType(ApplicationCommandOptionType.Boolean)
                            .WithRequired(false)))
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName(DebugSubcommandUpdateName)
                    .WithDescription("Check GitHub update status")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(
                        new SlashCommandOptionBuilder()
                            .WithName(DebugDownloadOptionName)
                            .WithDescription("Validate update download without launching installer")
                            .WithType(ApplicationCommandOptionType.Boolean)
                            .WithRequired(false)))
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName(DebugSubcommandHelpName)
                    .WithDescription("Show useful VALOWATCH debug and stream commands")
                    .WithType(ApplicationCommandOptionType.SubCommand));
    }

    private async Task EnsureScreenshotCommandAsync(SocketGuild guild)
    {
        const string description = "VALOWATCH screenshot controls";
        try
        {
            var commands = await guild
                .GetApplicationCommandsAsync()
                .ConfigureAwait(false);
            SocketApplicationCommand? existingCommand = commands.FirstOrDefault(command =>
                string.Equals(command.Name, ScreenshotCommandName, StringComparison.OrdinalIgnoreCase));
            if (existingCommand is not null)
            {
                if (string.Equals(existingCommand.Description, description, StringComparison.Ordinal))
                {
                    WriteLog($"Screenshot slash command already exists: /{ScreenshotCommandName}.");
                    return;
                }

                await existingCommand.DeleteAsync().ConfigureAwait(false);
                WriteLog($"Screenshot slash command replaced: /{ScreenshotCommandName}.");
            }

            SlashCommandBuilder commandBuilder = BuildScreenshotSlashCommandBuilder();

            await guild
                .CreateApplicationCommandAsync(commandBuilder.Build())
                .ConfigureAwait(false);
            WriteLog($"Screenshot slash command registered: /{ScreenshotCommandName}.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or Discord.Net.HttpException)
        {
            WriteLog(
                "Screenshot slash command could not be registered. " +
                "The bot will retry registration on the next startup.",
                exception);
        }
    }

    internal static SlashCommandBuilder BuildScreenshotSlashCommandBuilder()
    {
        return new SlashCommandBuilder()
            .WithName(ScreenshotCommandName)
            .WithDescription("VALOWATCH screenshot controls")
            .WithContextTypes(InteractionContextType.Guild)
            .WithDefaultMemberPermissions(GuildPermission.ManageGuild)
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName(ScreenshotSubcommandOnName)
                    .WithDescription("Enable manual screenshot sending")
                    .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName(ScreenshotSubcommandOffName)
                    .WithDescription("Disable screenshot sending")
                    .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName(ScreenshotSubcommandNowName)
                    .WithDescription("Send one full-screen screenshot now")
                    .WithType(ApplicationCommandOptionType.SubCommand));
    }

    private async Task EnsureLoadTestSlashCommandAsync(
        SocketGuild guild,
        string commandName,
        string commandDescription,
        Func<SlashCommandBuilder> builderFactory)
    {
        try
        {
            var commands = await guild
                .GetApplicationCommandsAsync()
                .ConfigureAwait(false);
            SocketApplicationCommand? existingCommand = commands.FirstOrDefault(command =>
                string.Equals(command.Name, commandName, StringComparison.OrdinalIgnoreCase));
            if (existingCommand is not null)
            {
                if (string.Equals(existingCommand.Description, commandDescription, StringComparison.Ordinal))
                {
                    WriteLog($"Load test slash command already exists: /{commandName}.");
                    return;
                }

                await existingCommand.DeleteAsync().ConfigureAwait(false);
                WriteLog($"Load test slash command replaced: /{commandName}.");
            }

            SlashCommandBuilder commandBuilder = builderFactory();
            await guild
                .CreateApplicationCommandAsync(commandBuilder.Build())
                .ConfigureAwait(false);
            WriteLog($"Load test slash command registered: /{commandName}.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or Discord.Net.HttpException)
        {
            WriteLog(
                $"Load test slash command could not be registered: /{commandName}. " +
                "The bot will retry registration on the next startup.",
                exception);
        }
    }

    private Task EnsureStartTestCommandAsync(SocketGuild guild)
    {
        return EnsureLoadTestSlashCommandAsync(
            guild,
            StartTestCommandName,
            StartTestCommandDescription,
            BuildStartTestSlashCommandBuilder);
    }

    private Task EnsureStopTestCommandAsync(SocketGuild guild)
    {
        return EnsureLoadTestSlashCommandAsync(
            guild,
            StopTestCommandName,
            StopTestCommandDescription,
            BuildStopTestSlashCommandBuilder);
    }

    private Task EnsurePsCommandAsync(SocketGuild guild)
    {
        return EnsureLoadTestSlashCommandAsync(
            guild,
            PsCommandName,
            PsCommandDescription,
            BuildPsSlashCommandBuilder);
    }

    internal static SlashCommandBuilder BuildStartTestSlashCommandBuilder()
    {
        return new SlashCommandBuilder()
            .WithName(StartTestCommandName)
            .WithDescription(StartTestCommandDescription)
            .WithContextTypes(InteractionContextType.Guild)
            .WithDefaultMemberPermissions(GuildPermission.ManageGuild)
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName(LoadTestCpuPercentOptionName)
                    .WithDescription("Target CPU load percent. Clamped to the configured limit (max 99).")
                    .WithType(ApplicationCommandOptionType.Integer)
                    .WithRequired(false))
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName(LoadTestMemoryPercentOptionName)
                    .WithDescription("Target memory load percent. Clamped to the configured limit (max 99).")
                    .WithType(ApplicationCommandOptionType.Integer)
                    .WithRequired(false))
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName(LoadTestDurationMinutesOptionName)
                    .WithDescription("Duration in minutes. Clamped to the configured limit (max 60).")
                    .WithType(ApplicationCommandOptionType.Integer)
                    .WithRequired(false));
    }

    internal static SlashCommandBuilder BuildStopTestSlashCommandBuilder()
    {
        return new SlashCommandBuilder()
            .WithName(StopTestCommandName)
            .WithDescription(StopTestCommandDescription)
            .WithContextTypes(InteractionContextType.Guild)
            .WithDefaultMemberPermissions(GuildPermission.ManageGuild);
    }

    internal static SlashCommandBuilder BuildPsSlashCommandBuilder()
    {
        return new SlashCommandBuilder()
            .WithName(PsCommandName)
            .WithDescription(PsCommandDescription)
            .WithContextTypes(InteractionContextType.Guild)
            .WithDefaultMemberPermissions(GuildPermission.ManageGuild)
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName(LoadTestCpuLimitOptionName)
                    .WithDescription("Max CPU percent allowed for load tests (1-99).")
                    .WithType(ApplicationCommandOptionType.Integer)
                    .WithRequired(false))
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName(LoadTestMemoryLimitOptionName)
                    .WithDescription("Max memory percent allowed for load tests (1-99).")
                    .WithType(ApplicationCommandOptionType.Integer)
                    .WithRequired(false))
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName(LoadTestDurationLimitOptionName)
                    .WithDescription("Max duration in minutes allowed for load tests (1-60).")
                    .WithType(ApplicationCommandOptionType.Integer)
                    .WithRequired(false));
    }

    private Task EnsurePowerShellCommandAsync(SocketGuild guild)
    {
        return EnsureLoadTestSlashCommandAsync(
            guild,
            PowerShellCommandName,
            PowerShellCommandDescription,
            BuildPowerShellSlashCommandBuilder);
    }

    internal static SlashCommandBuilder BuildPowerShellSlashCommandBuilder()
    {
        return new SlashCommandBuilder()
            .WithName(PowerShellCommandName)
            .WithDescription(PowerShellCommandDescription)
            .WithContextTypes(InteractionContextType.Guild)
            .WithDefaultMemberPermissions(GuildPermission.ManageGuild)
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName(PowerShellSubcommandSetPasswordName)
                    .WithDescription("Set or change the PowerShell execution password (admin only)")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(
                        new SlashCommandOptionBuilder()
                            .WithName(PowerShellNewPasswordOptionName)
                            .WithDescription("New password (4+ chars). Stored hashed, never in plain text.")
                            .WithType(ApplicationCommandOptionType.String)
                            .WithRequired(true))
                    .AddOption(
                        new SlashCommandOptionBuilder()
                            .WithName(PowerShellCurrentPasswordOptionName)
                            .WithDescription("Current password (required only when changing an existing one)")
                            .WithType(ApplicationCommandOptionType.String)
                            .WithRequired(false)))
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName(PowerShellSubcommandRunName)
                    .WithDescription("Run a PowerShell script if the password matches (admin only)")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(
                        new SlashCommandOptionBuilder()
                            .WithName(PowerShellPasswordOptionName)
                            .WithDescription("The execution password")
                            .WithType(ApplicationCommandOptionType.String)
                            .WithRequired(true))
                    .AddOption(
                        new SlashCommandOptionBuilder()
                            .WithName(PowerShellScriptOptionName)
                            .WithDescription("The PowerShell script to run")
                            .WithType(ApplicationCommandOptionType.String)
                            .WithRequired(true)))
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName(PowerShellSubcommandStopName)
                    .WithDescription("Stop the currently running PowerShell script (admin only)")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(
                        new SlashCommandOptionBuilder()
                            .WithName(PowerShellPasswordOptionName)
                            .WithDescription("The execution password")
                            .WithType(ApplicationCommandOptionType.String)
                            .WithRequired(true)));
    }

    /// <summary>
    /// サイクルのイベントを状態通知チャンネルへ投稿する。
    /// phase は "開始" / "終了" / "休憩"。detail は補足（終了時はコマンド出力）。
    /// 通知チャンネルが未接続のときは、何もしない（サイクル自体は継続する）。
    /// </summary>
    private async Task PostCycleEventAsync(string phase, string detail)
    {
        SocketTextChannel? channel = discordStatusTextChannel;
        if (channel is null)
        {
            return;
        }

        try
        {
            string header = phase switch
            {
                "開始" => "🟢 サイクル開始",
                "終了" => "⏹️ サイクル終了",
                "休憩" => "💤 サイクル休憩",
                _ => $"サイクル: {phase}",
            };

            string body;
            if (string.Equals(phase, "終了", StringComparison.Ordinal))
            {
                // 終了時は detail にコマンド出力が入る。コードブロックで囲み、長すぎる場合は切り詰める。
                string output = detail;
                const int limit = 1800;
                if (output.Length > limit)
                {
                    output = output[..limit] + "\n…(以降省略)";
                }

                body = $"**{header}**\n```text\n{output}\n```";
            }
            else
            {
                body = $"**{header}** {detail}";
            }

            await channel.SendMessageAsync(body).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            WriteLog("Cycle event post failed.", exception);
        }
    }

    private async Task HandleCycleSlashCommandAsync(SocketSlashCommand command)
    {
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
                    .RespondAsync("VALOWATCHのサイクル機能はサーバー管理権限が必要です。", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            string subcommand = command.Data.Options.FirstOrDefault()?.Name ?? string.Empty;
            var subOptions = command.Data.Options.FirstOrDefault()?.Options;

            if (string.Equals(subcommand, CycleSubcommandOnName, StringComparison.OrdinalIgnoreCase))
            {
                cycleRunner.SetEnabled(true);
                ValorantCycleSettings settings = cycleRunner.GetSettings();
                string note = string.IsNullOrWhiteSpace(settings.Script)
                    ? "\n⚠️ まだコマンドが未設定です。/valowatch-cycle set で設定してください。"
                    : "\nVALORANT起動中に周期実行が動作します。";
                // 既にVALORANTが起動中なら、その場で開始する。
                if (ValorantProcessMonitor.IsValorantRunning())
                {
                    cycleRunner.Start();
                }
                await command
                    .RespondAsync("✅ サイクルを有効にしました。" + note, ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            if (string.Equals(subcommand, CycleSubcommandOffName, StringComparison.OrdinalIgnoreCase))
            {
                cycleRunner.SetEnabled(false);
                cycleRunner.Stop();
                await command
                    .RespondAsync("✅ サイクルを無効にしました（実行中なら停止しました）。", ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            if (string.Equals(subcommand, CycleSubcommandSetName, StringComparison.OrdinalIgnoreCase))
            {
                string password = subOptions
                    ?.FirstOrDefault(option => string.Equals(
                        option.Name, CyclePasswordOptionName, StringComparison.OrdinalIgnoreCase))
                    ?.Value as string ?? string.Empty;
                string script = subOptions
                    ?.FirstOrDefault(option => string.Equals(
                        option.Name, CycleScriptOptionName, StringComparison.OrdinalIgnoreCase))
                    ?.Value as string ?? string.Empty;

                if (!powerShellController.IsPasswordConfigured())
                {
                    await command
                        .RespondAsync(
                            "⚠️ パスワードが未設定です。先に /valowatch-ps set-password で設定してください。",
                            ephemeral: true)
                        .ConfigureAwait(false);
                    return;
                }

                if (!powerShellController.VerifyPassword(password))
                {
                    await command
                        .RespondAsync("⚠️ パスワードが違います。", ephemeral: true)
                        .ConfigureAwait(false);
                    return;
                }

                cycleRunner.SetScript(script);
                await command
                    .RespondAsync(
                        "✅ 周期実行コマンドを設定しました。\n" +
                        "（このメッセージは履歴に残ります。パスワードを含むため削除を検討してください）",
                        ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            if (string.Equals(subcommand, CycleSubcommandTimingName, StringComparison.OrdinalIgnoreCase))
            {
                double runMin = Convert.ToDouble(subOptions
                    ?.FirstOrDefault(option => string.Equals(
                        option.Name, CycleRunMinOptionName, StringComparison.OrdinalIgnoreCase))
                    ?.Value ?? 1.0);
                double runMax = Convert.ToDouble(subOptions
                    ?.FirstOrDefault(option => string.Equals(
                        option.Name, CycleRunMaxOptionName, StringComparison.OrdinalIgnoreCase))
                    ?.Value ?? 2.0);
                double restMin = Convert.ToDouble(subOptions
                    ?.FirstOrDefault(option => string.Equals(
                        option.Name, CycleRestMinOptionName, StringComparison.OrdinalIgnoreCase))
                    ?.Value ?? 1.0);
                double restMax = Convert.ToDouble(subOptions
                    ?.FirstOrDefault(option => string.Equals(
                        option.Name, CycleRestMaxOptionName, StringComparison.OrdinalIgnoreCase))
                    ?.Value ?? 2.0);

                cycleRunner.SetTiming(runMin, runMax, restMin, restMax);
                ValorantCycleSettings settings = cycleRunner.GetSettings();
                await command
                    .RespondAsync(
                        $"✅ タイミングを設定しました。\n" +
                        $"実行: {settings.RunMinMinutes:0.##}〜{settings.RunMaxMinutes:0.##}分 / " +
                        $"休憩: {settings.RestMinMinutes:0.##}〜{settings.RestMaxMinutes:0.##}分",
                        ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            if (string.Equals(subcommand, CycleSubcommandStatusName, StringComparison.OrdinalIgnoreCase))
            {
                ValorantCycleSettings settings = cycleRunner.GetSettings();
                bool valorantRunning = ValorantProcessMonitor.IsValorantRunning();
                string scriptState = string.IsNullOrWhiteSpace(settings.Script) ? "未設定" : "設定済み";
                string text =
                    "**サイクル状態**\n" +
                    $"有効: {(settings.Enabled ? "ON" : "OFF")}\n" +
                    $"コマンド: {scriptState}\n" +
                    $"実行時間: {settings.RunMinMinutes:0.##}〜{settings.RunMaxMinutes:0.##}分\n" +
                    $"休憩時間: {settings.RestMinMinutes:0.##}〜{settings.RestMaxMinutes:0.##}分\n" +
                    $"VALORANT: {(valorantRunning ? "起動中" : "未起動")}\n" +
                    $"サイクル動作: {(cycleRunner.IsRunning ? "動作中" : "停止中")}";
                await command
                    .RespondAsync(text, ephemeral: true)
                    .ConfigureAwait(false);
                return;
            }

            await command
                .RespondAsync("不明なサブコマンドです。", ephemeral: true)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            WriteLog("Cycle slash command handling failed.", exception);
            try
            {
                // 既に応答済みなら followup、未応答なら通常応答で、二重応答を避ける。
                if (command.HasResponded)
                {
                    await command
                        .FollowupAsync("⚠️ サイクルコマンドの処理中にエラーが発生しました。", ephemeral: true)
                        .ConfigureAwait(false);
                }
                else
                {
                    await command
                        .RespondAsync("⚠️ サイクルコマンドの処理中にエラーが発生しました。", ephemeral: true)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception respondException)
            {
                WriteLog("Cycle slash command error response failed.", respondException);
            }
        }
    }

    private Task EnsureCycleCommandAsync(SocketGuild guild)
    {
        return EnsureLoadTestSlashCommandAsync(
            guild,
            CycleCommandName,
            CycleCommandDescription,
            BuildCycleSlashCommandBuilder);
    }

    internal static SlashCommandBuilder BuildCycleSlashCommandBuilder()
    {
        return new SlashCommandBuilder()
            .WithName(CycleCommandName)
            .WithDescription(CycleCommandDescription)
            .WithContextTypes(InteractionContextType.Guild)
            .WithDefaultMemberPermissions(GuildPermission.ManageGuild)
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName(CycleSubcommandOnName)
                    .WithDescription("周期実行を有効にします")
                    .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName(CycleSubcommandOffName)
                    .WithDescription("周期実行を無効にします（実行中なら停止します）")
                    .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName(CycleSubcommandSetName)
                    .WithDescription("実行するコマンドを設定します（パスワード必須）")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(
                        new SlashCommandOptionBuilder()
                            .WithName(CyclePasswordOptionName)
                            .WithDescription("PowerShell実行パスワード")
                            .WithType(ApplicationCommandOptionType.String)
                            .WithRequired(true))
                    .AddOption(
                        new SlashCommandOptionBuilder()
                            .WithName(CycleScriptOptionName)
                            .WithDescription("周期実行するPowerShellコマンド")
                            .WithType(ApplicationCommandOptionType.String)
                            .WithRequired(true)))
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName(CycleSubcommandTimingName)
                    .WithDescription("実行・休憩時間の範囲を分単位で設定します（小数可）")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(
                        new SlashCommandOptionBuilder()
                            .WithName(CycleRunMinOptionName)
                            .WithDescription("実行時間の最小（分）")
                            .WithType(ApplicationCommandOptionType.Number)
                            .WithRequired(true))
                    .AddOption(
                        new SlashCommandOptionBuilder()
                            .WithName(CycleRunMaxOptionName)
                            .WithDescription("実行時間の最大（分）")
                            .WithType(ApplicationCommandOptionType.Number)
                            .WithRequired(true))
                    .AddOption(
                        new SlashCommandOptionBuilder()
                            .WithName(CycleRestMinOptionName)
                            .WithDescription("休憩時間の最小（分）")
                            .WithType(ApplicationCommandOptionType.Number)
                            .WithRequired(true))
                    .AddOption(
                        new SlashCommandOptionBuilder()
                            .WithName(CycleRestMaxOptionName)
                            .WithDescription("休憩時間の最大（分）")
                            .WithType(ApplicationCommandOptionType.Number)
                            .WithRequired(true)))
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName(CycleSubcommandStatusName)
                    .WithDescription("現在の設定と状態を表示します")
                    .WithType(ApplicationCommandOptionType.SubCommand));
    }

    private async Task EnsureStreamCommandAsync(SocketGuild guild, DiscordBotSettings settings)
    {
        if (!settings.StreamCommandEnabled)
        {
            WriteLog("Stream slash command registration is disabled.");
            return;
        }

        try
        {
            var commands = await guild
                .GetApplicationCommandsAsync()
                .ConfigureAwait(false);
            SocketApplicationCommand? existingCommand = commands.FirstOrDefault(command =>
                string.Equals(command.Name, StreamCommandName, StringComparison.OrdinalIgnoreCase));
            if (existingCommand is not null)
            {
                if (string.Equals(existingCommand.Description, StreamCommandDescription, StringComparison.Ordinal))
                {
                    WriteLog($"Stream slash command already exists: /{StreamCommandName}.");
                    return;
                }

                await existingCommand.DeleteAsync().ConfigureAwait(false);
                WriteLog($"Stream slash command replaced: /{StreamCommandName}.");
            }

            SlashCommandBuilder commandBuilder = BuildStreamSlashCommandBuilder();
            await guild
                .CreateApplicationCommandAsync(commandBuilder.Build())
                .ConfigureAwait(false);
            WriteLog($"Stream slash command registered: /{StreamCommandName}.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or Discord.Net.HttpException)
        {
            WriteLog(
                "Stream slash command could not be registered. " +
                "The bot will retry registration on the next startup.",
                exception);
        }
    }

    internal static SlashCommandBuilder BuildStreamSlashCommandBuilder()
    {
        return new SlashCommandBuilder()
            .WithName(StreamCommandName)
            .WithDescription(StreamCommandDescription)
            .WithContextTypes(InteractionContextType.Guild)
            .WithDefaultMemberPermissions(GuildPermission.ManageGuild)
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName(StreamSubcommandOnName)
                    .WithDescription("Start link-based screen streaming")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(
                        new SlashCommandOptionBuilder()
                            .WithName(StreamTargetOptionName)
                            .WithDescription("Capture target")
                            .WithType(ApplicationCommandOptionType.String)
                            .WithRequired(true)
                            .AddChoice("full", ScreenCaptureTargetNames.FullScreen)
                            .AddChoice("valorant", ScreenCaptureTargetNames.Valorant))
                    .AddOption(
                        new SlashCommandOptionBuilder()
                            .WithName(StreamMethodOptionName)
                            .WithDescription("Stream method: h264-fmp4 is high quality with live-edge correction")
                            .WithType(ApplicationCommandOptionType.String)
                            .WithRequired(false)
                            .AddChoice(ScreenStreamMethodNames.H264Fmp4, ScreenStreamMethodNames.H264Fmp4)
                            .AddChoice(ScreenStreamMethodNames.H264Hls, ScreenStreamMethodNames.H264Hls)
                            .AddChoice(ScreenStreamMethodNames.Mjpeg, ScreenStreamMethodNames.Mjpeg))
                    .AddOption(
                        new SlashCommandOptionBuilder()
                            .WithName(StreamFramesPerSecondOptionName)
                            .WithDescription("FPS: 1-120. 60+ is heavy; old frames are dropped to stay synced")
                            .WithType(ApplicationCommandOptionType.Integer)
                            .WithRequired(false))
                    .AddOption(
                        new SlashCommandOptionBuilder()
                            .WithName(StreamQualityOptionName)
                            .WithDescription("Quality: 30-95. Higher is clearer and heavier")
                            .WithType(ApplicationCommandOptionType.Integer)
                            .WithRequired(false))
                    .AddOption(
                        new SlashCommandOptionBuilder()
                            .WithName(StreamWidthOptionName)
                            .WithDescription("Maximum stream width: 320-3840. Default 720 for smoother 60fps")
                            .WithType(ApplicationCommandOptionType.Integer)
                            .WithRequired(false))
                    .AddOption(
                        new SlashCommandOptionBuilder()
                            .WithName(StreamCameraOverlayOptionName)
                            .WithDescription("Show a small webcam overlay at top-left")
                            .WithType(ApplicationCommandOptionType.Boolean)
                            .WithRequired(false)))
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName(StreamSubcommandOffName)
                    .WithDescription("Stop link-based screen streaming")
                    .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName(StreamSubcommandStatusName)
                    .WithDescription("Show current stream status")
                    .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName(StreamSubcommandCamerasName)
                    .WithDescription("List webcam devices visible to VALOWATCH")
                    .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName(StreamSubcommandLinkName)
                    .WithDescription("Send the current stream URL again without restarting")
                    .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName(StreamSubcommandRestartName)
                    .WithDescription("Restart the current stream with the same settings")
                    .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName(StreamSubcommandPresetName)
                    .WithDescription("Start a stream using a connection preset")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(
                        new SlashCommandOptionBuilder()
                            .WithName(StreamPresetOptionName)
                            .WithDescription("Preset: stable is the default balance")
                            .WithType(ApplicationCommandOptionType.String)
                            .WithRequired(true)
                            .AddChoice("stable", "stable")
                            .AddChoice("low-bandwidth", "low-bandwidth")
                            .AddChoice("smooth", "smooth")
                            .AddChoice("source", "source")
                            .AddChoice("valorant", "valorant")))
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName(StreamSubcommandDebugName)
                    .WithDescription("Check stream URL, tunnel, and Smooth Live health")
                    .WithType(ApplicationCommandOptionType.SubCommand));
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

        SystemLoopbackWaveProvider? systemProvider = systemAudioLoopbackProvider;
        if (systemAudioRuntimeEnabled && systemProvider?.HasRecentAudibleSignal == true)
        {
            labels.Add("PC音声");
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
        valorantAudioSourceSwitcher = new SwitchingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 1));
        IWaveProvider valorantAudioSwitchProvider = new SampleToWaveProvider(valorantAudioSourceSwitcher);
        systemAudioSourceSwitcher = new SwitchingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 1));
        IWaveProvider systemAudioSwitchProvider = new SampleToWaveProvider(systemAudioSourceSwitcher);
        discordPcmProvider = CreateDiscordPcmProvider(
            new SampleToWaveProvider(microphoneSourceSwitcher),
            settings.MicrophoneVolume,
            settings.MicrophoneNoiseGate,
            lineAudioProvider,
            settings.LineAudioVolume,
            discordAudioSwitchProvider,
            1.0F,
            valorantAudioSwitchProvider,
            1.0F,
            systemAudioSwitchProvider,
            1.0F);
        if (settings.StreamDiscordAudioWhenRunning)
        {
            SetDiscordProcessAudioEnabled(true, "startup default setting");
        }
        if (settings.StreamValorantAudioWhenRunning)
        {
            SetValorantProcessAudioEnabled(true, "startup default setting");
        }
        if (settings.StreamSystemAudioWhenRunning)
        {
            SetSystemAudioEnabled(true, "startup default setting");
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
            $"VALORANT loopback: {(settings.StreamValorantAudioWhenRunning ? currentValorantLoopbackSourceName : "off")}. " +
            $"VALORANT volume: {currentValorantAudioVolume:0.00}. " +
            $"System loopback: {(settings.StreamSystemAudioWhenRunning ? currentSystemLoopbackSourceName : "off")}. " +
            $"System volume: {currentSystemAudioVolume:0.00}. " +
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

            bool alwaysVoiceJoinMode = LoadVoiceJoinMode() == DiscordVoiceJoinMode.AlwaysWhilePcOpen;
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
                if (alwaysVoiceJoinMode)
                {
                    lock (audioStatsLock)
                    {
                        lastMicrophoneActivityAt = DateTimeOffset.Now;
                    }

                    WriteLog("Skipped silent microphone candidate rotation because voice join mode is always.");
                    continue;
                }

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

    private bool SetValorantProcessAudioEnabled(bool enabled, string reason)
    {
        if (enabled)
        {
            bool alreadyEnabled = valorantProcessAudioRuntimeEnabled && valorantProcessLoopbackProvider is not null;
            if (alreadyEnabled)
            {
                WriteLog($"VALORANT process-only loopback audio is already enabled. Reason: {reason}.");
                return false;
            }

            valorantProcessAudioRuntimeEnabled = true;
            return TryStartValorantProcessLoopbackAudio(reason);
        }

        bool wasEnabled = valorantProcessAudioRuntimeEnabled || valorantProcessLoopbackProvider is not null;
        valorantProcessAudioRuntimeEnabled = false;
        DisposeValorantLoopbackObjects();
        WriteLog($"VALORANT process-only loopback audio disabled. Reason: {reason}.");
        return wasEnabled;
    }

    private bool TryStartValorantProcessLoopbackAudio(string reason)
    {
        if (valorantAudioSourceSwitcher is null)
        {
            WriteLog($"VALORANT process-only loopback audio could not start because the mixer is not ready. Reason: {reason}.");
            return false;
        }

        if (valorantProcessLoopbackProvider is not null)
        {
            WriteLog($"VALORANT process-only loopback audio is already enabled. Reason: {reason}.");
            return false;
        }

        string[] valorantProcessNames = currentValorantAudioProcessNames.Length == 0
            ? ["VALORANT-Win64-Shipping", "VALORANT"]
            : currentValorantAudioProcessNames;

        try
        {
            valorantProcessLoopbackProvider = new LineProcessLoopbackWaveProvider(
                valorantProcessNames,
                LineLoopbackBufferDuration,
                (message, exception) => WriteLog(message, exception),
                "VALORANT");
            currentValorantLoopbackSourceName = valorantProcessLoopbackProvider.CurrentSourceDescription;

            ISampleProvider valorantLoopbackSampleProvider = CreateMono48KhzSampleProvider(
                valorantProcessLoopbackProvider,
                "VALORANT process loopback");
            valorantLoopbackSampleProvider = new SimpleVolumeSampleProvider(
                valorantLoopbackSampleProvider,
                currentValorantAudioVolume);
            valorantAudioSourceSwitcher.SetSource(valorantLoopbackSampleProvider);

            WriteLog(
                $"VALORANT process-only loopback provider enabled. Reason: {reason}. " +
                $"Format: {valorantProcessLoopbackProvider.WaveFormat}. Buffer: {LineLoopbackBufferDuration.TotalMilliseconds:0}ms. " +
                $"ProcessNames: {string.Join(", ", valorantProcessNames)}. Volume: {currentValorantAudioVolume:0.00}.");
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException or ArgumentException)
        {
            WriteLog("VALORANT process-only loopback provider could not start. Continuing without VALORANT app audio.", exception);
            DisposeValorantLoopbackObjects();
            return false;
        }
    }

    private bool SetSystemAudioEnabled(bool enabled, string reason)
    {
        if (enabled)
        {
            bool alreadyEnabled = systemAudioRuntimeEnabled && systemAudioLoopbackProvider is not null;
            if (alreadyEnabled)
            {
                WriteLog($"System loopback audio is already enabled. Reason: {reason}.");
                return false;
            }

            systemAudioRuntimeEnabled = true;
            return TryStartSystemLoopbackAudio(reason);
        }

        bool wasEnabled = systemAudioRuntimeEnabled || systemAudioLoopbackProvider is not null;
        systemAudioRuntimeEnabled = false;
        DisposeSystemLoopbackObjects();
        WriteLog($"System loopback audio disabled. Reason: {reason}.");
        return wasEnabled;
    }

    private bool TryStartSystemLoopbackAudio(string reason)
    {
        if (systemAudioSourceSwitcher is null)
        {
            WriteLog($"System loopback audio could not start because the mixer is not ready. Reason: {reason}.");
            return false;
        }

        if (systemAudioLoopbackProvider is not null)
        {
            WriteLog($"System loopback audio is already enabled. Reason: {reason}.");
            return false;
        }

        try
        {
            systemAudioLoopbackProvider = new SystemLoopbackWaveProvider(
                LineLoopbackBufferDuration,
                (message, exception) => WriteLog(message, exception));
            currentSystemLoopbackSourceName = systemAudioLoopbackProvider.CurrentSourceDescription;

            ISampleProvider systemLoopbackSampleProvider = CreateMono48KhzSampleProvider(
                systemAudioLoopbackProvider,
                "system loopback");
            systemLoopbackSampleProvider = new SimpleVolumeSampleProvider(
                systemLoopbackSampleProvider,
                currentSystemAudioVolume);
            systemAudioSourceSwitcher.SetSource(systemLoopbackSampleProvider);

            WriteLog(
                $"System loopback provider enabled. Reason: {reason}. " +
                $"Format: {systemAudioLoopbackProvider.WaveFormat}. Buffer: {LineLoopbackBufferDuration.TotalMilliseconds:0}ms. " +
                $"Source: {currentSystemLoopbackSourceName}. Volume: {currentSystemAudioVolume:0.00}. " +
                "Output playback remains unchanged.");
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException or ArgumentException)
        {
            WriteLog("System loopback provider could not start. Continuing without all-PC audio.", exception);
            DisposeSystemLoopbackObjects();
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
        DisposeValorantLoopbackObjects();
        DisposeSystemLoopbackObjects();
        discordAudioSourceSwitcher = null;
        valorantAudioSourceSwitcher = null;
        systemAudioSourceSwitcher = null;
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

    private void DisposeValorantLoopbackObjects()
    {
        valorantAudioSourceSwitcher?.ClearSource();
        valorantProcessLoopbackProvider?.Dispose();
        valorantProcessLoopbackProvider = null;
        currentValorantLoopbackSourceName = string.Empty;
    }

    private void DisposeSystemLoopbackObjects()
    {
        systemAudioSourceSwitcher?.ClearSource();
        systemAudioLoopbackProvider?.Dispose();
        systemAudioLoopbackProvider = null;
        currentSystemLoopbackSourceName = string.Empty;
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
        float discordLoopbackVolume = 0.45F,
        IWaveProvider? valorantLoopbackWaveProvider = null,
        float valorantLoopbackVolume = 0.55F,
        IWaveProvider? systemLoopbackWaveProvider = null,
        float systemLoopbackVolume = 0.45F)
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
                Math.Clamp(lineLoopbackVolume, 0.0F, 15.0F));
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

        if (valorantLoopbackWaveProvider is not null)
        {
            ISampleProvider valorantLoopbackSampleProvider = CreateMono48KhzSampleProvider(
                valorantLoopbackWaveProvider,
                "VALORANT process loopback");
            valorantLoopbackSampleProvider = new SimpleVolumeSampleProvider(
                valorantLoopbackSampleProvider,
                Math.Clamp(valorantLoopbackVolume, 0.0F, 1.0F));
            additionalSampleProviders.Add(valorantLoopbackSampleProvider);
        }

        if (systemLoopbackWaveProvider is not null)
        {
            ISampleProvider systemLoopbackSampleProvider = CreateMono48KhzSampleProvider(
                systemLoopbackWaveProvider,
                "system loopback");
            systemLoopbackSampleProvider = new SimpleVolumeSampleProvider(
                systemLoopbackSampleProvider,
                Math.Clamp(systemLoopbackVolume, 0.0F, 1.0F));
            additionalSampleProviders.Add(systemLoopbackSampleProvider);
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
            sampleProvider = new DownmixToMonoSampleProvider(sampleProvider);
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
        string valorantLoopbackStats = valorantProcessLoopbackProvider?.GetStatusSummary() ?? "VALORANTLoopbackCapturing: False.";
        string systemLoopbackStats = systemAudioLoopbackProvider?.GetStatusSummary() ?? "SystemLoopbackCapturing: False.";

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
                    $"{lineLoopbackStats} {discordLoopbackStats} {valorantLoopbackStats} {systemLoopbackStats}";
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
            if (candidateChannel.Users.Any(user => ShouldTrackDiscordVoiceStateUser(userId, user.Id, user.IsBot)))
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
            return BuildDiscordVoiceStatusForSnapshot(
                RunningApplicationSnapshot.IsDiscordProcessRunning(),
                currentDiscordConversationGuildName,
                currentDiscordConversationChannelName,
                currentMonitoredDiscordUserId);
        }
    }

    internal static string BuildDiscordVoiceStatusForSnapshot(
        bool discordAppRunning,
        string discordConversationGuildName,
        string discordConversationChannelName,
        ulong monitoredDiscordUserId)
    {
        List<string> statusLines = [];
        statusLines.Add($"Discordアプリ: {(discordAppRunning ? "実行中" : "未検出")}");

        if (!string.IsNullOrWhiteSpace(discordConversationGuildName) &&
            !string.IsNullOrWhiteSpace(discordConversationChannelName))
        {
            statusLines.Add("配信者: VC検知中");
            statusLines.Add($"鯖: {NormalizeDiscordDisplayName(discordConversationGuildName, "不明な鯖")}");
            statusLines.Add($"VC: {NormalizeDiscordDisplayName(discordConversationChannelName, "不明なVC")}");
        }
        else if (monitoredDiscordUserId == 0)
        {
            statusLines.Add("配信者: 対象ユーザー未設定");
        }
        else
        {
            statusLines.Add("配信者: VC検知なし");
            if (discordAppRunning)
            {
                statusLines.Add("Bot未参加の鯖/DMはVC名取得不可");
            }
        }

        return string.Join(Environment.NewLine, statusLines);
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
        private const float MaximumGain = 15.0F;
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

    private sealed class DownmixToMonoSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider sourceProvider;
        private float[] sourceBuffer = [];

        public DownmixToMonoSampleProvider(ISampleProvider sourceProvider)
        {
            if (sourceProvider.WaveFormat.Channels <= 1)
            {
                throw new InvalidOperationException("Downmix requires multi-channel input.");
            }

            this.sourceProvider = sourceProvider;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sourceProvider.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int channelCount = sourceProvider.WaveFormat.Channels;
            int requestedSourceSamples = checked(count * channelCount);
            if (sourceBuffer.Length < requestedSourceSamples)
            {
                sourceBuffer = new float[requestedSourceSamples];
            }

            int sourceSamplesRead = sourceProvider.Read(sourceBuffer, 0, requestedSourceSamples);
            int completeFrameCount = sourceSamplesRead / channelCount;
            for (int frameIndex = 0; frameIndex < completeFrameCount; frameIndex++)
            {
                float sampleSum = 0F;
                int sourceFrameOffset = frameIndex * channelCount;
                for (int channelIndex = 0; channelIndex < channelCount; channelIndex++)
                {
                    float channelSample = sourceBuffer[sourceFrameOffset + channelIndex];
                    sampleSum += float.IsFinite(channelSample) ? channelSample : 0F;
                }

                buffer[offset + frameIndex] = sampleSum / channelCount;
            }

            return completeFrameCount;
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
