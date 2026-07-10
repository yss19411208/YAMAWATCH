using System.Diagnostics;

namespace VALOWATCH;

public static class ValorantProcessMonitor
{
    private static readonly string[] ValorantProcessNames =
    [
        "VALORANT-Win64-Shipping",
        "VALORANT"
    ];

    public static bool IsValorantRunning()
    {
        foreach (string processName in ValorantProcessNames)
        {
            try
            {
                Process[] matchingProcesses = Process.GetProcessesByName(processName);
                if (matchingProcesses.Length > 0)
                {
                    foreach (Process matchingProcess in matchingProcesses)
                    {
                        matchingProcess.Dispose();
                    }

                    return true;
                }

                foreach (Process matchingProcess in matchingProcesses)
                {
                    matchingProcess.Dispose();
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }

        return false;
    }

    public static bool TryGetValorantWindowBounds(out Rectangle windowBounds)
    {
        foreach (string processName in ValorantProcessNames)
        {
            Process[] matchingProcesses = [];

            try
            {
                matchingProcesses = Process.GetProcessesByName(processName);
                foreach (Process matchingProcess in matchingProcesses)
                {
                    if (matchingProcess.MainWindowHandle == IntPtr.Zero)
                    {
                        continue;
                    }

                    if (NativeMethods.GetWindowRect(matchingProcess.MainWindowHandle, out NativeRect nativeRect))
                    {
                        Rectangle rectangle = nativeRect.ToRectangle();
                        if (rectangle.Width > 0 && rectangle.Height > 0)
                        {
                            windowBounds = rectangle;
                            return true;
                        }
                    }
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                foreach (Process matchingProcess in matchingProcesses)
                {
                    matchingProcess.Dispose();
                }
            }
        }

        windowBounds = Rectangle.Empty;
        return false;
    }
}
