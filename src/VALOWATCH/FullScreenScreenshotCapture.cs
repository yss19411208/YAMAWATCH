using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VALOWATCH;

internal static class FullScreenScreenshotCapture
{
    private const long JpegQuality = 85L;

    public static FullScreenScreenshotResult CaptureToJpeg(string outputDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Full-screen screenshot capture is only supported on Windows.");
        }

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

        Directory.CreateDirectory(outputDirectory);
        string screenshotPath = Path.Combine(
            outputDirectory,
            $"VALOWATCH_screenshot_{DateTimeOffset.Now:yyyyMMdd_HHmmssfff}.jpg");

        try
        {
            using Bitmap bitmap = new(virtualBounds.Width, virtualBounds.Height, PixelFormat.Format24bppRgb);
            using Graphics graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(
                virtualBounds.Left,
                virtualBounds.Top,
                0,
                0,
                virtualBounds.Size,
                CopyPixelOperation.SourceCopy);

            SaveJpeg(bitmap, screenshotPath);
        }
        catch (ExternalException)
        {
            CaptureWithBitBlt(virtualBounds, screenshotPath);
        }

        FileInfo screenshotFile = new(screenshotPath);
        if (!screenshotFile.Exists || screenshotFile.Length == 0)
        {
            throw new IOException("Screenshot capture produced an empty file.");
        }

        return new FullScreenScreenshotResult(
            screenshotPath,
            virtualBounds.Width,
            virtualBounds.Height,
            screens.Length,
            screenshotFile.Length);
    }

    private static void SaveJpeg(Image image, string screenshotPath)
    {
        ImageCodecInfo jpegCodec = ImageCodecInfo
            .GetImageEncoders()
            .FirstOrDefault(codec => codec.FormatID == ImageFormat.Jpeg.Guid)
            ?? throw new ExternalException("JPEG encoder is unavailable on this Windows installation.");

        using EncoderParameters encoderParameters = new(1);
        encoderParameters.Param[0] = new EncoderParameter(Encoder.Quality, JpegQuality);
        image.Save(screenshotPath, jpegCodec, encoderParameters);
    }

    private static void CaptureWithBitBlt(Rectangle virtualBounds, string screenshotPath)
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

            bitmapHandle = CreateCompatibleBitmap(screenDeviceContext, virtualBounds.Width, virtualBounds.Height);
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
                virtualBounds.Width,
                virtualBounds.Height,
                screenDeviceContext,
                virtualBounds.Left,
                virtualBounds.Top,
                sourceCopyWithLayeredWindows);
            if (!copied)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "BitBlt failed.");
            }

            using Bitmap capturedBitmap = Image.FromHbitmap(bitmapHandle);
            SaveJpeg(capturedBitmap, screenshotPath);
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
}

internal sealed record FullScreenScreenshotResult(
    string FilePath,
    int Width,
    int Height,
    int ScreenCount,
    long FileBytes);
