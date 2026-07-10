using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VALOWATCH.OverlayTest;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        string appPath = args.Length > 0
            ? Path.GetFullPath(args[0])
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "exe", "VALOWATCH.exe"));
        string[] appArguments = args.Skip(1).ToArray();
        if (!appArguments.Any(argument => string.Equals(argument, "--no-discord", StringComparison.OrdinalIgnoreCase)))
        {
            appArguments = [.. appArguments, "--no-discord"];
        }

        ApplicationConfiguration.Initialize();
        using FakeValorantForm fakeValorantForm = new(appPath, appArguments);
        Application.Run(fakeValorantForm);
        Environment.ExitCode = fakeValorantForm.TestPassed ? 0 : 1;
    }
}

internal sealed class FakeValorantForm : Form
{
    private readonly string appPath;
    private readonly string appArguments;
    private readonly string resultPath;
    private readonly Label statusLabel = new();

    private Process? valowatchProcess;

    public FakeValorantForm(string appPath, IEnumerable<string> appArguments)
    {
        this.appPath = appPath;
        this.appArguments = string.Join(' ', appArguments.Select(argument => argument.Contains(' ') ? $"\"{argument}\"" : argument));
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
            NativeMethods.SendAltT();
            await Task.Delay(3500);

            Point centerPoint = new(Left + Width / 2, Top + Height / 2);
            IntPtr windowAtCenter = NativeMethods.WindowFromPoint(centerPoint);
            IntPtr rootWindow = NativeMethods.GetAncestor(windowAtCenter, NativeMethods.GaRoot);
            _ = NativeMethods.GetWindowThreadProcessId(rootWindow, out int processId);

            string processName = processId > 0
                ? Process.GetProcessById(processId).ProcessName
                : "(unknown)";
            string windowTitle = NativeMethods.GetWindowText(rootWindow);
            bool fakeStillVisible = Visible && WindowState != FormWindowState.Minimized;
            bool overlayOnTop = string.Equals(processName, "VALOWATCH", StringComparison.OrdinalIgnoreCase)
                || windowTitle.Contains("VALOWATCH", StringComparison.OrdinalIgnoreCase);

            TestPassed = fakeStillVisible && overlayOnTop;

            resultLines.Add($"AppPath={appPath}");
            resultLines.Add($"AppArguments={appArguments}");
            resultLines.Add($"TopWindowProcess={processName}");
            resultLines.Add($"TopWindowTitle={windowTitle}");
            resultLines.Add($"FakeStillVisible={fakeStillVisible}");
            resultLines.Add($"OverlayOnTop={overlayOnTop}");
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
}

internal static class NativeMethods
{
    public const uint GaRoot = 2;
    private const uint InputKeyboard = 1;
    private const ushort VirtualKeyMenu = 0x12;
    private const ushort VirtualKeyT = 0x54;
    private const uint KeyEventKeyUp = 0x0002;

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out int processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr windowHandle, char[] textBuffer, int maxCount);

    public static string GetWindowText(IntPtr windowHandle)
    {
        char[] textBuffer = new char[512];
        int length = GetWindowText(windowHandle, textBuffer, textBuffer.Length);
        return length <= 0 ? string.Empty : new string(textBuffer, 0, length);
    }

    public static void SendAltT()
    {
        Input[] inputs =
        [
            CreateKeyboardInput(VirtualKeyMenu, keyUp: false),
            CreateKeyboardInput(VirtualKeyT, keyUp: false),
            CreateKeyboardInput(VirtualKeyT, keyUp: true),
            CreateKeyboardInput(VirtualKeyMenu, keyUp: true)
        ];

        uint sentInputCount = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sentInputCount != inputs.Length)
        {
            throw new InvalidOperationException("SendInput did not send the complete Alt+T sequence.");
        }
    }

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
