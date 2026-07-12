using System.Runtime.InteropServices;

namespace VALOWATCH;

internal static class NativeMethods
{
    public const int WmHotKey = 0x0312;
    public const uint ModAlt = 0x0001;
    public const uint ModNoRepeat = 0x4000;
    public const uint VirtualKeyT = 0x54;
    public const int SwShownoactivate = 4;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpShowWindow = 0x0040;
    public const int WsExToolWindow = 0x00000080;
    public const int WsExTopMost = 0x00000008;
    public const int WsExNoActivate = 0x08000000;
    public const int VirtualKeyMenu = 0x12;

    public const int WmNclButtonDown = 0x00A1;
    public const int HtCaption = 0x0002;

    public static readonly IntPtr HwndTopMost = new(-1);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr windowHandle, int id);

    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr windowHandle, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ShowWindow(IntPtr windowHandle, int commandShow);

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

    public static bool IsKeyDown(int virtualKeyCode)
    {
        return (GetAsyncKeyState(virtualKeyCode) & 0x8000) != 0;
    }
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
