using System.Runtime.InteropServices;

namespace VALOWATCH;

public sealed class MainForm : Form
{
    private const int StratsHotKeyId = 9101;
    private const int WmAltTHookPressed = NativeMethods.WmApp + 0x51;
    private const int WmAltTKeyStatePressed = NativeMethods.WmApp + 0x52;
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
    private readonly bool disableKeyStateFallback;
    private readonly System.Windows.Forms.Timer processTimer = new();
    private readonly System.Windows.Forms.Timer hotKeyHealthTimer = new();
    private readonly System.Windows.Forms.Timer stratsToggleDelayTimer = new();
    private readonly AltTHotKeyStateMachine rawInputHotKeyState = new();
    private AltTHotKeyStateMachine lowLevelHookHotKeyState = new();
    private readonly LowLevelKeyboardProc lowLevelKeyboardHookProcedure;

    private StratsOverlayForm? stratsOverlayForm;
    private AsyncKeyStateAltTHotKeyMonitor? asyncKeyStateHotKeyMonitor;
    private IntPtr lowLevelKeyboardHookHandle;
    private IntPtr hotKeyMessageTargetHandle;
    private bool rawKeyboardInputRegistered;
    private bool hotKeyRegistered;
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
    private DateTimeOffset nextKeyStateMonitorHealthLogAtUtc = DateTimeOffset.MinValue;

    public MainForm(
        AppPaths appPaths,
        DiscordBotVoiceRelay discordBotVoiceRelay,
        GitUpdateChecker gitUpdateChecker,
        GitAutoUpdater gitAutoUpdater,
        bool disableDiscordAutomation,
        bool disableKeyStateFallback = false)
    {
        this.appPaths = appPaths;
        this.discordBotVoiceRelay = discordBotVoiceRelay;
        this.gitUpdateChecker = gitUpdateChecker;
        this.gitAutoUpdater = gitAutoUpdater;
        this.disableDiscordAutomation = disableDiscordAutomation;
        this.disableKeyStateFallback = disableKeyStateFallback;
        lowLevelKeyboardHookProcedure = ProcessLowLevelKeyboardInput;
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
        hotKeyMessageTargetHandle = Handle;
        RegisterStratsHotKey();
        RegisterRawKeyboardInput();
        RegisterLowLevelKeyboardHook();
        StartAsyncKeyStateHotKeyMonitor();
    }

    protected override void OnHandleDestroyed(EventArgs eventArgs)
    {
        StopAsyncKeyStateHotKeyMonitor();
        UnregisterLowLevelKeyboardHook();
        UnregisterRawKeyboardInput();
        UnregisterStratsHotKey();
        hotKeyMessageTargetHandle = IntPtr.Zero;
        base.OnHandleDestroyed(eventArgs);
    }

    protected override void OnFormClosing(FormClosingEventArgs eventArgs)
    {
        UnregisterStratsHotKey();
        processTimer.Stop();
        hotKeyHealthTimer.Stop();
        stratsToggleDelayTimer.Stop();
        StopAsyncKeyStateHotKeyMonitor();
        UnregisterLowLevelKeyboardHook();
        UnregisterRawKeyboardInput();
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

        if (message.Msg == NativeMethods.WmInput)
        {
            ProcessRawKeyboardInput(message.LParam);
        }

        if (message.Msg == WmAltTHookPressed)
        {
            RequestStratsToggleFromHotKey("low-level keyboard hook");
            return;
        }

        if (message.Msg == WmAltTKeyStatePressed)
        {
            RequestStratsToggleFromHotKey("dedicated key-state fallback");
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
        if (!hotKeyRegistered && DateTimeOffset.UtcNow >= nextHotKeyRegistrationRetryAtUtc)
        {
            RegisterStratsHotKey();
        }

        CheckAsyncKeyStateMonitorHealth();
    }

    private void CheckAsyncKeyStateMonitorHealth()
    {
        if (disableKeyStateFallback || DateTimeOffset.UtcNow < nextKeyStateMonitorHealthLogAtUtc)
        {
            return;
        }

        nextKeyStateMonitorHealthLogAtUtc = DateTimeOffset.UtcNow.AddSeconds(5);
        AsyncKeyStateAltTHotKeyMonitor? monitor = asyncKeyStateHotKeyMonitor;
        if (monitor is null)
        {
            WriteAppLog("Overlay", "Dedicated key-state monitor was missing and will be restarted.");
            StartAsyncKeyStateHotKeyMonitor();
            return;
        }

        WriteAppLog(
            "Overlay",
            $"Dedicated key-state monitor health. Responsive: {monitor.IsResponsive}. " +
            $"Heartbeat: {monitor.HeartbeatCount}. DetectedChords: {monitor.DetectedChordCount}.");
        if (monitor.IsResponsive)
        {
            return;
        }

        WriteAppLog("Overlay", "Dedicated key-state monitor stalled and will be restarted.");
        StopAsyncKeyStateHotKeyMonitor();
        StartAsyncKeyStateHotKeyMonitor();
    }

    private void StartAsyncKeyStateHotKeyMonitor()
    {
        if (disableKeyStateFallback || asyncKeyStateHotKeyMonitor is not null)
        {
            return;
        }

        AsyncKeyStateAltTHotKeyMonitor monitor = new();
        monitor.AltTPressed += OnAsyncKeyStateAltTPressed;
        monitor.Start();
        asyncKeyStateHotKeyMonitor = monitor;
        nextKeyStateMonitorHealthLogAtUtc = DateTimeOffset.UtcNow.AddSeconds(5);
        WriteAppLog("Overlay", "Dedicated Alt + T key-state fallback started.");
    }

    private void StopAsyncKeyStateHotKeyMonitor()
    {
        AsyncKeyStateAltTHotKeyMonitor? monitor = asyncKeyStateHotKeyMonitor;
        asyncKeyStateHotKeyMonitor = null;
        if (monitor is null)
        {
            return;
        }

        monitor.AltTPressed -= OnAsyncKeyStateAltTPressed;
        monitor.Dispose();
    }

    private void OnAsyncKeyStateAltTPressed()
    {
        IntPtr targetHandle = hotKeyMessageTargetHandle;
        if (targetHandle != IntPtr.Zero)
        {
            NativeMethods.PostMessage(targetHandle, WmAltTKeyStatePressed, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private void RegisterRawKeyboardInput()
    {
        if (rawKeyboardInputRegistered || Handle == IntPtr.Zero)
        {
            return;
        }

        RawInputDevice[] devices =
        [
            new RawInputDevice
            {
                UsagePage = NativeMethods.HidUsagePageGeneric,
                Usage = NativeMethods.HidUsageGenericKeyboard,
                Flags = NativeMethods.RidevInputSink,
                TargetWindow = Handle
            }
        ];
        rawKeyboardInputRegistered = NativeMethods.RegisterRawInputDevices(
            devices,
            (uint)devices.Length,
            (uint)Marshal.SizeOf<RawInputDevice>());
        if (rawKeyboardInputRegistered)
        {
            WriteAppLog("Overlay", "Background Raw Input keyboard registered.");
            return;
        }

        WriteAppLog(
            "Overlay",
            $"Background Raw Input keyboard registration failed. Win32Error: {Marshal.GetLastWin32Error()}.");
    }

    private void RegisterLowLevelKeyboardHook()
    {
        if (lowLevelKeyboardHookHandle != IntPtr.Zero || Handle == IntPtr.Zero)
        {
            return;
        }

        IntPtr moduleHandle = NativeMethods.GetModuleHandle(null);
        lowLevelHookHotKeyState = new AltTHotKeyStateMachine();
        lowLevelKeyboardHookHandle = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhKeyboardLl,
            lowLevelKeyboardHookProcedure,
            moduleHandle,
            threadId: 0);
        if (lowLevelKeyboardHookHandle != IntPtr.Zero)
        {
            WriteAppLog("Overlay", "Low-level Alt + T keyboard hook registered.");

            return;
        }

        WriteAppLog(
            "Overlay",
            $"Low-level Alt + T keyboard hook registration failed. Win32Error: {Marshal.GetLastWin32Error()}.");
    }

    private void UnregisterLowLevelKeyboardHook()
    {
        IntPtr hookHandle = lowLevelKeyboardHookHandle;
        lowLevelKeyboardHookHandle = IntPtr.Zero;
        if (hookHandle != IntPtr.Zero && !NativeMethods.UnhookWindowsHookEx(hookHandle))
        {
            WriteAppLog(
                "Overlay",
                $"Low-level Alt + T keyboard hook removal failed. Win32Error: {Marshal.GetLastWin32Error()}.");
        }
    }

    private IntPtr ProcessLowLevelKeyboardInput(int hookCode, IntPtr message, IntPtr keyboardData)
    {
        IntPtr activeHookHandle = lowLevelKeyboardHookHandle;
        if (hookCode >= 0 && keyboardData != IntPtr.Zero)
        {
            int keyboardMessage = unchecked((int)message.ToInt64());
            bool keyDown = keyboardMessage is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown;
            bool keyUp = keyboardMessage is NativeMethods.WmKeyUp or NativeMethods.WmSysKeyUp;
            if (keyDown || keyUp)
            {
                LowLevelKeyboardInput keyboardInput = Marshal.PtrToStructure<LowLevelKeyboardInput>(keyboardData);
                bool altIsCurrentlyDown = (keyboardInput.Flags & NativeMethods.LlkhfAltDown) != 0;
                if (lowLevelHookHotKeyState.Process(
                    keyboardInput.VirtualKey,
                    keyDown,
                    keyUp,
                    altIsCurrentlyDown))
                {
                    IntPtr targetHandle = hotKeyMessageTargetHandle;
                    if (targetHandle != IntPtr.Zero)
                    {
                        NativeMethods.PostMessage(targetHandle, WmAltTHookPressed, IntPtr.Zero, IntPtr.Zero);
                    }
                }
            }
        }

        return NativeMethods.CallNextHookEx(activeHookHandle, hookCode, message, keyboardData);
    }

    private void UnregisterRawKeyboardInput()
    {
        if (!rawKeyboardInputRegistered)
        {
            return;
        }

        RawInputDevice[] devices =
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
            devices,
            (uint)devices.Length,
            (uint)Marshal.SizeOf<RawInputDevice>());
        rawKeyboardInputRegistered = false;
    }

    private void ProcessRawKeyboardInput(IntPtr rawInputHandle)
    {
        uint headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        uint dataSize = 0;
        uint queryResult = NativeMethods.GetRawInputData(
            rawInputHandle,
            NativeMethods.RidInput,
            IntPtr.Zero,
            ref dataSize,
            headerSize);
        if (queryResult != 0 || dataSize < headerSize + Marshal.SizeOf<RawKeyboardInput>())
        {
            return;
        }

        IntPtr inputBuffer = Marshal.AllocHGlobal(checked((int)dataSize));
        try
        {
            uint readSize = dataSize;
            uint bytesRead = NativeMethods.GetRawInputData(
                rawInputHandle,
                NativeMethods.RidInput,
                inputBuffer,
                ref readSize,
                headerSize);
            if (bytesRead != dataSize)
            {
                return;
            }

            RawInputHeader inputHeader = Marshal.PtrToStructure<RawInputHeader>(inputBuffer);
            if (inputHeader.Type != NativeMethods.RimTypeKeyboard)
            {
                return;
            }

            IntPtr keyboardPointer = IntPtr.Add(inputBuffer, Marshal.SizeOf<RawInputHeader>());
            RawKeyboardInput keyboardInput = Marshal.PtrToStructure<RawKeyboardInput>(keyboardPointer);
            bool keyUp = (keyboardInput.Flags & NativeMethods.RawKeyboardBreak) != 0 ||
                keyboardInput.Message is NativeMethods.WmKeyUp or NativeMethods.WmSysKeyUp;
            bool keyDown = !keyUp &&
                keyboardInput.Message is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown;
            bool altIsCurrentlyDown = NativeMethods.IsKeyDown(NativeMethods.VirtualKeyMenu);
            if (rawInputHotKeyState.Process(keyboardInput.VirtualKey, keyDown, keyUp, altIsCurrentlyDown))
            {
                RequestStratsToggleFromHotKey("background Raw Input");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inputBuffer);
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
        bool valorantRunning = lastValorantDetected || ValorantProcessMonitor.IsValorantRunning();
        if (stratsToggleInProgress || !valorantRunning)
        {
            if (!valorantRunning)
            {
                WriteAppLog("Overlay", "Alt + T toggle ignored because no VALORANT process was detected.");
            }

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

            (IntPtr valorantWindowHandle, Rectangle targetBounds) = GetValorantTargetWindow();
            await stratsOverlayForm
                .BringOverlayToFrontAsync(targetBounds, valorantWindowHandle)
                .ConfigureAwait(true);
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

    private static (IntPtr WindowHandle, Rectangle WindowBounds) GetValorantTargetWindow()
    {
        if (ValorantProcessMonitor.TryGetValorantWindow(out IntPtr windowHandle, out Rectangle windowBounds))
        {
            return (windowHandle, windowBounds);
        }

        Rectangle fallbackBounds = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        return (IntPtr.Zero, fallbackBounds);
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
