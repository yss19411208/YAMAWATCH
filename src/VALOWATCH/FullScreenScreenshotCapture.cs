using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VALOWATCH;

internal static class FullScreenScreenshotCapture
{
    private const long JpegQuality = 85L;
    private static readonly string[] ValorantProcessNames =
    [
        "VALORANT-Win64-Shipping",
        "VALORANT"
    ];

    public static FullScreenScreenshotResult CaptureToJpeg(string outputDirectory)
    {
        FullScreenScreenshotFrame frame = CaptureToJpegBytes(ScreenCaptureTarget.FullScreen);
        Directory.CreateDirectory(outputDirectory);
        string screenshotPath = Path.Combine(
            outputDirectory,
            $"VALOWATCH_screenshot_{DateTimeOffset.Now:yyyyMMdd_HHmmssfff}.jpg");
        File.WriteAllBytes(screenshotPath, frame.JpegBytes);

        FileInfo screenshotFile = new(screenshotPath);
        if (!screenshotFile.Exists || screenshotFile.Length == 0)
        {
            throw new IOException("Screenshot capture produced an empty file.");
        }

        return new FullScreenScreenshotResult(
            screenshotPath,
            frame.Width,
            frame.Height,
            frame.ScreenCount,
            screenshotFile.Length);
    }

    public static FullScreenScreenshotFrame CaptureToJpegBytes(ScreenCaptureTarget target)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Full-screen screenshot capture is only supported on Windows.");
        }

        CaptureRegion captureRegion = ResolveCaptureRegion(target);
        byte[] jpegBytes = CaptureRegionToJpegBytes(captureRegion.Bounds);
        if (jpegBytes.Length == 0)
        {
            throw new IOException("Screenshot capture produced an empty image buffer.");
        }

        return new FullScreenScreenshotFrame(
            jpegBytes,
            captureRegion.Bounds.Width,
            captureRegion.Bounds.Height,
            captureRegion.ScreenCount,
            captureRegion.Description,
            target);
    }

    public static void ValidateCaptureTarget(ScreenCaptureTarget target)
    {
        ResolveCaptureRegion(target);
    }

    private static byte[] CaptureRegionToJpegBytes(Rectangle bounds)
    {
        try
        {
            using Bitmap bitmap = new(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
            using Graphics graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(
                bounds.Left,
                bounds.Top,
                0,
                0,
                bounds.Size,
                CopyPixelOperation.SourceCopy);

            return SaveJpegToBytes(bitmap);
        }
        catch (ExternalException)
        {
            return CaptureWithBitBlt(bounds);
        }
    }

    private static CaptureRegion ResolveCaptureRegion(ScreenCaptureTarget target)
    {
        Rectangle virtualBounds = GetVirtualScreenBounds(out int screenCount);
        return target switch
        {
            ScreenCaptureTarget.FullScreen => new CaptureRegion(
                virtualBounds,
                screenCount,
                "full screen"),
            ScreenCaptureTarget.Valorant => ResolveValorantCaptureRegion(virtualBounds, screenCount),
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown capture target.")
        };
    }

    private static Rectangle GetVirtualScreenBounds(out int screenCount)
    {
        Screen[] screens = Screen.AllScreens;
        if (screens.Length == 0)
        {
            throw new InvalidOperationException("No display screen was reported by Windows.");
        }

        Rectangle virtualBounds = screens[0].Bounds;
        foreach (Screen screen in screens.Skip(1))
        {
            virtualBounds = Rectangle.Union(virtualBounds, screen.Bounds);
        }

        if (virtualBounds.Width <= 0 || virtualBounds.Height <= 0)
        {
            throw new InvalidOperationException($"Invalid virtual screen size: {virtualBounds.Width}x{virtualBounds.Height}.");
        }

        screenCount = screens.Length;
        return virtualBounds;
    }

    private static CaptureRegion ResolveValorantCaptureRegion(Rectangle virtualBounds, int screenCount)
    {
        if (!TryFindValorantWindow(virtualBounds, out Rectangle valorantBounds, out string processName))
        {
            throw new InvalidOperationException(
                "VALORANT window was not found. Start VALORANT and use windowed fullscreen before /stream on target:valorant.");
        }

        return new CaptureRegion(
            valorantBounds,
            screenCount,
            $"{processName} window");
    }

    private static void SaveJpeg(Image image, string screenshotPath)
    {
        File.WriteAllBytes(screenshotPath, SaveJpegToBytes(image));
    }

    private static byte[] SaveJpegToBytes(Image image)
    {
        ImageCodecInfo jpegCodec = ImageCodecInfo
            .GetImageEncoders()
            .FirstOrDefault(codec => codec.FormatID == ImageFormat.Jpeg.Guid)
            ?? throw new ExternalException("JPEG encoder is unavailable on this Windows installation.");

        using EncoderParameters encoderParameters = new(1);
        encoderParameters.Param[0] = new EncoderParameter(Encoder.Quality, JpegQuality);
        using MemoryStream imageStream = new();
        image.Save(imageStream, jpegCodec, encoderParameters);
        return imageStream.ToArray();
    }

    private static byte[] CaptureWithBitBlt(Rectangle bounds)
    {
        IntPtr screenDeviceContext = GetDC(IntPtr.Zero);
        if (screenDeviceContext == IntPtr.Zero)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "GetDC failed for the desktop.");
        }

        IntPtr memoryDeviceContext = IntPtr.Zero;
        IntPtr bitmapHandle = IntPtr.Zero;
        IntPtr previousBitmapHandle = IntPtr.Zero;
        try
        {
            memoryDeviceContext = CreateCompatibleDC(screenDeviceContext);
            if (memoryDeviceContext == IntPtr.Zero)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreateCompatibleDC failed.");
            }

            bitmapHandle = CreateCompatibleBitmap(screenDeviceContext, bounds.Width, bounds.Height);
            if (bitmapHandle == IntPtr.Zero)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreateCompatibleBitmap failed.");
            }

            previousBitmapHandle = SelectObject(memoryDeviceContext, bitmapHandle);
            if (previousBitmapHandle == IntPtr.Zero)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SelectObject failed.");
            }

            const int sourceCopyWithLayeredWindows = 0x00CC0020 | 0x40000000;
            bool copied = BitBlt(
                memoryDeviceContext,
                0,
                0,
                bounds.Width,
                bounds.Height,
                screenDeviceContext,
                bounds.Left,
                bounds.Top,
                sourceCopyWithLayeredWindows);
            if (!copied)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "BitBlt failed.");
            }

            using Bitmap capturedBitmap = Image.FromHbitmap(bitmapHandle);
            return SaveJpegToBytes(capturedBitmap);
        }
        finally
        {
            if (memoryDeviceContext != IntPtr.Zero && previousBitmapHandle != IntPtr.Zero)
            {
                SelectObject(memoryDeviceContext, previousBitmapHandle);
            }

            if (bitmapHandle != IntPtr.Zero)
            {
                DeleteObject(bitmapHandle);
            }

            if (memoryDeviceContext != IntPtr.Zero)
            {
                DeleteDC(memoryDeviceContext);
            }

            ReleaseDC(IntPtr.Zero, screenDeviceContext);
        }
    }

    private static bool TryFindValorantWindow(
        Rectangle virtualBounds,
        out Rectangle valorantBounds,
        out string processName)
    {
        ValorantWindowCandidate bestCandidate = new(Rectangle.Empty, string.Empty, 0);

        EnumWindows((windowHandle, _) =>
        {
            if (!IsWindowVisible(windowHandle) || IsIconic(windowHandle))
            {
                return true;
            }

            GetWindowThreadProcessId(windowHandle, out uint processId);
            if (processId == 0)
            {
                return true;
            }

            string candidateProcessName;
            try
            {
                using Process process = Process.GetProcessById(unchecked((int)processId));
                candidateProcessName = process.ProcessName;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return true;
            }

            if (!ValorantProcessNames.Any(name => string.Equals(name, candidateProcessName, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (!TryGetWindowBounds(windowHandle, out Rectangle windowBounds))
            {
                return true;
            }

            Rectangle clippedBounds = Rectangle.Intersect(windowBounds, virtualBounds);
            if (clippedBounds.Width < 320 || clippedBounds.Height < 240)
            {
                return true;
            }

            long area = (long)clippedBounds.Width * clippedBounds.Height;
            if (area > bestCandidate.Area)
            {
                bestCandidate = new ValorantWindowCandidate(clippedBounds, candidateProcessName, area);
            }

            return true;
        }, IntPtr.Zero);

        valorantBounds = bestCandidate.Bounds;
        processName = bestCandidate.ProcessName;
        return bestCandidate.Area > 0;
    }

    private static bool TryGetWindowBounds(IntPtr windowHandle, out Rectangle bounds)
    {
        if (TryGetDwmWindowBounds(windowHandle, out bounds))
        {
            return true;
        }

        if (!GetWindowRect(windowHandle, out NativeRect windowRect))
        {
            bounds = Rectangle.Empty;
            return false;
        }

        bounds = windowRect.ToRectangle();
        return bounds.Width > 0 && bounds.Height > 0;
    }

    private static bool TryGetDwmWindowBounds(IntPtr windowHandle, out Rectangle bounds)
    {
        try
        {
            const int extendedFrameBounds = 9;
            int result = DwmGetWindowAttribute(
                windowHandle,
                extendedFrameBounds,
                out NativeRect windowRect,
                Marshal.SizeOf<NativeRect>());
            bounds = result == 0 ? windowRect.ToRectangle() : Rectangle.Empty;
            return result == 0 && bounds.Width > 0 && bounds.Height > 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            bounds = Rectangle.Empty;
            return false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr windowHandle, IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr deviceContext, int width, int height);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr gdiObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(
        IntPtr destinationDeviceContext,
        int destinationX,
        int destinationY,
        int width,
        int height,
        IntPtr sourceDeviceContext,
        int sourceX,
        int sourceY,
        int rasterOperation);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr gdiObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr deviceContext);

    private delegate bool EnumWindowsProc(IntPtr windowHandle, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rectangle);

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmGetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        out NativeRect rectangle,
        int attributeSize);

    private readonly record struct CaptureRegion(
        Rectangle Bounds,
        int ScreenCount,
        string Description);

    private readonly record struct ValorantWindowCandidate(
        Rectangle Bounds,
        string ProcessName,
        long Area);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly Rectangle ToRectangle()
        {
            return Rectangle.FromLTRB(Left, Top, Right, Bottom);
        }
    }
}

internal enum ScreenCaptureTarget
{
    FullScreen,
    Valorant
}

internal static class ScreenCaptureTargetNames
{
    public const string FullScreen = "full";
    public const string Valorant = "valorant";

    public static bool TryParse(string text, out ScreenCaptureTarget target)
    {
        if (string.Equals(text, FullScreen, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "fullscreen", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "all", StringComparison.OrdinalIgnoreCase))
        {
            target = ScreenCaptureTarget.FullScreen;
            return true;
        }

        if (string.Equals(text, Valorant, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "valo", StringComparison.OrdinalIgnoreCase))
        {
            target = ScreenCaptureTarget.Valorant;
            return true;
        }

        target = ScreenCaptureTarget.FullScreen;
        return false;
    }

    public static string ToOptionValue(ScreenCaptureTarget target)
    {
        return target == ScreenCaptureTarget.Valorant ? Valorant : FullScreen;
    }
}

internal sealed record FullScreenScreenshotResult(
    string FilePath,
    int Width,
    int Height,
    int ScreenCount,
    long FileBytes);

internal sealed record FullScreenScreenshotFrame(
    byte[] JpegBytes,
    int Width,
    int Height,
    int ScreenCount,
    string Description,
    ScreenCaptureTarget Target);
