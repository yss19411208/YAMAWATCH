using System.Runtime.InteropServices;

namespace VALOWATCH;

internal static class NativeMethods
{
    public const int WmHotKey = 0x0312;
    public const int WmApp = 0x8000;
    public const int WmInput = 0x00FF;
    public const int WmKeyDown = 0x0100;
    public const int WmKeyUp = 0x0101;
    public const int WmSysKeyDown = 0x0104;
    public const int WmSysKeyUp = 0x0105;
    public const int WhKeyboardLl = 13;
    public const uint LlkhfAltDown = 0x20;
    public const uint RidInput = 0x10000003;
    public const uint RimTypeKeyboard = 1;
    public const uint RidevRemove = 0x00000001;
    public const uint RidevInputSink = 0x00000100;
    public const ushort HidUsagePageGeneric = 0x01;
    public const ushort HidUsageGenericKeyboard = 0x06;
    public const ushort RawKeyboardBreak = 0x0001;
    public const uint ModAlt = 0x0001;
    public const uint ModNoRepeat = 0x4000;
    public const uint VirtualKeyT = 0x54;
    public const int SwShow = 5;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpShowWindow = 0x0040;
    public const int WsExToolWindow = 0x00000080;
    public const int WsExTopMost = 0x00000008;
    public const int VirtualKeyMenu = 0x12;
    public const int VirtualKeyLeftMenu = 0xA4;
    public const int VirtualKeyRightMenu = 0xA5;
    public const uint GaRoot = 2;

    public const int WmNclButtonDown = 0x00A1;
    public const int HtCaption = 0x0002;

    public static readonly IntPtr HwndTopMost = new(-1);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr windowHandle, int id);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterRawInputDevices(
        [In] RawInputDevice[] rawInputDevices,
        uint deviceCount,
        uint structureSize);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetRawInputData(
        IntPtr rawInputHandle,
        uint command,
        IntPtr data,
        ref uint dataSize,
        uint headerSize);

    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr windowHandle, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr windowHandle, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ShowWindow(IntPtr windowHandle, int commandShow);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint attachThreadId, uint attachToThreadId, bool attach);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr windowHandle);

    [DllImport("user32.dll")]
    public static extern IntPtr SetFocus(IntPtr windowHandle);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfterWindowHandle,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int virtualKeyCode);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(
        int hookType,
        LowLevelKeyboardProc hookProcedure,
        IntPtr moduleHandle,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(
        IntPtr hookHandle,
        int hookCode,
        IntPtr message,
        IntPtr keyboardData);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    public static bool IsKeyDown(int virtualKeyCode)
    {
        return (GetAsyncKeyState(virtualKeyCode) & 0x8000) != 0;
    }
}

internal delegate IntPtr LowLevelKeyboardProc(int hookCode, IntPtr message, IntPtr keyboardData);

[StructLayout(LayoutKind.Sequential)]
internal struct RawInputDevice
{
    public ushort UsagePage;
    public ushort Usage;
    public uint Flags;
    public IntPtr TargetWindow;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RawInputHeader
{
    public uint Type;
    public uint Size;
    public IntPtr Device;
    public IntPtr WParam;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RawKeyboardInput
{
    public ushort MakeCode;
    public ushort Flags;
    public ushort Reserved;
    public ushort VirtualKey;
    public uint Message;
    public uint ExtraInformation;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LowLevelKeyboardInput
{
    public uint VirtualKey;
    public uint ScanCode;
    public uint Flags;
    public uint Time;
    public nuint ExtraInformation;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public Rectangle ToRectangle()
    {
        return Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }
}
