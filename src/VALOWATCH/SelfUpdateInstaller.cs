using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace VALOWATCH;

internal static class SelfUpdateInstaller
{
    private const string UpdateOption = "--update";
    private const string SilentOption = "--silent";
    private const string InstallDirectoryOption = "--install-dir";
    private static readonly (string ResourceName, string FileName)[] NativeResources =
    [
        ("UpdateNative/libdave.dll", "libdave.dll"),
        ("UpdateNative/libsodium.dll", "libsodium.dll"),
        ("UpdateNative/opus.dll", "opus.dll")
    ];
    private enum AgentInstallLocation
    {
        WorkspaceRoot,
        InstallDirectory
    }

    private static readonly (string ResourceName, string FileName, string ProcessName, AgentInstallLocation Location)[] AgentResources =
    [
        ("UpdateAgent/GITHUB.exe", "GITHUB.exe", "GITHUB", AgentInstallLocation.WorkspaceRoot),
        ("UpdateAgent/VALOWATCH_Start.exe", "VALOWATCH_Start.exe", "VALOWATCH_Start", AgentInstallLocation.WorkspaceRoot)
    ];

    public static bool IsUpdateInvocation(IReadOnlyList<string> args)
    {
        return args.Any(argument => string.Equals(argument, UpdateOption, StringComparison.OrdinalIgnoreCase)) &&
            args.Any(argument => string.Equals(argument, SilentOption, StringComparison.OrdinalIgnoreCase));
    }

    public static int Run(IReadOnlyList<string> args)
    {
        ReplacementResult? replacementResult = null;
        try
        {
            string installDirectory = ParseInstallDirectory(args);
            ValidateInstallDirectory(installDirectory);
            string targetExecutablePath = Path.Combine(installDirectory, "VALOWATCH.exe");

            Directory.CreateDirectory(installDirectory);
            StopInstalledApp(targetExecutablePath);
            replacementResult = ReplaceExecutable(targetExecutablePath);
            ExtractNativeResources(installDirectory);
            RepairEmbeddedAgentResources(installDirectory);
            RemoveObsoleteCaptureTools(installDirectory);
            WriteUpdateCompletedMarker(installDirectory);
            StartInstalledApp(targetExecutablePath);
            RemoveBackup(replacementResult);
            WriteLog($"Self update completed. Target: {targetExecutablePath}");
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            WriteLog("Self update failed.", exception);
            RestoreBackup(replacementResult);
            return 1;
        }
    }

    internal static void RepairEmbeddedAgentResourcesForInstalledApp(
        string installDirectory,
        Action<string, Exception?> writeLog)
    {
        try
        {
            ValidateInstallDirectory(installDirectory);
            RepairEmbeddedAgentResources(installDirectory);
            writeLog("Embedded update agents were checked for the installed app.", null);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            writeLog("Embedded update agents could not be checked for the installed app.", exception);
        }
    }

    private static string ParseInstallDirectory(IReadOnlyList<string> args)
    {
        string optionPrefix = InstallDirectoryOption + "=";
        for (int argumentIndex = 0; argumentIndex < args.Count; argumentIndex++)
        {
            string argument = args[argumentIndex];
            if (argument.StartsWith(optionPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(argument[optionPrefix.Length..].Trim().Trim('"'));
            }

            if (string.Equals(argument, InstallDirectoryOption, StringComparison.OrdinalIgnoreCase) &&
                argumentIndex + 1 < args.Count)
            {
                return Path.GetFullPath(args[argumentIndex + 1].Trim().Trim('"'));
            }
        }

        throw new ArgumentException("Self update install directory is missing.");
    }

    private static void ValidateInstallDirectory(string installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            throw new ArgumentException("Self update install directory is empty.", nameof(installDirectory));
        }

        string pathRoot = Path.GetPathRoot(installDirectory) ?? string.Empty;
        if (string.Equals(installDirectory.TrimEnd(Path.DirectorySeparatorChar), pathRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Self update cannot target a drive root.");
        }
    }

    private static void StopInstalledApp(string targetExecutablePath)
    {
        string normalizedTargetPath = Path.GetFullPath(targetExecutablePath);
        foreach (Process candidateProcess in Process.GetProcessesByName("VALOWATCH"))
        {
            using (candidateProcess)
            {
                if (candidateProcess.Id == Environment.ProcessId || !ProcessMatchesPath(candidateProcess, normalizedTargetPath))
                {
                    continue;
                }

                try
                {
                    candidateProcess.CloseMainWindow();
                    if (!candidateProcess.WaitForExit(3000))
                    {
                        candidateProcess.Kill(entireProcessTree: true);
                        candidateProcess.WaitForExit(5000);
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    WriteLog($"Could not stop installed process {candidateProcess.Id}.", exception);
                }
            }
        }
    }

    private static bool ProcessMatchesPath(Process process, string targetExecutablePath)
    {
        try
        {
            string? processPath = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(processPath) &&
                string.Equals(Path.GetFullPath(processPath), targetExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private static ReplacementResult ReplaceExecutable(string targetExecutablePath)
    {
        string sourceExecutablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Self update source executable path is unavailable.");
        string temporaryPath = targetExecutablePath + $".{Environment.ProcessId}.new";
        string backupPath = targetExecutablePath + ".previous";
        string? activeBackupPath = null;
        File.Copy(sourceExecutablePath, temporaryPath, overwrite: true);
        ValidateExecutableFile(temporaryPath, "temporary update executable");

        Exception? lastReplacementException = null;
        try
        {
            for (int replacementAttempt = 1; replacementAttempt <= 20; replacementAttempt++)
            {
                StopInstalledApp(targetExecutablePath);
                try
                {
                    if (File.Exists(targetExecutablePath))
                    {
                        File.Copy(targetExecutablePath, backupPath, overwrite: true);
                        activeBackupPath = backupPath;
                    }

                    File.Move(temporaryPath, targetExecutablePath, overwrite: true);
                    ValidateExecutableFile(targetExecutablePath, "installed update executable");
                    return new ReplacementResult(targetExecutablePath, activeBackupPath);
                }
                catch (Exception exception) when (
                    (exception is IOException or UnauthorizedAccessException) &&
                    replacementAttempt < 20)
                {
                    lastReplacementException = exception;
                    Thread.Sleep(TimeSpan.FromMilliseconds(500));
                }
            }

            throw new IOException(
                "VALOWATCH.exe remained locked after 20 replacement attempts.",
                lastReplacementException);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    WriteLog($"Temporary update file could not be removed: {temporaryPath}", exception);
                }
            }
        }
    }

    private static void ExtractNativeResources(string installDirectory)
    {
        foreach ((string resourceName, string fileName) in NativeResources)
        {
            using Stream? resourceStream = typeof(SelfUpdateInstaller).Assembly.GetManifestResourceStream(resourceName);
            if (resourceStream is null)
            {
                throw new InvalidOperationException($"Embedded update resource is missing: {resourceName}");
            }

            string targetPath = Path.Combine(installDirectory, fileName);
            string temporaryPath = targetPath + $".{Environment.ProcessId}.new";
            using (FileStream targetStream = new(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                resourceStream.CopyTo(targetStream);
            }

            File.Move(temporaryPath, targetPath, overwrite: true);
        }
    }

    private static void RepairEmbeddedAgentResources(string installDirectory)
    {
        string workspaceRoot = ResolveWorkspaceRootForInstallDirectory(installDirectory);
        foreach ((string resourceName, string fileName, string processName, AgentInstallLocation location) in AgentResources)
        {
            TryExtractEmbeddedAgentResource(
                resourceName,
                Path.Combine(
                    location == AgentInstallLocation.WorkspaceRoot ? workspaceRoot : installDirectory,
                    fileName),
                processName);
        }
    }

    private static string ResolveWorkspaceRootForInstallDirectory(string installDirectory)
    {
        return Directory.GetParent(Path.GetFullPath(installDirectory))?.FullName
            ?? throw new InvalidOperationException("VALOWATCH workspace root could not be resolved.");
    }

    private static void TryExtractEmbeddedAgentResource(
        string resourceName,
        string targetPath,
        string processName)
    {
        using Stream? resourceStream = typeof(SelfUpdateInstaller).Assembly.GetManifestResourceStream(resourceName);
        if (resourceStream is null)
        {
            WriteLog($"Embedded agent resource was not found and will be skipped: {resourceName}");
            return;
        }

        byte[] resourceSha256 = ComputeSha256(resourceStream);
        if (resourceStream.CanSeek)
        {
            resourceStream.Position = 0;
        }
        else
        {
            WriteLog($"Embedded agent resource stream is not seekable and will be skipped: {resourceName}");
            return;
        }

        string existingAgentStatus;
        if (!File.Exists(targetPath))
        {
            existingAgentStatus = "target is missing.";
        }
        else if (TryValidateExecutableFileWithRetry(
            targetPath,
            $"existing agent resource {resourceName}",
            out existingAgentStatus))
        {
            byte[] existingSha256 = ComputeSha256(targetPath);
            if (CryptographicOperations.FixedTimeEquals(resourceSha256, existingSha256))
            {
                WriteLog(
                    "Embedded agent resource rewrite skipped because the installed agent already matches the embedded resource. " +
                    $"Target: {targetPath}. SHA-256: {Convert.ToHexString(existingSha256)}.");
                return;
            }

            existingAgentStatus += $" Existing SHA-256 differs from embedded resource. Existing: {Convert.ToHexString(existingSha256)}. Embedded: {Convert.ToHexString(resourceSha256)}.";
        }

        string temporaryPath = targetPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.new";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? AppContext.BaseDirectory);
            WriteLog(
                "Embedded agent resource repair is attempting extraction because the installed agent is missing or unreadable. " +
                $"Target: {targetPath}. Status: {existingAgentStatus}");
            using (FileStream targetStream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                resourceStream.CopyTo(targetStream);
                targetStream.Flush(flushToDisk: true);
            }

            ValidateExecutableFileWithRetry(temporaryPath, $"embedded agent resource {resourceName}");
            if (File.Exists(targetPath) && FilesHaveSameSha256(temporaryPath, targetPath))
            {
                File.Delete(temporaryPath);
                WriteLog($"Embedded agent resource is already installed: {targetPath}");
                return;
            }

            StopProcessFromPath(processName, targetPath);
            MoveFileWithRetry(temporaryPath, targetPath, $"embedded agent resource {resourceName}");
            if (TryValidateExecutableFileWithRetry(targetPath, $"installed agent resource {resourceName}", out string validationStatus))
            {
                WriteLog($"Embedded agent resource installed: {targetPath}");
            }
            else
            {
                WriteLog(
                    $"Embedded agent resource was placed but final validation is still blocked: {targetPath}. " +
                    validationStatus);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            WriteLog($"Embedded agent resource could not be installed: {resourceName}", exception);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    WriteLog($"Temporary embedded agent file could not be removed: {temporaryPath}", exception);
                }
            }
        }
    }

    private static void StopProcessFromPath(string processName, string targetExecutablePath)
    {
        if (!File.Exists(targetExecutablePath))
        {
            return;
        }

        string normalizedTargetPath = Path.GetFullPath(targetExecutablePath);
        foreach (Process candidateProcess in Process.GetProcessesByName(processName))
        {
            using (candidateProcess)
            {
                if (candidateProcess.Id == Environment.ProcessId || !ProcessMatchesPath(candidateProcess, normalizedTargetPath))
                {
                    continue;
                }

                try
                {
                    candidateProcess.CloseMainWindow();
                    if (!candidateProcess.WaitForExit(3000))
                    {
                        candidateProcess.Kill(entireProcessTree: true);
                        candidateProcess.WaitForExit(5000);
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    WriteLog($"Could not stop agent process {candidateProcess.Id}.", exception);
                }
            }
        }
    }

    private static void MoveFileWithRetry(string sourcePath, string targetPath, string label)
    {
        Exception? lastReplacementException = null;
        for (int replacementAttempt = 1; replacementAttempt <= 20; replacementAttempt++)
        {
            try
            {
                File.Move(sourcePath, targetPath, overwrite: true);
                return;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                lastReplacementException = exception;
                if (replacementAttempt >= 20)
                {
                    break;
                }

                Thread.Sleep(TimeSpan.FromMilliseconds(500));
            }
        }

        throw new IOException($"{label} remained locked after 20 replacement attempts.", lastReplacementException);
    }

    private static void ValidateExecutableFileWithRetry(string filePath, string label)
    {
        if (TryValidateExecutableFileWithRetry(filePath, label, out string status))
        {
            return;
        }

        throw new IOException(status);
    }

    private static bool TryValidateExecutableFileWithRetry(string filePath, string label, out string status)
    {
        Exception? lastValidationException = null;
        for (int validationAttempt = 1; validationAttempt <= 60; validationAttempt++)
        {
            try
            {
                ValidateExecutableFile(filePath, label);
                status = $"{label} is readable.";
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                lastValidationException = exception;
                if (validationAttempt >= 60)
                {
                    break;
                }

                Thread.Sleep(TimeSpan.FromMilliseconds(500));
            }
        }

        status = $"{label} remained unreadable after 60 validation attempts. {lastValidationException?.Message}";
        return false;
    }

    private static bool FilesHaveSameSha256(string firstPath, string secondPath)
    {
        byte[] firstHash = ComputeSha256(firstPath);
        byte[] secondHash = ComputeSha256(secondPath);
        return CryptographicOperations.FixedTimeEquals(firstHash, secondHash);
    }

    private static byte[] ComputeSha256(string filePath)
    {
        using FileStream fileStream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return SHA256.HashData(fileStream);
    }

    private static byte[] ComputeSha256(Stream stream)
    {
        return SHA256.HashData(stream);
    }

    private static void ValidateExecutableFile(string filePath, string label)
    {
        FileInfo fileInfo = new(filePath);
        if (!fileInfo.Exists || fileInfo.Length < 2)
        {
            throw new IOException($"{label} is missing or empty: {filePath}");
        }

        using FileStream executableStream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (executableStream.ReadByte() != 'M' || executableStream.ReadByte() != 'Z')
        {
            throw new InvalidOperationException($"{label} is not a Windows PE executable: {filePath}");
        }
    }

    private static void RemoveObsoleteCaptureTools(string installDirectory)
    {
        string normalizedInstallDirectory = Path.GetFullPath(installDirectory);
        string ffmpegDirectory = Path.GetFullPath(Path.Combine(normalizedInstallDirectory, "ffmpeg"));
        string installPrefix = normalizedInstallDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!ffmpegDirectory.StartsWith(installPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("FFmpeg cleanup path escaped the install directory.");
        }

        if (Directory.Exists(ffmpegDirectory))
        {
            try
            {
                Directory.Delete(ffmpegDirectory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A locked obsolete tool must not prevent the new audio-only app from starting.
                WriteLog($"Obsolete FFmpeg directory could not be removed: {ffmpegDirectory}", exception);
            }
        }
    }

    private static void WriteUpdateCompletedMarker(string installDirectory)
    {
        string workspaceRoot = Directory.GetParent(Path.GetFullPath(installDirectory))?.FullName
            ?? throw new InvalidOperationException("VALOWATCH workspace root could not be resolved.");
        string dataDirectory = Path.Combine(workspaceRoot, "data");
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(
            Path.Combine(dataDirectory, "update-completed.pending"),
            DateTimeOffset.UtcNow.ToString("O"),
            Encoding.UTF8);
    }

    private static void StartInstalledApp(string targetExecutablePath)
    {
        ValidateExecutableFile(targetExecutablePath, "updated VALOWATCH executable");
        ProcessStartInfo processStartInfo = new()
        {
            FileName = targetExecutablePath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(targetExecutablePath)
        };
        Process? startedProcess = Process.Start(processStartInfo);
        if (startedProcess is null)
        {
            throw new InvalidOperationException("Updated VALOWATCH could not be started.");
        }

        using (startedProcess)
        {
            if (startedProcess.WaitForExit(3000))
            {
                throw new InvalidOperationException(
                    $"Updated VALOWATCH exited immediately with code {startedProcess.ExitCode}.");
            }
        }
    }

    private static void RestoreBackup(ReplacementResult? replacementResult)
    {
        if (replacementResult?.BackupPath is null || !File.Exists(replacementResult.BackupPath))
        {
            return;
        }

        try
        {
            StopInstalledApp(replacementResult.TargetExecutablePath);
            File.Copy(replacementResult.BackupPath, replacementResult.TargetExecutablePath, overwrite: true);
            ValidateExecutableFile(replacementResult.TargetExecutablePath, "restored VALOWATCH executable");
            StartInstalledApp(replacementResult.TargetExecutablePath);
            WriteLog($"Previous VALOWATCH executable restored after failed update: {replacementResult.BackupPath}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            WriteLog("Previous VALOWATCH executable could not be restored after failed update.", exception);
        }
    }

    private static void RemoveBackup(ReplacementResult replacementResult)
    {
        if (replacementResult.BackupPath is null || !File.Exists(replacementResult.BackupPath))
        {
            return;
        }

        try
        {
            File.Delete(replacementResult.BackupPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            WriteLog($"Previous VALOWATCH backup could not be removed: {replacementResult.BackupPath}", exception);
        }
    }

    internal static bool RunRollbackSafetyDiagnostic(string diagnosticRoot, out string status)
    {
        try
        {
            string installDirectory = Path.Combine(diagnosticRoot, "app");
            Directory.CreateDirectory(installDirectory);
            string targetExecutablePath = Path.Combine(installDirectory, "VALOWATCH.exe");
            string backupPath = targetExecutablePath + ".previous";
            byte[] oldBytes = Encoding.UTF8.GetBytes("old executable placeholder");
            byte[] damagedBytes = Encoding.UTF8.GetBytes("damaged executable placeholder");
            File.WriteAllBytes(targetExecutablePath, damagedBytes);
            File.WriteAllBytes(backupPath, oldBytes);

            ReplacementResult replacementResult = new(targetExecutablePath, backupPath);
            RestoreBackupWithoutStarting(replacementResult);
            byte[] restoredBytes = File.ReadAllBytes(targetExecutablePath);
            bool restored = restoredBytes.SequenceEqual(oldBytes);
            bool currentProcessLooksExecutable = Environment.ProcessPath is not null &&
                File.Exists(Environment.ProcessPath) &&
                IsExecutableFile(Environment.ProcessPath);
            status = $"RollbackRestored: {restored}. CurrentProcessExecutable: {currentProcessLooksExecutable}.";
            return restored && currentProcessLooksExecutable;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            status = exception.Message;
            return false;
        }
    }

    internal static bool RunEmbeddedAgentResourceDiagnostic(out string status)
    {
        try
        {
            List<string> statuses = [];
            bool allResourcesReady = true;
            foreach ((string resourceName, string fileName, _, _) in AgentResources)
            {
                using Stream? resourceStream = typeof(SelfUpdateInstaller).Assembly.GetManifestResourceStream(resourceName);
                bool resourceReady = resourceStream is not null &&
                    resourceStream.Length > 2 &&
                    resourceStream.ReadByte() == 'M' &&
                    resourceStream.ReadByte() == 'Z';
                statuses.Add($"{fileName}: {resourceReady}");
                allResourcesReady &= resourceReady;
            }

            status = string.Join(". ", statuses) + ".";
            return allResourcesReady;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            status = exception.Message;
            return false;
        }
    }

    internal static bool RunEmbeddedAgentRepairDiagnostic(string diagnosticRoot, out string status)
    {
        try
        {
            string installDirectory = Path.Combine(diagnosticRoot, "app");
            Directory.CreateDirectory(installDirectory);
            RepairEmbeddedAgentResources(installDirectory);

            string agentPath = Path.Combine(diagnosticRoot, "GITHUB.exe");
            string startAgentPath = Path.Combine(diagnosticRoot, "VALOWATCH_Start.exe");
            bool agentReady = IsExecutableFile(agentPath);
            bool startAgentReady = IsExecutableFile(startAgentPath);
            status = $"GITHUB.exe: {agentReady}. VALOWATCH_Start.exe: {startAgentReady}.";
            return agentReady && startAgentReady;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            status = exception.Message;
            return false;
        }
    }

    internal static bool RunEmbeddedAgentExistingTargetSkipDiagnostic(string diagnosticRoot, out string status)
    {
        try
        {
            string installDirectory = Path.Combine(diagnosticRoot, "app");
            Directory.CreateDirectory(installDirectory);

            RepairEmbeddedAgentResources(installDirectory);

            byte[] originalAgentBytes = File.ReadAllBytes(Path.Combine(diagnosticRoot, "GITHUB.exe"));
            byte[] originalStartAgentBytes = File.ReadAllBytes(Path.Combine(diagnosticRoot, "VALOWATCH_Start.exe"));

            RepairEmbeddedAgentResources(installDirectory);

            byte[] currentAgentBytes = File.ReadAllBytes(Path.Combine(diagnosticRoot, "GITHUB.exe"));
            byte[] currentStartAgentBytes = File.ReadAllBytes(Path.Combine(diagnosticRoot, "VALOWATCH_Start.exe"));
            bool agentWasSkipped = originalAgentBytes.SequenceEqual(currentAgentBytes);
            bool startAgentWasSkipped = originalStartAgentBytes.SequenceEqual(currentStartAgentBytes);
            status = $"GITHUB.exe skipped: {agentWasSkipped}. VALOWATCH_Start.exe skipped: {startAgentWasSkipped}.";
            return agentWasSkipped && startAgentWasSkipped;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            status = exception.Message;
            return false;
        }
    }

    private static void RestoreBackupWithoutStarting(ReplacementResult replacementResult)
    {
        if (replacementResult.BackupPath is null || !File.Exists(replacementResult.BackupPath))
        {
            throw new IOException("Rollback backup is missing.");
        }

        File.Copy(replacementResult.BackupPath, replacementResult.TargetExecutablePath, overwrite: true);
    }

    private static bool IsExecutableFile(string filePath)
    {
        return TryValidateExecutableFileWithRetry(filePath, "diagnostic executable", out _);
    }

    private static void WriteLog(string message, Exception? exception = null)
    {
        try
        {
            string logDirectory = Path.Combine(Path.GetTempPath(), "VALOWATCH");
            Directory.CreateDirectory(logDirectory);
            string exceptionText = exception is null ? string.Empty : $" Exception: {exception}";
            File.AppendAllText(
                Path.Combine(logDirectory, "self-update.log"),
                $"{DateTimeOffset.Now:O} {message}{exceptionText}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch (Exception logException) when (logException is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record ReplacementResult(string TargetExecutablePath, string? BackupPath);
}
