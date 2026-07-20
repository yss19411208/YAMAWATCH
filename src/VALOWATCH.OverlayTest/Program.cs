using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VALOWATCH.OverlayTest;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Any(argument => string.Equals(argument, "--motion-source", StringComparison.OrdinalIgnoreCase)))
        {
            int durationSeconds = ReadIntegerOption(args, "--duration-seconds", defaultValue: 90, minimumValue: 5, maximumValue: 900);
            ApplicationConfiguration.Initialize();
            using MotionSourceForm motionSourceForm = new(TimeSpan.FromSeconds(durationSeconds));
            Application.Run(motionSourceForm);
            Environment.ExitCode = 0;
            return;
        }

        string appPath = args.Length > 0
            ? Path.GetFullPath(args[0])
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "exe", "VALOWATCH.exe"));
        string[] appArguments = args.Skip(1).ToArray();
        bool blockHotKeyRegistration = appArguments.Any(argument =>
            string.Equals(argument, "--block-hotkey", StringComparison.OrdinalIgnoreCase));
        bool longRunningHotKeyTest = appArguments.Any(argument =>
            string.Equals(argument, "--long-running-hotkey-test", StringComparison.OrdinalIgnoreCase));
        bool keepHotKeyBlocked = appArguments.Any(argument =>
            string.Equals(argument, "--keep-hotkey-blocked", StringComparison.OrdinalIgnoreCase));
        appArguments = appArguments.Where(argument =>
            !string.Equals(argument, "--block-hotkey", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(argument, "--long-running-hotkey-test", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(argument, "--keep-hotkey-blocked", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (!appArguments.Any(argument => string.Equals(argument, "--no-discord", StringComparison.OrdinalIgnoreCase)))
        {
            appArguments = [.. appArguments, "--no-discord"];
        }

        ApplicationConfiguration.Initialize();
        using FakeValorantForm fakeValorantForm = new(
            appPath,
            appArguments,
            blockHotKeyRegistration,
            longRunningHotKeyTest,
            keepHotKeyBlocked);
        Application.Run(fakeValorantForm);
        Environment.ExitCode = fakeValorantForm.TestPassed ? 0 : 1;
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
}

internal sealed class FakeValorantForm : Form
{
    private readonly string appPath;
    private readonly string appArguments;
    private readonly string resultPath;
    private readonly bool blockHotKeyRegistration;
    private readonly bool longRunningHotKeyTest;
    private readonly bool keepHotKeyBlocked;
    private readonly Label statusLabel = new();

    private Process? valowatchProcess;

    public FakeValorantForm(
        string appPath,
        IEnumerable<string> appArguments,
        bool blockHotKeyRegistration,
        bool longRunningHotKeyTest,
        bool keepHotKeyBlocked)
    {
        this.appPath = appPath;
        this.appArguments = string.Join(' ', appArguments.Select(argument => argument.Contains(' ') ? $"\"{argument}\"" : argument));
        this.blockHotKeyRegistration = blockHotKeyRegistration;
        this.longRunningHotKeyTest = longRunningHotKeyTest;
        this.keepHotKeyBlocked = keepHotKeyBlocked;
        resultPath = Path.Combine(AppContext.BaseDirectory, "overlay-test-result.txt");
        BuildInterface();
    }

    public bool TestPassed { get; private set; }

    protected override async void OnShown(EventArgs eventArgs)
    {
        base.OnShown(eventArgs);
        await RunTestAsync();
    }

    private void BuildInterface()
    {
        Text = "Pseudo VALORANT Fullscreen";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
        BackColor = Color.FromArgb(8, 10, 14);
        ForeColor = Color.White;
        TopMost = true;
        ShowInTaskbar = true;

        statusLabel.Dock = DockStyle.Fill;
        statusLabel.TextAlign = ContentAlignment.MiddleCenter;
        statusLabel.Font = new Font("Segoe UI", 24F, FontStyle.Bold);
        statusLabel.Text = "Pseudo Fullscreen VALORANT Test";
        Controls.Add(statusLabel);
    }

    private async Task RunTestAsync()
    {
        List<string> resultLines = [];

        try
        {
            if (!File.Exists(appPath))
            {
                throw new FileNotFoundException("VALOWATCH.exe was not found.", appPath);
            }

            bool blockerRegistered = !blockHotKeyRegistration || NativeMethods.RegisterTestHotKey(Handle);
            if (!blockerRegistered)
            {
                throw new InvalidOperationException("The Alt+T conflict test hotkey could not be registered.");
            }

            statusLabel.Text = "Launching VALOWATCH...";
            valowatchProcess = Process.Start(new ProcessStartInfo
            {
                FileName = appPath,
                Arguments = appArguments,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(appPath)
            });

            await Task.Delay(2200);

            NativeMethods.SetForegroundWindow(Handle);
            await Task.Delay(300);

            statusLabel.Text = "Sending Alt + T...";
            NativeMethods.AltTInjectionResult firstInjection = await NativeMethods.SendAltTAsync();
            await Task.Delay(3500);

            Point centerPoint = new(Left + Width / 2, Top + Height / 2);
            IntPtr windowAtCenter = NativeMethods.WindowFromPoint(centerPoint);
            IntPtr rootWindow = NativeMethods.GetAncestor(windowAtCenter, NativeMethods.GaRoot);
            _ = NativeMethods.GetWindowThreadProcessId(rootWindow, out int processId);

            string processName = GetProcessName(processId);
            string windowTitle = NativeMethods.GetWindowText(rootWindow);
            bool fakeStillVisible = Visible && WindowState != FormWindowState.Minimized;
            bool overlayOnTop = string.Equals(processName, "VALOWATCH", StringComparison.OrdinalIgnoreCase)
                || processName.StartsWith("VALOWATCH_", StringComparison.OrdinalIgnoreCase)
                || windowTitle.Contains("VALOWATCH", StringComparison.OrdinalIgnoreCase);
            IntPtr foregroundWindow = NativeMethods.GetForegroundWindow();
            IntPtr foregroundRootWindow = NativeMethods.GetAncestor(foregroundWindow, NativeMethods.GaRoot);
            _ = NativeMethods.GetWindowThreadProcessId(foregroundRootWindow, out int foregroundProcessId);
            string foregroundProcessName = GetProcessName(foregroundProcessId);
            bool overlayHasInputFocus = foregroundRootWindow == rootWindow &&
                (foregroundProcessName.StartsWith("VALOWATCH", StringComparison.OrdinalIgnoreCase) ||
                    windowTitle.Contains("VALOWATCH", StringComparison.OrdinalIgnoreCase));

            bool hotKeyRegistrationRecovered = true;
            if (blockHotKeyRegistration && !keepHotKeyBlocked)
            {
                NativeMethods.UnregisterTestHotKey(Handle);
                await Task.Delay(5600);
                hotKeyRegistrationRecovered = !NativeMethods.RegisterTestHotKey(Handle);
                if (!hotKeyRegistrationRecovered)
                {
                    NativeMethods.UnregisterTestHotKey(Handle);
                }
            }

            if (longRunningHotKeyTest)
            {
                statusLabel.Text = "Keeping the overlay active for 30 seconds...";
                await Task.Delay(TimeSpan.FromSeconds(30));
            }

            statusLabel.Text = "Sending Alt + T again...";
            NativeMethods.AltTInjectionResult secondInjection = await NativeMethods.SendAltTAsync();
            await Task.Delay(1200);

            IntPtr hiddenWindowAtCenter = NativeMethods.WindowFromPoint(centerPoint);
            IntPtr hiddenRootWindow = NativeMethods.GetAncestor(hiddenWindowAtCenter, NativeMethods.GaRoot);
            _ = NativeMethods.GetWindowThreadProcessId(hiddenRootWindow, out int hiddenProcessId);
            string hiddenTopProcessName = GetProcessName(hiddenProcessId);
            bool overlayHidden = IsValorantProcessName(hiddenTopProcessName);
            IntPtr restoredForegroundWindow = NativeMethods.GetForegroundWindow();
            _ = NativeMethods.GetWindowThreadProcessId(restoredForegroundWindow, out int restoredForegroundProcessId);
            string restoredForegroundProcessName = GetProcessName(restoredForegroundProcessId);
            bool valorantFocusRestored = IsValorantProcessName(restoredForegroundProcessName);

            statusLabel.Text = "Showing retained overlay again...";
            NativeMethods.AltTInjectionResult thirdInjection = await NativeMethods.SendAltTAsync();
            await Task.Delay(2200);

            IntPtr retainedWindowAtCenter = NativeMethods.WindowFromPoint(centerPoint);
            IntPtr retainedRootWindow = NativeMethods.GetAncestor(retainedWindowAtCenter, NativeMethods.GaRoot);
            _ = NativeMethods.GetWindowThreadProcessId(retainedRootWindow, out int retainedProcessId);
            string retainedProcessName = GetProcessName(retainedProcessId);
            bool retainedOverlayVisible = retainedRootWindow == rootWindow &&
                retainedProcessName.StartsWith("VALOWATCH", StringComparison.OrdinalIgnoreCase);
            IntPtr retainedForegroundRootWindow = NativeMethods.GetAncestor(
                NativeMethods.GetForegroundWindow(),
                NativeMethods.GaRoot);
            bool retainedOverlayHasInputFocus = retainedForegroundRootWindow == retainedRootWindow;

            statusLabel.Text = "Hiding retained overlay...";
            NativeMethods.AltTInjectionResult fourthInjection = await NativeMethods.SendAltTAsync();
            await Task.Delay(1200);

            IntPtr finalWindowAtCenter = NativeMethods.WindowFromPoint(centerPoint);
            IntPtr finalRootWindow = NativeMethods.GetAncestor(finalWindowAtCenter, NativeMethods.GaRoot);
            _ = NativeMethods.GetWindowThreadProcessId(finalRootWindow, out int finalProcessId);
            string finalProcessName = GetProcessName(finalProcessId);
            bool finalOverlayHidden = IsValorantProcessName(finalProcessName);

            TestPassed = fakeStillVisible &&
                overlayOnTop &&
                overlayHasInputFocus &&
                overlayHidden &&
                valorantFocusRestored &&
                retainedOverlayVisible &&
                retainedOverlayHasInputFocus &&
                finalOverlayHidden &&
                hotKeyRegistrationRecovered;

            resultLines.Add($"AppPath={appPath}");
            resultLines.Add($"AppArguments={appArguments}");
            resultLines.Add($"TopWindowProcess={processName}");
            resultLines.Add($"TopWindowTitle={windowTitle}");
            resultLines.Add($"FakeStillVisible={fakeStillVisible}");
            resultLines.Add($"OverlayOnTop={overlayOnTop}");
            resultLines.Add($"OverlayHasInputFocus={overlayHasInputFocus}");
            resultLines.Add($"ForegroundProcess={foregroundProcessName}");
            resultLines.Add($"BlockHotKeyRegistration={blockHotKeyRegistration}");
            resultLines.Add($"LongRunningHotKeyTest={longRunningHotKeyTest}");
            resultLines.Add($"KeepHotKeyBlocked={keepHotKeyBlocked}");
            resultLines.Add($"KeyStateFallbackDisabled={appArguments.Contains("--disable-key-state-fallback", StringComparison.OrdinalIgnoreCase)}");
            resultLines.Add($"HotKeyRegistrationRecovered={hotKeyRegistrationRecovered}");
            resultLines.Add($"OverlayHiddenAfterSecondToggle={overlayHidden}");
            resultLines.Add($"ValorantFocusRestored={valorantFocusRestored}");
            resultLines.Add($"RetainedOverlaySameWindow={retainedOverlayVisible}");
            resultLines.Add($"RetainedOverlayHasInputFocus={retainedOverlayHasInputFocus}");
            resultLines.Add($"OverlayHiddenAfterFinalToggle={finalOverlayHidden}");
            resultLines.Add($"FirstInjection={firstInjection}");
            resultLines.Add($"SecondInjection={secondInjection}");
            resultLines.Add($"ThirdInjection={thirdInjection}");
            resultLines.Add($"FourthInjection={fourthInjection}");
            resultLines.Add($"TestPassed={TestPassed}");

            statusLabel.Text = TestPassed
                ? "PASS: VALOWATCH is above pseudo fullscreen"
                : "FAIL: VALOWATCH is not above pseudo fullscreen";

            await Task.Delay(1200);
        }
        catch (Exception exception)
        {
            TestPassed = false;
            resultLines.Add($"TestPassed=False");
            resultLines.Add($"Error={exception}");
            statusLabel.Text = "FAIL: " + exception.Message;
            await Task.Delay(1200);
        }
        finally
        {
            NativeMethods.UnregisterTestHotKey(Handle);
            File.WriteAllLines(resultPath, resultLines);
            StopValowatchProcess();
            Close();
        }
    }

    private void StopValowatchProcess()
    {
        if (valowatchProcess is null)
        {
            return;
        }

        try
        {
            if (valowatchProcess.HasExited)
            {
                return;
            }

            valowatchProcess.CloseMainWindow();
            if (!valowatchProcess.WaitForExit(1500))
            {
                valowatchProcess.Kill(entireProcessTree: true);
                valowatchProcess.WaitForExit(3000);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static string GetProcessName(int processId)
    {
        if (processId <= 0)
        {
            return "(unknown)";
        }

        using Process process = Process.GetProcessById(processId);
        return process.ProcessName;
    }

    private static bool IsValorantProcessName(string processName)
    {
        return processName.Equals("VALORANT", StringComparison.OrdinalIgnoreCase) ||
            processName.StartsWith("VALORANT-", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class MotionSourceForm : Form
{
    private readonly TimeSpan duration;
    private readonly Stopwatch stopwatch = new();
    private readonly System.Windows.Forms.Timer frameTimer = new()
    {
        Interval = 16
    };
    private readonly System.Windows.Forms.Timer closeTimer = new()
    {
        Interval = 500
    };

    public MotionSourceForm(TimeSpan duration)
    {
        this.duration = duration;
        BuildInterface();
        frameTimer.Tick += (_, _) => Invalidate();
        closeTimer.Tick += (_, _) =>
        {
            if (stopwatch.Elapsed >= this.duration)
            {
                Close();
            }
        };
    }

    protected override void OnShown(EventArgs eventArgs)
    {
        base.OnShown(eventArgs);
        stopwatch.Restart();
        frameTimer.Start();
        closeTimer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            frameTimer.Dispose();
            closeTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void BuildInterface()
    {
        Text = "VALOWATCH 60fps Motion Source";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
        BackColor = Color.FromArgb(8, 10, 14);
        ForeColor = Color.White;
        TopMost = true;
        ShowInTaskbar = true;
        DoubleBuffered = true;
    }

    protected override void OnPaint(PaintEventArgs paintEventArgs)
    {
        base.OnPaint(paintEventArgs);
        Graphics graphics = paintEventArgs.Graphics;
        graphics.Clear(Color.FromArgb(8, 10, 14));
        double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
        int frameNumber = (int)Math.Floor(elapsedSeconds * 60D);
        int formWidth = Math.Max(1, ClientSize.Width);
        int formHeight = Math.Max(1, ClientSize.Height);

        using Pen gridPen = new(Color.FromArgb(36, 255, 255, 255));
        for (int x = 0; x < formWidth; x += 48)
        {
            graphics.DrawLine(gridPen, x, 0, x, formHeight);
        }

        for (int y = 0; y < formHeight; y += 48)
        {
            graphics.DrawLine(gridPen, 0, y, formWidth, y);
        }

        using SolidBrush accentBrush = new(ColorFromHue(frameNumber % 360, 230, 190));
        using SolidBrush whiteBrush = new(Color.FromArgb(235, 255, 255, 255));
        int movingWidth = Math.Max(120, formWidth / 5);
        int movingX = (int)((elapsedSeconds * 520D) % (formWidth + movingWidth)) - movingWidth;
        graphics.FillRectangle(accentBrush, movingX, formHeight / 2 - 50, movingWidth, 100);

        int orbitRadius = Math.Max(80, Math.Min(formWidth, formHeight) / 5);
        int orbitCenterX = formWidth - orbitRadius - 120;
        int orbitCenterY = formHeight / 2;
        int orbitX = orbitCenterX + (int)(Math.Cos(elapsedSeconds * Math.PI * 2D) * orbitRadius);
        int orbitY = orbitCenterY + (int)(Math.Sin(elapsedSeconds * Math.PI * 2D) * orbitRadius);
        graphics.FillEllipse(whiteBrush, orbitX - 28, orbitY - 28, 56, 56);

        using Font titleFont = new("Segoe UI", 34F, FontStyle.Bold);
        using Font infoFont = new("Consolas", 22F, FontStyle.Regular);
        graphics.DrawString("VALOWATCH 60fps Motion Test", titleFont, Brushes.White, 56, 52);
        graphics.DrawString($"frame={frameNumber:000000}", infoFont, Brushes.White, 64, 132);
        graphics.DrawString($"time={DateTimeOffset.Now:HH:mm:ss.fff}", infoFont, Brushes.White, 64, 172);
        graphics.DrawString("scroll + orbit + color shift", infoFont, Brushes.White, 64, 212);

        int scrollY = formHeight - 120;
        for (int index = 0; index < 30; index++)
        {
            int x = (int)(((index * 112D) - elapsedSeconds * 260D) % (formWidth + 112));
            if (x < -100)
            {
                x += formWidth + 112;
            }

            graphics.FillRectangle(index % 2 == 0 ? accentBrush : whiteBrush, x, scrollY, 82, 38);
        }
    }

    private static Color ColorFromHue(int hueDegrees, int saturation, int value)
    {
        double hue = Math.Clamp(hueDegrees, 0, 359) / 60D;
        double chroma = Math.Clamp(value, 0, 255) / 255D * Math.Clamp(saturation, 0, 255) / 255D;
        double intermediate = chroma * (1D - Math.Abs((hue % 2D) - 1D));
        double match = Math.Clamp(value, 0, 255) / 255D - chroma;
        (double red, double green, double blue) = hue switch
        {
            < 1D => (chroma, intermediate, 0D),
            < 2D => (intermediate, chroma, 0D),
            < 3D => (0D, chroma, intermediate),
            < 4D => (0D, intermediate, chroma),
            < 5D => (intermediate, 0D, chroma),
            _ => (chroma, 0D, intermediate)
        };

        return Color.FromArgb(
            (int)Math.Round((red + match) * 255D),
            (int)Math.Round((green + match) * 255D),
            (int)Math.Round((blue + match) * 255D));
    }
}

internal static class NativeMethods
{
    public const uint GaRoot = 2;
    private const uint InputKeyboard = 1;
    private const ushort VirtualKeyMenu = 0x12;
    private const ushort VirtualKeyT = 0x54;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint ModAlt = 0x0001;
    private const uint ModNoRepeat = 0x4000;
    private const int TestHotKeyId = 8127;

    public readonly record struct AltTInjectionResult(bool AltWasDown, bool TWasDown);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out int processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr windowHandle, char[] textBuffer, int maxCount);

    public static string GetWindowText(IntPtr windowHandle)
    {
        char[] textBuffer = new char[512];
        int length = GetWindowText(windowHandle, textBuffer, textBuffer.Length);
        return length <= 0 ? string.Empty : new string(textBuffer, 0, length);
    }

    public static bool RegisterTestHotKey(IntPtr windowHandle)
    {
        return RegisterHotKey(windowHandle, TestHotKeyId, ModAlt | ModNoRepeat, VirtualKeyT);
    }

    public static void UnregisterTestHotKey(IntPtr windowHandle)
    {
        UnregisterHotKey(windowHandle, TestHotKeyId);
    }

    public static async Task<AltTInjectionResult> SendAltTAsync()
    {
        Input[] keyDownInputs =
        [
            CreateKeyboardInput(VirtualKeyMenu, keyUp: false),
            CreateKeyboardInput(VirtualKeyT, keyUp: false)
        ];
        Input[] keyUpInputs =
        [
            CreateKeyboardInput(VirtualKeyT, keyUp: true),
            CreateKeyboardInput(VirtualKeyMenu, keyUp: true)
        ];

        uint keyDownCount = SendInput((uint)keyDownInputs.Length, keyDownInputs, Marshal.SizeOf<Input>());
        if (keyDownCount != keyDownInputs.Length)
        {
            throw new InvalidOperationException("SendInput did not send the Alt+T key-down sequence.");
        }

        await Task.Delay(180);

        AltTInjectionResult result = new(
            (GetAsyncKeyState(VirtualKeyMenu) & 0x8000) != 0,
            (GetAsyncKeyState(VirtualKeyT) & 0x8000) != 0);

        uint keyUpCount = SendInput((uint)keyUpInputs.Length, keyUpInputs, Marshal.SizeOf<Input>());
        if (keyUpCount != keyUpInputs.Length)
        {
            throw new InvalidOperationException("SendInput did not send the Alt+T key-up sequence.");
        }

        return result;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKeyCode);

    private static Input CreateKeyboardInput(ushort virtualKeyCode, bool keyUp)
    {
        return new Input
        {
            Type = InputKeyboard,
            Union = new InputUnion
            {
                KeyboardInput = new KeyboardInput
                {
                    VirtualKey = virtualKeyCode,
                    ScanCode = 0,
                    Flags = keyUp ? KeyEventKeyUp : 0,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput MouseInput;

        [FieldOffset(0)]
        public KeyboardInput KeyboardInput;

        [FieldOffset(0)]
        public HardwareInput HardwareInput;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Message;
        public ushort ParamLow;
        public ushort ParamHigh;
    }
}
