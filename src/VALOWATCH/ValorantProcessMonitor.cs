using System.Diagnostics;

namespace VALOWATCH;

public static class ValorantProcessMonitor
{
    private static readonly string[] ExactValorantProcessNames =
    [
        "VALORANT-Win64-Shipping",
        "VALORANT"
    ];

    public static bool IsValorantRunning()
    {
        Process[] processes = Process.GetProcesses();
        try
        {
            return processes.Any(IsValorantProcess);
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }

    public static bool TryGetValorantWindow(out IntPtr windowHandle, out Rectangle windowBounds)
    {
        Process[] processes = Process.GetProcesses();
        try
        {
            IntPtr largestWindowHandle = IntPtr.Zero;
            Rectangle largestWindowBounds = Rectangle.Empty;
            long largestWindowArea = 0;

            foreach (Process process in processes)
            {
                if (!IsValorantProcess(process))
                {
                    continue;
                }

                try
                {
                    IntPtr candidateHandle = process.MainWindowHandle;
                    if (candidateHandle == IntPtr.Zero ||
                        !NativeMethods.GetWindowRect(candidateHandle, out NativeRect nativeRect))
                    {
                        continue;
                    }

                    Rectangle candidateBounds = nativeRect.ToRectangle();
                    long candidateArea = (long)candidateBounds.Width * candidateBounds.Height;
                    if (candidateBounds.Width <= 0 || candidateBounds.Height <= 0 || candidateArea <= largestWindowArea)
                    {
                        continue;
                    }

                    largestWindowHandle = candidateHandle;
                    largestWindowBounds = candidateBounds;
                    largestWindowArea = candidateArea;
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }

            windowHandle = largestWindowHandle;
            windowBounds = largestWindowBounds;
            return windowHandle != IntPtr.Zero;
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }

    public static bool TryGetValorantWindowBounds(out Rectangle windowBounds)
    {
        return TryGetValorantWindow(out _, out windowBounds);
    }

    private static bool IsValorantProcess(Process process)
    {
        try
        {
            string processName = process.ProcessName;
            return ExactValorantProcessNames.Any(name =>
                    string.Equals(processName, name, StringComparison.OrdinalIgnoreCase)) ||
                processName.StartsWith("VALORANT-", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
