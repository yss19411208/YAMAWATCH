using System.Diagnostics;
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

    public static bool IsUpdateInvocation(IReadOnlyList<string> args)
    {
        return args.Any(argument => string.Equals(argument, UpdateOption, StringComparison.OrdinalIgnoreCase)) &&
            args.Any(argument => string.Equals(argument, SilentOption, StringComparison.OrdinalIgnoreCase));
    }

    public static int Run(IReadOnlyList<string> args)
    {
        try
        {
            string installDirectory = ParseInstallDirectory(args);
            ValidateInstallDirectory(installDirectory);
            string targetExecutablePath = Path.Combine(installDirectory, "VALOWATCH.exe");

            Directory.CreateDirectory(installDirectory);
            StopInstalledApp(targetExecutablePath);
            ReplaceExecutable(targetExecutablePath);
            ExtractNativeResources(installDirectory);
            RemoveObsoleteCaptureTools(installDirectory);
            WriteUpdateCompletedMarker(installDirectory);
            StartInstalledApp(targetExecutablePath);
            WriteLog($"Self update completed. Target: {targetExecutablePath}");
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            WriteLog("Self update failed.", exception);
            return 1;
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

    private static void ReplaceExecutable(string targetExecutablePath)
    {
        string sourceExecutablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Self update source executable path is unavailable.");
        string temporaryPath = targetExecutablePath + $".{Environment.ProcessId}.new";
        File.Copy(sourceExecutablePath, temporaryPath, overwrite: true);

        Exception? lastReplacementException = null;
        try
        {
            for (int replacementAttempt = 1; replacementAttempt <= 20; replacementAttempt++)
            {
                StopInstalledApp(targetExecutablePath);
                try
                {
                    File.Move(temporaryPath, targetExecutablePath, overwrite: true);
                    return;
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException &&
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
        ProcessStartInfo processStartInfo = new()
        {
            FileName = targetExecutablePath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(targetExecutablePath)
        };
        if (Process.Start(processStartInfo) is null)
        {
            throw new InvalidOperationException("Updated VALOWATCH could not be started.");
        }
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
}
