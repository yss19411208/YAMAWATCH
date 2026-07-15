using System.ComponentModel;
using System.Diagnostics;

namespace VALOWATCH;

internal sealed record WatchAgentPlan(
    string WorkspaceRoot,
    string InstallDirectory,
    string? AgentPath,
    bool InstalledAppExists,
    bool AgentAlreadyRunning);

internal static class WatchAgentSupervisor
{
    private const string AgentFileName = "GITHUB.exe";
    private const string AgentProcessName = "GITHUB";
    private const string AppFileName = "VALOWATCH.exe";
    private const int ApplicationControlPolicyBlockedErrorCode = 4551;

    public static WatchAgentPlan GetPlan(AppPaths appPaths)
    {
        string currentAppDirectory = NormalizeDirectory(AppContext.BaseDirectory);
        string workspaceRoot = ResolveWorkspaceRoot(appPaths, currentAppDirectory);
        string installDirectory = ResolveInstallDirectory(workspaceRoot, currentAppDirectory);
        string? agentPath = ResolveAgentPath(workspaceRoot, installDirectory, currentAppDirectory);
        bool installedAppExists = File.Exists(Path.Combine(installDirectory, AppFileName));
        bool agentAlreadyRunning = agentPath is not null && IsProcessRunningFromPath(AgentProcessName, agentPath);

        return new WatchAgentPlan(
            workspaceRoot,
            installDirectory,
            agentPath,
            installedAppExists,
            agentAlreadyRunning);
    }

    public static void EnsureRunning(AppPaths appPaths, Action<string, Exception?> writeLog)
    {
        WatchAgentPlan plan = GetPlan(appPaths);
        if (plan.AgentPath is null)
        {
            writeLog("GITHUB watch agent was not found; normal background updates cannot be supervised yet.", null);
            return;
        }

        if (!plan.InstalledAppExists)
        {
            writeLog($"GITHUB watch agent was not started because VALOWATCH.exe was not found in {plan.InstallDirectory}.", null);
            return;
        }

        if (plan.AgentAlreadyRunning)
        {
            return;
        }

        try
        {
            ProcessStartInfo processStartInfo = new()
            {
                FileName = plan.AgentPath,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(plan.AgentPath),
                WindowStyle = ProcessWindowStyle.Hidden
            };
            processStartInfo.ArgumentList.Add("--watch");
            processStartInfo.ArgumentList.Add("--install-dir");
            processStartInfo.ArgumentList.Add(plan.InstallDirectory);

            Process.Start(processStartInfo);
            writeLog($"GITHUB watch agent recovery launch requested: {plan.AgentPath}", null);
        }
        catch (Win32Exception exception) when (IsApplicationControlPolicyBlock(exception))
        {
            writeLog(
                "GITHUB watch agent recovery launch was blocked by Windows application control policy.",
                exception);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            writeLog("GITHUB watch agent recovery launch failed.", exception);
        }
    }

    private static string ResolveWorkspaceRoot(AppPaths appPaths, string currentAppDirectory)
    {
        string dataDirectory = NormalizeDirectory(appPaths.DataDirectory);
        string? dataParentDirectory = Directory.GetParent(dataDirectory)?.FullName;
        if (!string.IsNullOrWhiteSpace(dataParentDirectory) && LooksLikeWorkspaceRoot(dataParentDirectory))
        {
            return NormalizeDirectory(dataParentDirectory);
        }

        DirectoryInfo? currentDirectory = new(currentAppDirectory);
        while (currentDirectory is not null)
        {
            if (LooksLikeWorkspaceRoot(currentDirectory.FullName))
            {
                return NormalizeDirectory(currentDirectory.FullName);
            }

            currentDirectory = currentDirectory.Parent;
        }

        return NormalizeDirectory(Directory.GetParent(currentAppDirectory)?.FullName ?? currentAppDirectory);
    }

    private static string ResolveInstallDirectory(string workspaceRoot, string currentAppDirectory)
    {
        if (IsSourceRepositoryRoot(workspaceRoot))
        {
            string isolatedInstallDirectory = Path.Combine(workspaceRoot, "data", "installed", "VALOWATCH", "app");
            if (File.Exists(Path.Combine(isolatedInstallDirectory, AppFileName)))
            {
                return NormalizeDirectory(isolatedInstallDirectory);
            }
        }

        if (File.Exists(Path.Combine(currentAppDirectory, AppFileName)) &&
            Path.GetFileName(currentAppDirectory).Equals("app", StringComparison.OrdinalIgnoreCase))
        {
            return currentAppDirectory;
        }

        string standardInstallDirectory = Path.Combine(workspaceRoot, "app");
        if (File.Exists(Path.Combine(standardInstallDirectory, AppFileName)))
        {
            return NormalizeDirectory(standardInstallDirectory);
        }

        return currentAppDirectory;
    }

    private static string? ResolveAgentPath(string workspaceRoot, string installDirectory, string currentAppDirectory)
    {
        string? installParentDirectory = Directory.GetParent(installDirectory)?.FullName;
        IEnumerable<string?> candidatePaths =
        [
            string.IsNullOrWhiteSpace(installParentDirectory) ? null : Path.Combine(installParentDirectory, AgentFileName),
            Path.Combine(workspaceRoot, AgentFileName),
            Path.Combine(workspaceRoot, "github", AgentFileName),
            Path.Combine(currentAppDirectory, AgentFileName)
        ];

        return candidatePaths
            .Where(static candidatePath => !string.IsNullOrWhiteSpace(candidatePath))
            .Select(static candidatePath => Path.GetFullPath(candidatePath!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists);
    }

    private static bool LooksLikeWorkspaceRoot(string directoryPath)
    {
        return File.Exists(Path.Combine(directoryPath, "VALOWATCH.slnx")) ||
            Directory.Exists(Path.Combine(directoryPath, "src")) ||
            File.Exists(Path.Combine(directoryPath, AgentFileName)) ||
            Directory.Exists(Path.Combine(directoryPath, "installer"));
    }

    private static bool IsSourceRepositoryRoot(string workspaceRoot)
    {
        return File.Exists(Path.Combine(workspaceRoot, "VALOWATCH.slnx")) ||
            Directory.Exists(Path.Combine(workspaceRoot, "src"));
    }

    private static bool IsProcessRunningFromPath(string processName, string expectedPath)
    {
        string normalizedExpectedPath = Path.GetFullPath(expectedPath);
        foreach (Process process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    string? processPath = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(processPath) &&
                        Path.GetFullPath(processPath).Equals(normalizedExpectedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or UnauthorizedAccessException)
                {
                }
            }
        }

        return false;
    }

    private static bool IsApplicationControlPolicyBlock(Win32Exception exception)
    {
        return exception.NativeErrorCode == ApplicationControlPolicyBlockedErrorCode ||
            exception.Message.Contains("application control policy", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("アプリケーション制御ポリシー", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDirectory(string directoryPath)
    {
        return Path.GetFullPath(directoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
