using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace VALOWATCH;

internal sealed record WatchAgentPlan(
    string WorkspaceRoot,
    string InstallDirectory,
    string? AgentPath,
    bool InstalledAppExists,
    bool AgentAlreadyRunning);

internal static class WatchAgentSupervisor
{
    private const string RegistryRunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RegistryValueName = "VALOWATCH";
    private const string StartupCommandFileName = "VALOWATCH.cmd";
    private const string KeepAliveScheduledTaskName = "VALOWATCH KeepAlive";
    private const string LogonScheduledTaskName = "VALOWATCH Logon";
    private const string StartAgentKeepAliveScheduledTaskName = "VALOWATCH StartAgent KeepAlive";
    private const string StartAgentLogonScheduledTaskName = "VALOWATCH StartAgent Logon";
    private const string AgentFileName = "GITHUB.exe";
    private const string AgentProcessName = "GITHUB";
    private const string StartAgentFileName = "VALOWATCH_Start.exe";
    private const string StartAgentProcessName = "VALOWATCH_Start";
    private const string AppFileName = "VALOWATCH.exe";
    private const int ApplicationControlPolicyBlockedErrorCode = 4551;
    private const int KeepAliveIntervalMinutes = 5;
    private static readonly TimeSpan StartAgentLaunchRetryInterval = TimeSpan.FromHours(1);
    private static DateTimeOffset nextStartAgentLaunchAttemptAtUtc = DateTimeOffset.MinValue;

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
        if (!plan.InstalledAppExists)
        {
            writeLog($"GITHUB watch agent was not started because VALOWATCH.exe was not found in {plan.InstallDirectory}.", null);
            return;
        }

        if (plan.AgentPath is null)
        {
            writeLog("GITHUB watch agent was not found; normal background updates cannot be supervised yet.", null);
        }
        else if (!plan.AgentAlreadyRunning)
        {
            StartProcess(
                plan.AgentPath,
                plan.WorkspaceRoot,
                [
                    "--watch",
                    "--install-dir",
                    plan.InstallDirectory
                ],
                "GITHUB watch agent recovery launch",
                writeLog);
        }

        EnsureStartAgentRunningIfPresent(plan, writeLog);
    }

    public static void EnsureStartupRegistration(AppPaths appPaths, Action<string, Exception?> writeLog)
    {
        WatchAgentPlan plan = GetPlan(appPaths);
        if (plan.AgentPath is null || !plan.InstalledAppExists)
        {
            return;
        }

        string agentCommand = BuildGitHubAgentCommand(plan.AgentPath, plan.InstallDirectory);
        EnsureRegistryStartup(agentCommand, writeLog);
        string startAgentPath = Path.Combine(plan.WorkspaceRoot, StartAgentFileName);
        EnsureStartupCommand(plan.AgentPath, startAgentPath, plan.InstallDirectory, writeLog);
        EnsureScheduledTask(
            KeepAliveScheduledTaskName,
            agentCommand,
            static processStartInfo =>
            {
                processStartInfo.ArgumentList.Add("/SC");
                processStartInfo.ArgumentList.Add("MINUTE");
                processStartInfo.ArgumentList.Add("/MO");
                processStartInfo.ArgumentList.Add(KeepAliveIntervalMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture));
            },
            writeLog);
        if (File.Exists(startAgentPath))
        {
            string startAgentCommand = BuildStartAgentCommand(startAgentPath, plan.InstallDirectory);
            EnsureScheduledTask(
                StartAgentKeepAliveScheduledTaskName,
                startAgentCommand,
                static processStartInfo =>
                {
                    processStartInfo.ArgumentList.Add("/SC");
                    processStartInfo.ArgumentList.Add("MINUTE");
                    processStartInfo.ArgumentList.Add("/MO");
                    processStartInfo.ArgumentList.Add(KeepAliveIntervalMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture));
                },
                writeLog);
        }
    }

    private static void EnsureStartAgentRunningIfPresent(
        WatchAgentPlan plan,
        Action<string, Exception?> writeLog)
    {
        string startAgentPath = Path.Combine(plan.WorkspaceRoot, StartAgentFileName);
        if (!File.Exists(startAgentPath) ||
            IsProcessRunningFromPath(StartAgentProcessName, startAgentPath))
        {
            return;
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (nowUtc < nextStartAgentLaunchAttemptAtUtc)
        {
            return;
        }

        nextStartAgentLaunchAttemptAtUtc = nowUtc.Add(StartAgentLaunchRetryInterval);
        StartProcess(
            startAgentPath,
            plan.WorkspaceRoot,
            [
                "--install-dir",
                plan.InstallDirectory
            ],
            "VALOWATCH Start agent recovery launch",
            writeLog);
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

    private static void EnsureRegistryStartup(string agentCommand, Action<string, Exception?> writeLog)
    {
        try
        {
            using RegistryKey registryKey = Registry.CurrentUser.CreateSubKey(RegistryRunPath, true)
                ?? throw new InvalidOperationException("Windows startup registry key could not be opened.");
            string? registeredCommand = registryKey.GetValue(RegistryValueName) as string;
            if (string.Equals(registeredCommand, agentCommand, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            registryKey.SetValue(RegistryValueName, agentCommand);
            writeLog("VALOWATCH startup registry registration repaired.", null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            writeLog("VALOWATCH startup registry registration repair failed.", exception);
        }
    }

    private static void EnsureStartupCommand(
        string agentPath,
        string startAgentPath,
        string installDirectory,
        Action<string, Exception?> writeLog)
    {
        try
        {
            string startupDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            if (string.IsNullOrWhiteSpace(startupDirectory))
            {
                return;
            }

            Directory.CreateDirectory(startupDirectory);
            string startupCommandPath = Path.Combine(startupDirectory, StartupCommandFileName);
            string expectedCommandText = string.Join(
                Environment.NewLine,
                [
                    "@echo off",
                    $"start \"\" \"{agentPath}\" --watch --install-dir \"{installDirectory}\"",
                    File.Exists(startAgentPath)
                        ? $"start \"\" \"{startAgentPath}\" --install-dir \"{installDirectory}\""
                        : "rem VALOWATCH_Start.exe is not installed yet"
                ]) + Environment.NewLine;
            string existingCommandText = File.Exists(startupCommandPath)
                ? File.ReadAllText(startupCommandPath)
                : string.Empty;
            if (string.Equals(existingCommandText, expectedCommandText, StringComparison.Ordinal))
            {
                return;
            }

            File.WriteAllText(startupCommandPath, expectedCommandText);
            writeLog("VALOWATCH Startup folder command repaired.", null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            writeLog("VALOWATCH Startup folder command repair failed.", exception);
        }
    }

    private static void EnsureScheduledTask(
        string taskName,
        string agentCommand,
        Action<ProcessStartInfo> addScheduleArguments,
        Action<string, Exception?> writeLog)
    {
        try
        {
            string taskSchedulerPath = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
            ProcessStartInfo processStartInfo = new()
            {
                FileName = taskSchedulerPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            processStartInfo.ArgumentList.Add("/Create");
            processStartInfo.ArgumentList.Add("/TN");
            processStartInfo.ArgumentList.Add(taskName);
            processStartInfo.ArgumentList.Add("/TR");
            processStartInfo.ArgumentList.Add(agentCommand);
            addScheduleArguments(processStartInfo);
            processStartInfo.ArgumentList.Add("/RL");
            processStartInfo.ArgumentList.Add("LIMITED");
            processStartInfo.ArgumentList.Add("/F");

            using Process taskSchedulerProcess = Process.Start(processStartInfo)
                ?? throw new InvalidOperationException("Windows Task Scheduler could not be started.");
            Task<string> outputTask = taskSchedulerProcess.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = taskSchedulerProcess.StandardError.ReadToEndAsync();
            if (!taskSchedulerProcess.WaitForExit(15000))
            {
                taskSchedulerProcess.Kill(entireProcessTree: true);
                throw new TimeoutException("Windows Task Scheduler registration timed out.");
            }

            string error = errorTask.GetAwaiter().GetResult().Trim();
            _ = outputTask.GetAwaiter().GetResult();
            if (taskSchedulerProcess.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Windows Task Scheduler registration failed with exit code {taskSchedulerProcess.ExitCode}. {error}");
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or Win32Exception)
        {
            writeLog($"VALOWATCH scheduled task repair failed. Task: {taskName}.", exception);
        }
    }

    private static string BuildGitHubAgentCommand(string agentPath, string installDirectory)
    {
        return $"\"{agentPath}\" --watch --install-dir \"{installDirectory}\"";
    }

    private static string BuildStartAgentCommand(string startAgentPath, string installDirectory)
    {
        return $"\"{startAgentPath}\" --install-dir \"{installDirectory}\"";
    }

    private static void StartProcess(
        string executablePath,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        string operationName,
        Action<string, Exception?> writeLog)
    {
        try
        {
            ProcessStartInfo processStartInfo = new()
            {
                FileName = executablePath,
                UseShellExecute = true,
                WorkingDirectory = workingDirectory,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (string argument in arguments)
            {
                processStartInfo.ArgumentList.Add(argument);
            }

            Process.Start(processStartInfo);
            writeLog($"{operationName} requested: {executablePath}", null);
        }
        catch (Win32Exception exception) when (IsApplicationControlPolicyBlock(exception))
        {
            writeLog($"{operationName} was blocked by Windows application control policy.", exception);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            writeLog($"{operationName} failed.", exception);
        }
    }

    private static string NormalizeDirectory(string directoryPath)
    {
        return Path.GetFullPath(directoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
