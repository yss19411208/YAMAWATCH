using System.Diagnostics;

namespace VALOWATCH;

internal static class LineProcessMonitor
{
    private static readonly string[] LineProcessNames = ["LINE", "Line", "line"];

    public static bool IsLineRunning()
    {
        foreach (string processName in LineProcessNames)
        {
            Process[] matchingProcesses;
            try
            {
                matchingProcesses = Process.GetProcessesByName(processName);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            try
            {
                if (matchingProcesses.Any(process => !process.HasExited))
                {
                    return true;
                }
            }
            finally
            {
                foreach (Process process in matchingProcesses)
                {
                    process.Dispose();
                }
            }
        }

        return false;
    }
}
