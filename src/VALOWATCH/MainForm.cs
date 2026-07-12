using System.Runtime.InteropServices;

namespace VALOWATCH;

public sealed class MainForm : Form
{
    private const int StratsHotKeyId = 9101;
    private static readonly TimeSpan DiscordRetryInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HotKeyRegistrationRetryInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HotKeyTriggerCooldown = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ValorantStopGracePeriod = TimeSpan.FromSeconds(20);

    private readonly AppPaths appPaths;
    private readonly DiscordBotVoiceRelay discordBotVoiceRelay;
    private readonly GitUpdateChecker gitUpdateChecker;
    private readonly GitAutoUpdater gitAutoUpdater;
    private readonly GitUpdateSchedule gitUpdateSchedule = new(GitUpdateSchedule.DefaultInterval);
    private readonly bool disableDiscordAutomation;
    private readonly System.Windows.Forms.Timer processTimer = new();
    private readonly System.Windows.Forms.Timer hotKeyHealthTimer = new();
    private readonly System.Windows.Forms.Timer stratsToggleDelayTimer = new();

    private StratsOverlayForm? stratsOverlayForm;
    private bool hotKeyRegistered;
    private bool hotKeyChordWasDown;
    private bool lastValorantDetected;
    private bool discordTransitionInProgress;
    private bool hidOnInitialShow;
    private bool stratsTogglePending;
    private bool stratsToggleInProgress;
    private bool stratsPreloadInProgress;
    private bool gitUpdateCheckInProgress;
    private DateTimeOffset? valorantMissingSinceUtc;
    private DateTimeOffset lastHotKeyTriggerAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset nextHotKeyRegistrationRetryAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset nextDiscordRetryAtUtc = DateTimeOffset.MinValue;

    public MainForm(
        AppPaths appPaths,
        DiscordBotVoiceRelay discordBotVoiceRelay,
        GitUpdateChecker gitUpdateChecker,
        GitAutoUpdater gitAutoUpdater,
        bool disableDiscordAutomation)
    {
        this.appPaths = appPaths;
        this.discordBotVoiceRelay = discordBotVoiceRelay;
        this.gitUpdateChecker = gitUpdateChecker;
        this.gitAutoUpdater = gitAutoUpdater;
        this.disableDiscordAutomation = disableDiscordAutomation;

        BuildHeadlessWindow();

        processTimer.Interval = 2000;
        processTimer.Tick += (_, _) => RefreshValorantStatus();
        processTimer.Start();

        hotKeyHealthTimer.Interval = 50;
        hotKeyHealthTimer.Tick += (_, _) => PollStratsHotKeyAndRepairRegistration();
        hotKeyHealthTimer.Start();

        stratsToggleDelayTimer.Interval = 30;
        stratsToggleDelayTimer.Tick += (_, _) => RunPendingStratsToggleAfterHotKeyRelease();
    }

    protected override void OnShown(EventArgs eventArgs)
    {
        base.OnShown(eventArgs);
        if (hidOnInitialShow)
        {
            return;
        }

        hidOnInitialShow = true;
        Hide();
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        RegisterStratsHotKey();
    }

    protected override void OnHandleDestroyed(EventArgs eventArgs)
    {
        UnregisterStratsHotKey();
        base.OnHandleDestroyed(eventArgs);
    }

    protected override void OnFormClosing(FormClosingEventArgs eventArgs)
    {
        UnregisterStratsHotKey();
        processTimer.Stop();
        hotKeyHealthTimer.Stop();
        stratsToggleDelayTimer.Stop();
        DisposeStratsOverlay();

        try
        {
            discordBotVoiceRelay.Dispose();
        }
        catch (Exception exception)
        {
            WriteAppLog("Discord", "Discord shutdown failed.", exception);
        }

        base.OnFormClosing(eventArgs);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == NativeMethods.WmHotKey && message.WParam.ToInt32() == StratsHotKeyId)
        {
            RequestStratsToggleFromHotKey("WM_HOTKEY");
            return;
        }

        base.WndProc(ref message);
    }

    private void BuildHeadlessWindow()
    {
        Text = "VALOWATCH";
        Size = new Size(1, 1);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        Opacity = 0;
    }

    private void RefreshValorantStatus()
    {
        bool rawValorantDetected = ValorantProcessMonitor.IsValorantRunning();
        bool valorantDetected = GetDebouncedValorantDetected(rawValorantDetected);

        if (!valorantDetected && stratsOverlayForm?.IsOverlayVisible == true)
        {
            stratsOverlayForm.HideOverlayKeepingPage();
        }

        if (valorantDetected != lastValorantDetected)
        {
            lastValorantDetected = valorantDetected;
            nextDiscordRetryAtUtc = valorantDetected
                ? DateTimeOffset.UtcNow.Add(DiscordRetryInterval)
                : DateTimeOffset.MinValue;

            if (valorantDetected)
            {
                gitUpdateSchedule.Reset();
                PreloadStratsOverlayIfNeeded();
                StartGitUpdateCheckIfNeeded(force: true);
            }

            _ = HandleValorantStateChangeAsync(valorantDetected);
        }
        else if (ShouldRetryDiscordStart(valorantDetected))
        {
            nextDiscordRetryAtUtc = DateTimeOffset.UtcNow.Add(DiscordRetryInterval);
            _ = HandleValorantStateChangeAsync(valorantDetected);
        }

        if (valorantDetected)
        {
            PreloadStratsOverlayIfNeeded();
        }

        StartGitUpdateCheckIfNeeded(force: false);
    }

    private bool GetDebouncedValorantDetected(bool rawValorantDetected)
    {
        if (rawValorantDetected)
        {
            valorantMissingSinceUtc = null;
            return true;
        }

        if (!lastValorantDetected)
        {
            valorantMissingSinceUtc = null;
            return false;
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        valorantMissingSinceUtc ??= nowUtc;
        return nowUtc - valorantMissingSinceUtc.Value < ValorantStopGracePeriod;
    }

    private async Task HandleValorantStateChangeAsync(bool valorantDetected)
    {
        if (discordTransitionInProgress)
        {
            return;
        }

        discordTransitionInProgress = true;
        try
        {
            if (valorantDetected && !disableDiscordAutomation)
            {
                await discordBotVoiceRelay.StartForValorantAsync().ConfigureAwait(true);
            }
            else if (!valorantDetected)
            {
                await discordBotVoiceRelay.StopAsync().ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            WriteAppLog("Discord", "Discord state transition failed.", exception);
        }
        finally
        {
            discordTransitionInProgress = false;
        }
    }

    private bool ShouldRetryDiscordStart(bool valorantDetected)
    {
        if (!valorantDetected || disableDiscordAutomation || discordTransitionInProgress)
        {
            return false;
        }

        if (discordBotVoiceRelay.IsRunning || !discordBotVoiceRelay.HasConfig)
        {
            return false;
        }

        return DateTimeOffset.UtcNow >= nextDiscordRetryAtUtc;
    }

    private void RegisterStratsHotKey()
    {
        if (hotKeyRegistered || Handle == IntPtr.Zero)
        {
            return;
        }

        hotKeyRegistered = NativeMethods.RegisterHotKey(
            Handle,
            StratsHotKeyId,
            NativeMethods.ModAlt | NativeMethods.ModNoRepeat,
            NativeMethods.VirtualKeyT);

        if (hotKeyRegistered)
        {
            nextHotKeyRegistrationRetryAtUtc = DateTimeOffset.MaxValue;
            WriteAppLog("Overlay", "Alt + T hotkey registered.");
            return;
        }

        nextHotKeyRegistrationRetryAtUtc = DateTimeOffset.UtcNow.Add(HotKeyRegistrationRetryInterval);
        WriteAppLog("Overlay", $"Alt + T hotkey registration failed. Win32Error: {Marshal.GetLastWin32Error()}.");
    }

    private void UnregisterStratsHotKey()
    {
        if (!hotKeyRegistered || Handle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.UnregisterHotKey(Handle, StratsHotKeyId);
        hotKeyRegistered = false;
        nextHotKeyRegistrationRetryAtUtc = DateTimeOffset.MinValue;
    }

    private void PollStratsHotKeyAndRepairRegistration()
    {
        bool chordIsDown = NativeMethods.IsKeyDown(NativeMethods.VirtualKeyMenu) &&
            NativeMethods.IsKeyDown((int)NativeMethods.VirtualKeyT);
        if (chordIsDown && !hotKeyChordWasDown)
        {
            RequestStratsToggleFromHotKey("key-state fallback");
        }

        hotKeyChordWasDown = chordIsDown;
        if (!hotKeyRegistered && DateTimeOffset.UtcNow >= nextHotKeyRegistrationRetryAtUtc)
        {
            RegisterStratsHotKey();
        }
    }

    private void RequestStratsToggleFromHotKey(string triggerSource)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (nowUtc - lastHotKeyTriggerAtUtc < HotKeyTriggerCooldown)
        {
            return;
        }

        lastHotKeyTriggerAtUtc = nowUtc;
        WriteAppLog("Overlay", $"Alt + T received from {triggerSource}.");
        stratsTogglePending = true;
        stratsToggleDelayTimer.Stop();
        stratsToggleDelayTimer.Start();
    }

    private void RunPendingStratsToggleAfterHotKeyRelease()
    {
        if (!stratsTogglePending)
        {
            stratsToggleDelayTimer.Stop();
            return;
        }

        if (NativeMethods.IsKeyDown(NativeMethods.VirtualKeyMenu) ||
            NativeMethods.IsKeyDown((int)NativeMethods.VirtualKeyT))
        {
            return;
        }

        stratsTogglePending = false;
        stratsToggleDelayTimer.Stop();
        _ = ToggleStratsOverlayWhenValorantRunningAsync();
    }

    private void PreloadStratsOverlayIfNeeded()
    {
        if (stratsPreloadInProgress || stratsOverlayForm is not null)
        {
            return;
        }

        stratsPreloadInProgress = true;
        _ = PreloadStratsOverlayAsync();
    }

    private async Task PreloadStratsOverlayAsync()
    {
        try
        {
            stratsOverlayForm = new StratsOverlayForm();
            await stratsOverlayForm.PreloadAsync(GetValorantTargetBounds()).ConfigureAwait(true);
            if (!ValorantProcessMonitor.IsValorantRunning())
            {
                DisposeStratsOverlay();
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException)
        {
            WriteAppLog("Overlay", "Strats preload failed.", exception);
            DisposeStratsOverlay();
        }
        finally
        {
            stratsPreloadInProgress = false;
        }
    }

    private async Task ToggleStratsOverlayWhenValorantRunningAsync()
    {
        if (stratsToggleInProgress || !ValorantProcessMonitor.IsValorantRunning())
        {
            return;
        }

        stratsToggleInProgress = true;
        try
        {
            stratsOverlayForm ??= new StratsOverlayForm();
            if (stratsOverlayForm.IsDisposed)
            {
                stratsOverlayForm = new StratsOverlayForm();
            }

            if (stratsOverlayForm.IsOverlayVisible)
            {
                stratsOverlayForm.HideOverlayKeepingPage();
                return;
            }

            await stratsOverlayForm.BringOverlayToFrontAsync(GetValorantTargetBounds()).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException)
        {
            WriteAppLog("Overlay", "Strats overlay display failed.", exception);
        }
        finally
        {
            stratsToggleInProgress = false;
        }
    }

    private static Rectangle GetValorantTargetBounds()
    {
        return ValorantProcessMonitor.TryGetValorantWindowBounds(out Rectangle valorantBounds)
            ? valorantBounds
            : Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
    }

    private void StartGitUpdateCheckIfNeeded(bool force)
    {
        if (gitUpdateCheckInProgress || !gitUpdateSchedule.IsDue(DateTimeOffset.UtcNow, force))
        {
            return;
        }

        _ = RunGitUpdateCheckAsync();
    }

    private async Task RunGitUpdateCheckAsync()
    {
        if (gitUpdateCheckInProgress)
        {
            return;
        }

        gitUpdateCheckInProgress = true;
        try
        {
            GitUpdateCheckResult updateResult = await gitUpdateChecker
                .CheckLatestReleaseAsync(CancellationToken.None)
                .ConfigureAwait(true);

            if (!updateResult.HasUpdate)
            {
                WriteAppLog("Update", $"Update check finished. Status: {updateResult.Status}. Message: {updateResult.Message}.");
                return;
            }

            WriteAppLog(
                "Update",
                $"Update found. Current: {updateResult.CurrentVersion}. Latest: {updateResult.LatestVersion}. " +
                $"Download: {updateResult.DownloadUri}.");
            GitAutoUpdateResult autoUpdateResult = await gitAutoUpdater
                .DownloadAndStartInstallerAsync(updateResult, CancellationToken.None)
                .ConfigureAwait(true);

            if (autoUpdateResult.StartedInstaller)
            {
                WriteAppLog("Update", "Silent installer was started. Exiting current VALOWATCH process for replacement.");
                BeginInvoke((MethodInvoker)Application.Exit);
                return;
            }

            WriteAppLog(
                "Update",
                $"Auto update did not start. Status: {autoUpdateResult.Status}. Message: {autoUpdateResult.Message}.");
        }
        catch (Exception exception)
        {
            WriteAppLog("Update", "Automatic update check failed.", exception);
        }
        finally
        {
            gitUpdateSchedule.MarkCompleted(DateTimeOffset.UtcNow);
            gitUpdateCheckInProgress = false;
        }
    }

    private void DisposeStratsOverlay()
    {
        if (stratsOverlayForm is null)
        {
            return;
        }

        try
        {
            stratsOverlayForm.Close();
        }
        finally
        {
            stratsOverlayForm.Dispose();
            stratsOverlayForm = null;
        }
    }

    private void WriteAppLog(string category, string message, Exception? exception = null)
    {
        try
        {
            string logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath) ?? appPaths.DataDirectory);
            string exceptionText = exception is null ? string.Empty : $" Exception: {exception}";
            File.AppendAllText(
                logFilePath,
                $"{DateTimeOffset.Now:O} [{category}] {message}{exceptionText}{Environment.NewLine}");
        }
        catch (Exception logException) when (logException is IOException or UnauthorizedAccessException)
        {
        }
    }
}
