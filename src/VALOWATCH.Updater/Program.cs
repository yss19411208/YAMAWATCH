using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VALOWATCH.Updater;

internal static class Program
{
    private const string Repository = "yss19411208/YAMAWATCH";
    private const string AppAssetName = "VALOWATCH_App.exe";
    private const string AgentAssetName = "GITHUB.exe";
    private const string StartAgentAssetName = "VALOWATCH_Start.exe";
    private const string InstalledAppName = "VALOWATCH.exe";
    private const string AgentFileName = "GITHUB.exe";
    private const string StartAgentFileName = "VALOWATCH_Start.exe";
    private const string AgentMutexName = "Local\\VALOWATCH.GitHubAgent";
    private const int MaximumAttempts = 5;
    private const int ApplicationControlPolicyBlockedErrorCode = 4551;
    private static int appReplacementInProgress;
    private static DateTimeOffset nextInstalledAppLaunchAttemptAtUtc = DateTimeOffset.MinValue;
    private static DateTimeOffset nextStartAgentLaunchAttemptAtUtc = DateTimeOffset.MinValue;
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InstalledAppLaunchRetryInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan StartAgentLaunchRetryInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30)
    ];
    private static readonly TimeSpan[] PolicyBlockedStartRetryDelays =
    [
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60)
    ];

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (args.Any(argument => string.Equals(
            argument,
            "--replace-agent",
            StringComparison.OrdinalIgnoreCase)))
        {
            return RunAgentReplacement(args);
        }

        if (args.Any(argument => string.Equals(
            argument,
            "--check-installed-app-hash",
            StringComparison.OrdinalIgnoreCase)))
        {
            return RunInstalledAppHashDiagnostic(args);
        }

        string installDirectory;
        try
        {
            installDirectory = ResolveInstallDirectory(args);
            ValidateInstallDirectory(installDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException or NotSupportedException)
        {
            WriteLog("Updater argument validation failed.", exception);
            return 1;
        }

        bool watchMode = args.Any(argument => string.Equals(
                argument,
                "--watch",
                StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(
                Path.GetFileNameWithoutExtension(Environment.ProcessPath),
                "GITHUB",
                StringComparison.OrdinalIgnoreCase);
        if (watchMode)
        {
            bool disableUpdates = args.Any(argument => string.Equals(
                argument,
                "--disable-updates",
                StringComparison.OrdinalIgnoreCase));
            return await RunWatchAgentAsync(installDirectory, disableUpdates).ConfigureAwait(false);
        }

        int updateExitCode;
        try
        {
            updateExitCode = await RunUpdateAsync(
                installDirectory,
                restartWhenCurrent: true).ConfigureAwait(false);
        }
        catch (Exception exception) when (ContainsRetryableException(exception))
        {
            WriteLog(
                "Dedicated updater could not reach GitHub; existing VALOWATCH will restart and updates will retry later. " +
                SummarizeException(exception));
            RestartInstalledAppIfPresent(installDirectory);
            updateExitCode = 1;
        }
        catch (Exception exception)
        {
            WriteLog("Dedicated updater failed.", exception);
            RestartInstalledAppIfPresent(installDirectory);
            updateExitCode = 1;
        }

        try
        {
            string installedAgentPath = InstallOrRefreshAgent(installDirectory);
            StartWatchAgent(installedAgentPath, installDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            WriteLog("GITHUB watch agent could not be installed or started.", exception);
            return 1;
        }

        return updateExitCode;
    }

    private static async Task<int> RunUpdateAsync(string installDirectory, bool restartWhenCurrent)
    {
        using HttpClient httpClient = CreateHttpClient();
        ReleaseAppAsset appAsset = await ExecuteWithRetryAsync(
            "latest release lookup",
            cancellationToken => GetLatestReleaseAssetAsync(httpClient, AppAssetName, cancellationToken)).ConfigureAwait(false);

        if (InstalledAppMatchesRelease(installDirectory, appAsset.ExpectedSha256, out string installedAppStatus))
        {
            WriteLog($"Dedicated update skipped because the installed app is already current. {installedAppStatus}");
            if (restartWhenCurrent)
            {
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                RestartInstalledAppIfPresent(installDirectory);
            }

            return 0;
        }

        string workspaceRoot = Directory.GetParent(installDirectory)?.FullName
            ?? throw new InvalidOperationException("VALOWATCH workspace root could not be resolved.");
        string updateDirectory = Path.Combine(workspaceRoot, "data", "updates", "dedicated");
        Directory.CreateDirectory(updateDirectory);
        string safeTag = SanitizeFileName(appAsset.TagName);
        string downloadedAppPath = Path.Combine(updateDirectory, $"VALOWATCH_App_{safeTag}.exe");

        await ExecuteWithRetryAsync(
            "app download",
            cancellationToken => DownloadAndValidateAppAsync(
                httpClient,
                appAsset,
                downloadedAppPath,
                cancellationToken)).ConfigureAwait(false);

        int updateExitCode;
        Interlocked.Exchange(ref appReplacementInProgress, 1);
        try
        {
            updateExitCode = await LaunchAppSelfUpdateAsync(
                downloadedAppPath,
                installDirectory).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref appReplacementInProgress, 0);
        }
        if (updateExitCode != 0)
        {
            WriteLog($"App self-update returned exit code {updateExitCode}.");
            RestartInstalledAppIfPresent(installDirectory);
            return 1;
        }

        WriteLog($"Dedicated update completed. Tag: {appAsset.TagName}. App: {downloadedAppPath}");
        return 0;
    }

    private static async Task<int> RunWatchAgentAsync(string installDirectory, bool disableUpdates)
    {
        using Mutex singleInstanceMutex = new(true, AgentMutexName, out bool ownsSingleInstance);
        if (!ownsSingleInstance)
        {
            return 0;
        }

        WriteLog(
            $"GITHUB watch agent started. InstallDirectory: {installDirectory}. " +
            $"WatchdogSeconds: {WatchdogInterval.TotalSeconds:0}. " +
            $"UpdateMinutes: {UpdateCheckInterval.TotalMinutes:0}. DisableUpdates: {disableUpdates}.");

        DateTimeOffset nextUpdateCheckAtUtc = DateTimeOffset.MinValue;
        Task<int>? activeUpdateTask = null;
        while (true)
        {
            try
            {
                if (Volatile.Read(ref appReplacementInProgress) == 0)
                {
                    TryEnsureInstalledAppRunning(installDirectory);
                    EnsureStartAgentRunningIfPresent(installDirectory);
                }

                if (activeUpdateTask is { IsCompleted: true })
                {
                    int updateExitCode = await activeUpdateTask.ConfigureAwait(false);
                    WriteLog($"GITHUB background update check completed. ExitCode: {updateExitCode}.");
                    if (updateExitCode == 10)
                    {
                        WriteLog("GITHUB is exiting so the validated replacement can be installed.");
                        return 0;
                    }

                    activeUpdateTask = null;
                    nextUpdateCheckAtUtc = DateTimeOffset.UtcNow.Add(UpdateCheckInterval);
                }

                if (!disableUpdates &&
                    activeUpdateTask is null &&
                    DateTimeOffset.UtcNow >= nextUpdateCheckAtUtc)
                {
                    activeUpdateTask = RunBackgroundUpdateCheckAsync(installDirectory);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                WriteLog("GITHUB watch iteration failed; monitoring will continue.", exception);
            }

            await Task.Delay(WatchdogInterval).ConfigureAwait(false);
            GC.KeepAlive(singleInstanceMutex);
        }
    }

    private static async Task<int> RunBackgroundUpdateCheckAsync(string installDirectory)
    {
        try
        {
            if (await TryStartAgentReplacementAsync(installDirectory).ConfigureAwait(false))
            {
                return 10;
            }

            await TryEnsureStartAgentInstalledAndRunningAsync(installDirectory).ConfigureAwait(false);

            return await RunUpdateAsync(
                installDirectory,
                restartWhenCurrent: false).ConfigureAwait(false);
        }
        catch (Exception exception) when (ContainsRetryableException(exception))
        {
            WriteLog(
                "GITHUB background update check could not reach GitHub; monitoring will retry on the next schedule. " +
                SummarizeException(exception));
            return 1;
        }
        catch (Exception exception)
        {
            WriteLog("GITHUB background update check failed; the current app will remain active.", exception);
            return 1;
        }
    }

    private static async Task TryEnsureStartAgentInstalledAndRunningAsync(string installDirectory)
    {
        try
        {
            await EnsureStartAgentInstalledAndRunningAsync(installDirectory).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or TaskCanceledException or HttpRequestException or JsonException)
        {
            WriteLog("VALOWATCH Start agent maintenance was skipped; app update will continue.", exception);
        }
    }

    private static async Task<bool> TryStartAgentReplacementAsync(string installDirectory)
    {
        string currentAgentPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Current GITHUB executable path is unavailable.");
        using HttpClient httpClient = CreateHttpClient();
        ReleaseAppAsset agentAsset = await ExecuteWithRetryAsync(
            "GITHUB agent release lookup",
            cancellationToken => GetLatestReleaseAssetAsync(httpClient, AgentAssetName, cancellationToken)).ConfigureAwait(false);
        if (FileMatchesRelease(currentAgentPath, agentAsset.ExpectedSha256, out string currentStatus))
        {
            WriteLog($"GITHUB agent is already current. {currentStatus}");
            return false;
        }

        string workspaceRoot = Directory.GetParent(Path.GetFullPath(installDirectory))?.FullName
            ?? throw new InvalidOperationException("VALOWATCH workspace root could not be resolved.");
        string updateDirectory = Path.Combine(workspaceRoot, "data", "updates", "github-agent");
        Directory.CreateDirectory(updateDirectory);
        string downloadedAgentPath = Path.Combine(
            updateDirectory,
            $"GITHUB_{SanitizeFileName(agentAsset.TagName)}.exe");
        await ExecuteWithRetryAsync(
            "GITHUB agent download",
            cancellationToken => DownloadAndValidateAppAsync(
                httpClient,
                agentAsset,
                downloadedAgentPath,
                cancellationToken)).ConfigureAwait(false);

        ProcessStartInfo replacementProcessStartInfo = new()
        {
            FileName = downloadedAgentPath,
            UseShellExecute = false,
            WorkingDirectory = updateDirectory,
            CreateNoWindow = true
        };
        replacementProcessStartInfo.ArgumentList.Add("--replace-agent");
        replacementProcessStartInfo.ArgumentList.Add("--target-agent");
        replacementProcessStartInfo.ArgumentList.Add(currentAgentPath);
        replacementProcessStartInfo.ArgumentList.Add("--parent-pid");
        replacementProcessStartInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        replacementProcessStartInfo.ArgumentList.Add("--install-dir");
        replacementProcessStartInfo.ArgumentList.Add(installDirectory);
        Process replacementProcess = await StartProcessWithPolicyRetryAsync(
                replacementProcessStartInfo,
                "validated GITHUB replacement")
            .ConfigureAwait(false);
        replacementProcess.Dispose();
        WriteLog($"Validated GITHUB replacement was started. Current: {currentStatus}");
        return true;
    }

    private static int RunAgentReplacement(IReadOnlyList<string> args)
    {
        try
        {
            string installDirectory = ResolveInstallDirectory(args);
            ValidateInstallDirectory(installDirectory);
            string targetAgentPath = Path.GetFullPath(ReadRequiredOption(args, "--target-agent"));
            string workspaceRoot = Directory.GetParent(Path.GetFullPath(installDirectory))?.FullName
                ?? throw new InvalidOperationException("VALOWATCH workspace root could not be resolved.");
            string expectedAgentPath = Path.Combine(workspaceRoot, AgentFileName);
            if (!targetAgentPath.Equals(expectedAgentPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("GITHUB replacement target is outside the VALOWATCH workspace.");
            }

            string parentProcessIdText = ReadRequiredOption(args, "--parent-pid");
            if (!int.TryParse(
                parentProcessIdText,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out int parentProcessId) ||
                parentProcessId <= 0)
            {
                throw new ArgumentException("GITHUB replacement parent process ID is invalid.");
            }

            try
            {
                using Process parentProcess = Process.GetProcessById(parentProcessId);
                if (!parentProcess.WaitForExit(30000))
                {
                    throw new TimeoutException("The old GITHUB process did not exit within 30 seconds.");
                }
            }
            catch (ArgumentException)
            {
                // The old process already exited before the replacement inspected it.
            }

            string replacementSourcePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("GITHUB replacement source path is unavailable.");
            string temporaryTargetPath = targetAgentPath + $".{Environment.ProcessId}.new";
            File.Copy(replacementSourcePath, temporaryTargetPath, overwrite: true);
            File.Move(temporaryTargetPath, targetAgentPath, overwrite: true);
            bool disableUpdates = args.Any(argument => string.Equals(
                argument,
                "--disable-updates",
                StringComparison.OrdinalIgnoreCase));
            StartWatchAgent(targetAgentPath, installDirectory, disableUpdates);
            WriteLog($"GITHUB agent replacement completed: {targetAgentPath}");
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            WriteLog("GITHUB agent replacement failed.", exception);
            return 1;
        }
    }

    private static void TryEnsureInstalledAppRunning(string installDirectory)
    {
        string installedAppPath = Path.GetFullPath(Path.Combine(installDirectory, InstalledAppName));
        if (!File.Exists(installedAppPath) || IsProcessRunningFromPath("VALOWATCH", installedAppPath))
        {
            return;
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (nowUtc < nextInstalledAppLaunchAttemptAtUtc)
        {
            return;
        }

        nextInstalledAppLaunchAttemptAtUtc = nowUtc.Add(InstalledAppLaunchRetryInterval);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = installedAppPath,
                UseShellExecute = true,
                WorkingDirectory = installDirectory,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            WriteLog($"GITHUB restarted VALOWATCH: {installedAppPath}");
        }
        catch (System.ComponentModel.Win32Exception exception) when (IsApplicationControlPolicyBlock(exception))
        {
            WriteLog(
                $"VALOWATCH app launch was blocked by Windows application control policy; update checks will continue and launch will retry after {InstalledAppLaunchRetryInterval.TotalMinutes:0} minutes.",
                exception);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            WriteLog(
                $"VALOWATCH app launch failed; update checks will continue and launch will retry after {InstalledAppLaunchRetryInterval.TotalMinutes:0} minutes.",
                exception);
        }
    }

    private static async Task EnsureStartAgentInstalledAndRunningAsync(string installDirectory)
    {
        string workspaceRoot = Directory.GetParent(Path.GetFullPath(installDirectory))?.FullName
            ?? throw new InvalidOperationException("VALOWATCH workspace root could not be resolved.");
        string installedStartAgentPath = Path.Combine(workspaceRoot, StartAgentFileName);
        using HttpClient httpClient = CreateHttpClient();
        ReleaseAppAsset startAgentAsset = await ExecuteWithRetryAsync(
            "VALOWATCH Start agent release lookup",
            cancellationToken => GetLatestReleaseAssetAsync(httpClient, StartAgentAssetName, cancellationToken)).ConfigureAwait(false);

        if (!FileMatchesRelease(installedStartAgentPath, startAgentAsset.ExpectedSha256, out string currentStatus))
        {
            string updateDirectory = Path.Combine(workspaceRoot, "data", "updates", "start-agent");
            Directory.CreateDirectory(updateDirectory);
            string downloadedStartAgentPath = Path.Combine(
                updateDirectory,
                $"VALOWATCH_Start_{SanitizeFileName(startAgentAsset.TagName)}.exe");
            await ExecuteWithRetryAsync(
                "VALOWATCH Start agent download",
                cancellationToken => DownloadAndValidateAppAsync(
                    httpClient,
                    startAgentAsset,
                    downloadedStartAgentPath,
                    cancellationToken)).ConfigureAwait(false);
            StopProcessesFromPath("VALOWATCH_Start", installedStartAgentPath);
            string temporaryPath = installedStartAgentPath + $".{Environment.ProcessId}.new";
            File.Copy(downloadedStartAgentPath, temporaryPath, overwrite: true);
            File.Move(temporaryPath, installedStartAgentPath, overwrite: true);
            WriteLog($"VALOWATCH Start agent installed. Previous: {currentStatus}");
        }

        EnsureStartAgentRunningIfPresent(installDirectory);
    }

    private static void EnsureStartAgentRunningIfPresent(string installDirectory)
    {
        string workspaceRoot = Directory.GetParent(Path.GetFullPath(installDirectory))?.FullName
            ?? throw new InvalidOperationException("VALOWATCH workspace root could not be resolved.");
        string installedStartAgentPath = Path.Combine(workspaceRoot, StartAgentFileName);
        if (!File.Exists(installedStartAgentPath) ||
            IsProcessRunningFromPath("VALOWATCH_Start", installedStartAgentPath))
        {
            return;
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (nowUtc < nextStartAgentLaunchAttemptAtUtc)
        {
            return;
        }

        nextStartAgentLaunchAttemptAtUtc = nowUtc.Add(StartAgentLaunchRetryInterval);
        try
        {
            ProcessStartInfo processStartInfo = new()
            {
                FileName = installedStartAgentPath,
                UseShellExecute = true,
                WorkingDirectory = workspaceRoot,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            processStartInfo.ArgumentList.Add("--install-dir");
            processStartInfo.ArgumentList.Add(installDirectory);
            Process.Start(processStartInfo);
            WriteLog($"GITHUB started VALOWATCH Start agent: {installedStartAgentPath}");
        }
        catch (System.ComponentModel.Win32Exception exception) when (IsApplicationControlPolicyBlock(exception))
        {
            WriteLog(
                $"VALOWATCH Start agent launch was blocked by Windows application control policy; retrying after {StartAgentLaunchRetryInterval.TotalMinutes:0} minutes.",
                exception);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            WriteLog(
                $"VALOWATCH Start agent launch failed; retrying after {StartAgentLaunchRetryInterval.TotalMinutes:0} minutes.",
                exception);
        }
    }

    private static void StopProcessesFromPath(string processName, string expectedPath)
    {
        if (!File.Exists(expectedPath))
        {
            return;
        }

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
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(5000);
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                {
                    WriteLog($"Could not stop process {processName} for replacement.", exception);
                }
            }
        }
    }

    private static string InstallOrRefreshAgent(string installDirectory)
    {
        string workspaceRoot = Directory.GetParent(Path.GetFullPath(installDirectory))?.FullName
            ?? throw new InvalidOperationException("VALOWATCH workspace root could not be resolved.");
        string installedAgentPath = Path.Combine(workspaceRoot, AgentFileName);
        string sourceAgentPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("GITHUB source executable path is unavailable.");
        if (Path.GetFullPath(sourceAgentPath).Equals(
            Path.GetFullPath(installedAgentPath),
            StringComparison.OrdinalIgnoreCase))
        {
            return installedAgentPath;
        }

        if (IsProcessRunningFromPath("GITHUB", installedAgentPath))
        {
            WriteLog("The installed GITHUB agent is already running and will update itself from the release asset.");
            return installedAgentPath;
        }

        string temporaryAgentPath = installedAgentPath + $".{Environment.ProcessId}.new";
        File.Copy(sourceAgentPath, temporaryAgentPath, overwrite: true);
        File.Move(temporaryAgentPath, installedAgentPath, overwrite: true);
        WriteLog($"GITHUB watch agent installed: {installedAgentPath}");
        return installedAgentPath;
    }

    private static void StartWatchAgent(
        string installedAgentPath,
        string installDirectory,
        bool disableUpdates = false)
    {
        if (IsProcessRunningFromPath("GITHUB", installedAgentPath))
        {
            return;
        }

        ProcessStartInfo processStartInfo = new()
        {
            FileName = installedAgentPath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installedAgentPath),
            WindowStyle = ProcessWindowStyle.Hidden
        };
        processStartInfo.ArgumentList.Add("--watch");
        if (disableUpdates)
        {
            processStartInfo.ArgumentList.Add("--disable-updates");
        }

        processStartInfo.ArgumentList.Add("--install-dir");
        processStartInfo.ArgumentList.Add(installDirectory);
        Process.Start(processStartInfo);
        WriteLog($"GITHUB watch agent launch requested: {installedAgentPath}");
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
                        Path.GetFullPath(processPath).Equals(
                            normalizedExpectedPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                }
            }
        }

        return false;
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
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("VALOWATCH-Updater/0.1.2");
        httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return httpClient;
    }

    private static async Task<ReleaseAppAsset> GetLatestReleaseAssetAsync(
        HttpClient httpClient,
        string requiredAssetName,
        CancellationToken cancellationToken)
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
        if (!root.TryGetProperty("assets", out JsonElement assetsElement) ||
            assetsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Latest release has no assets array.");
        }

        foreach (JsonElement assetElement in assetsElement.EnumerateArray())
        {
            string assetName = ReadOptionalString(assetElement, "name");
            if (!string.Equals(assetName, requiredAssetName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string downloadUrl = ReadRequiredString(assetElement, "browser_download_url");
            string digest = NormalizeSha256Digest(ReadRequiredString(assetElement, "digest"));
            long expectedSize = assetElement.TryGetProperty("size", out JsonElement sizeElement) &&
                sizeElement.TryGetInt64(out long parsedSize)
                ? parsedSize
                : 0;
            if (expectedSize <= 0)
            {
                throw new InvalidOperationException($"{requiredAssetName} release size is missing.");
            }

            return new ReleaseAppAsset(
                tagName,
                new Uri(downloadUrl, UriKind.Absolute),
                digest,
                expectedSize);
        }

        throw new InvalidOperationException($"Latest release does not contain {requiredAssetName}.");
    }

    private static async Task<bool> DownloadAndValidateAppAsync(
        HttpClient httpClient,
        ReleaseAppAsset appAsset,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        if (File.Exists(destinationPath) && ValidateDownloadedApp(destinationPath, appAsset, out string existingStatus))
        {
            WriteLog($"Reusing validated app. {existingStatus}");
            return true;
        }

        string partialPath = destinationPath + ".download";
        long existingLength = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (existingLength >= appAsset.ExpectedSize)
        {
            File.Delete(partialPath);
            existingLength = 0;
        }

        using HttpRequestMessage request = new(HttpMethod.Get, appAsset.DownloadUri);
        if (existingLength > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingLength, null);
        }

        using HttpResponseMessage response = await httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        bool appendResponse = existingLength > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        FileMode fileMode = appendResponse ? FileMode.Append : FileMode.Create;
        if (!appendResponse)
        {
            existingLength = 0;
        }

        await using (Stream sourceStream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false))
        await using (FileStream targetStream = new(
            partialPath,
            fileMode,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 131072,
            useAsync: true))
        {
            await sourceStream.CopyToAsync(targetStream, 131072, cancellationToken).ConfigureAwait(false);
            await targetStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        long downloadedLength = new FileInfo(partialPath).Length;
        WriteLog($"App download progress: {downloadedLength}/{appAsset.ExpectedSize} bytes.");
        if (downloadedLength != appAsset.ExpectedSize)
        {
            throw new IOException(
                $"App download is incomplete. Expected {appAsset.ExpectedSize}, actual {downloadedLength}.");
        }

        if (!ValidateDownloadedApp(partialPath, appAsset, out string validationStatus))
        {
            File.Delete(partialPath);
            throw new InvalidDataException(validationStatus);
        }

        File.Move(partialPath, destinationPath, overwrite: true);
        WriteLog(validationStatus);
        return true;
    }

    private static bool ValidateDownloadedApp(
        string filePath,
        ReleaseAppAsset appAsset,
        out string status)
    {
        FileInfo fileInfo = new(filePath);
        if (!fileInfo.Exists || fileInfo.Length != appAsset.ExpectedSize)
        {
            status = $"App size mismatch. Expected {appAsset.ExpectedSize}, actual {(fileInfo.Exists ? fileInfo.Length : 0)}.";
            return false;
        }

        using (FileStream peStream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (peStream.ReadByte() != 'M' || peStream.ReadByte() != 'Z')
            {
                status = "Downloaded app is not a Windows PE executable.";
                return false;
            }
        }

        string actualSha256;
        using (FileStream hashStream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            actualSha256 = Convert.ToHexString(SHA256.HashData(hashStream));
        }

        if (!string.Equals(actualSha256, appAsset.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            status = $"App SHA-256 mismatch. Expected {appAsset.ExpectedSha256}, actual {actualSha256}.";
            return false;
        }

        status = $"App PE and SHA-256 validation passed: {actualSha256}.";
        return true;
    }

    private static async Task<int> LaunchAppSelfUpdateAsync(
        string downloadedAppPath,
        string installDirectory)
    {
        ProcessStartInfo processStartInfo = new()
        {
            FileName = downloadedAppPath,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(downloadedAppPath),
            CreateNoWindow = true
        };
        processStartInfo.ArgumentList.Add("--silent");
        processStartInfo.ArgumentList.Add("--update");
        processStartInfo.ArgumentList.Add("--install-dir");
        processStartInfo.ArgumentList.Add(installDirectory);

        using Process updateProcess = await StartProcessWithPolicyRetryAsync(
                processStartInfo,
                "downloaded app self-update")
            .ConfigureAwait(false);
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2));
        try
        {
            await updateProcess.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                updateProcess.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            throw new TimeoutException("Downloaded app self-update did not finish within two minutes.");
        }

        return updateProcess.ExitCode;
    }

    private static async Task<Process> StartProcessWithPolicyRetryAsync(
        ProcessStartInfo processStartInfo,
        string operationName)
    {
        System.ComponentModel.Win32Exception? lastPolicyException = null;
        int maximumStartAttempts = PolicyBlockedStartRetryDelays.Length + 1;
        for (int attempt = 1; attempt <= maximumStartAttempts; attempt++)
        {
            try
            {
                return Process.Start(processStartInfo)
                    ?? throw new InvalidOperationException($"{operationName} process could not be started.");
            }
            catch (System.ComponentModel.Win32Exception exception) when (IsApplicationControlPolicyBlock(exception))
            {
                lastPolicyException = exception;
                if (attempt >= maximumStartAttempts)
                {
                    break;
                }

                TimeSpan retryDelay = PolicyBlockedStartRetryDelays[attempt - 1];
                WriteLog(
                    $"{operationName} was blocked by Windows application control policy. " +
                    $"Attempt {attempt}/{maximumStartAttempts}; retrying in {retryDelay.TotalSeconds:0} seconds. " +
                    SummarizeException(exception));
                await Task.Delay(retryDelay).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            $"{operationName} remained blocked by Windows application control policy after {maximumStartAttempts} attempts.",
            lastPolicyException);
    }

    private static bool IsApplicationControlPolicyBlock(System.ComponentModel.Win32Exception exception)
    {
        return exception.NativeErrorCode == ApplicationControlPolicyBlockedErrorCode ||
            exception.Message.Contains("application control policy", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("アプリケーション制御ポリシー", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<T> ExecuteWithRetryAsync<T>(
        string operationName,
        Func<CancellationToken, Task<T>> operation)
    {
        Exception? lastException = null;
        for (int attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            using CancellationTokenSource attemptTimeout = new(TimeSpan.FromMinutes(30));
            try
            {
                return await operation(attemptTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsRetryable(exception) && attempt < MaximumAttempts)
            {
                lastException = exception;
                TimeSpan retryDelay = RetryDelays[attempt - 1];
                WriteLog(
                    $"{operationName} attempt {attempt}/{MaximumAttempts} failed. " +
                    $"Retrying in {retryDelay.TotalSeconds:0} seconds. " +
                    SummarizeException(exception));
                await Task.Delay(retryDelay).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            $"{operationName} failed after {MaximumAttempts} attempts.",
            lastException);
    }

    private static bool InstalledAppMatchesRelease(
        string installDirectory,
        string expectedSha256,
        out string status)
    {
        string installedAppPath = Path.Combine(installDirectory, InstalledAppName);
        if (!File.Exists(installedAppPath))
        {
            status = "Installed app is missing.";
            return false;
        }

        try
        {
            using FileStream appStream = new(
                installedAppPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            string installedSha256 = Convert.ToHexString(SHA256.HashData(appStream));
            bool matches = string.Equals(installedSha256, expectedSha256, StringComparison.OrdinalIgnoreCase);
            status = matches
                ? $"Installed SHA-256 matches release: {installedSha256}."
                : $"Installed SHA-256 differs from release. Installed: {installedSha256}. Expected: {expectedSha256}.";
            return matches;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            status = $"Installed app SHA-256 could not be read: {exception.Message}";
            return false;
        }
    }

    private static bool FileMatchesRelease(string filePath, string expectedSha256, out string status)
    {
        if (!File.Exists(filePath))
        {
            status = "File is missing.";
            return false;
        }

        try
        {
            using FileStream fileStream = new(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            string actualSha256 = Convert.ToHexString(SHA256.HashData(fileStream));
            bool matches = string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase);
            status = matches
                ? $"SHA-256 matches release: {actualSha256}."
                : $"SHA-256 differs from release. Actual: {actualSha256}. Expected: {expectedSha256}.";
            return matches;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            status = $"SHA-256 could not be read: {exception.Message}";
            return false;
        }
    }

    private static int RunInstalledAppHashDiagnostic(IReadOnlyList<string> args)
    {
        try
        {
            string installDirectory = ResolveInstallDirectory(args);
            string expectedSha256 = ReadRequiredOption(args, "--expected-sha256");
            if (expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
            {
                throw new ArgumentException("Expected SHA-256 must contain exactly 64 hexadecimal characters.");
            }

            bool matches = InstalledAppMatchesRelease(installDirectory, expectedSha256, out string status);
            WriteLog($"Installed app hash diagnostic: {(matches ? "ready" : "failed")}. {status}");
            return matches ? 0 : 1;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException)
        {
            WriteLog("Installed app hash diagnostic failed.", exception);
            return 1;
        }
    }

    private static string ReadRequiredOption(IReadOnlyList<string> args, string optionName)
    {
        string optionPrefix = optionName + "=";
        for (int argumentIndex = 0; argumentIndex < args.Count; argumentIndex++)
        {
            string argument = args[argumentIndex];
            if (argument.StartsWith(optionPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return argument[optionPrefix.Length..].Trim().Trim('"');
            }

            if (string.Equals(argument, optionName, StringComparison.OrdinalIgnoreCase) &&
                argumentIndex + 1 < args.Count)
            {
                return args[argumentIndex + 1].Trim().Trim('"');
            }
        }

        throw new ArgumentException($"Required option is missing: {optionName}.");
    }

    private static bool IsRetryable(Exception exception)
    {
        return exception is HttpRequestException or TaskCanceledException or IOException or JsonException;
    }

    private static bool ContainsRetryableException(Exception exception)
    {
        for (Exception? currentException = exception;
             currentException is not null;
             currentException = currentException.InnerException)
        {
            if (IsRetryable(currentException))
            {
                return true;
            }
        }

        return false;
    }

    private static string SummarizeException(Exception exception)
    {
        List<string> exceptionParts = [];
        for (Exception? currentException = exception;
             currentException is not null && exceptionParts.Count < 3;
             currentException = currentException.InnerException)
        {
            string message = currentException.Message
                .Replace(Environment.NewLine, " ", StringComparison.Ordinal)
                .Trim();
            exceptionParts.Add($"{currentException.GetType().Name}: {message}");
        }

        return $"Exception: {string.Join(" -> ", exceptionParts)}";
    }

    private static string ResolveInstallDirectory(IReadOnlyList<string> args)
    {
        const string installDirectoryOption = "--install-dir";
        string optionPrefix = installDirectoryOption + "=";
        for (int argumentIndex = 0; argumentIndex < args.Count; argumentIndex++)
        {
            string argument = args[argumentIndex];
            if (argument.StartsWith(optionPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(argument[optionPrefix.Length..].Trim().Trim('"'));
            }

            if (string.Equals(argument, installDirectoryOption, StringComparison.OrdinalIgnoreCase) &&
                argumentIndex + 1 < args.Count)
            {
                return Path.GetFullPath(args[argumentIndex + 1].Trim().Trim('"'));
            }
        }

        string documentsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documentsDirectory))
        {
            throw new InvalidOperationException("Windows Documents directory could not be resolved.");
        }

        return Path.Combine(documentsDirectory, "VALOWATCH", "app");
    }

    private static void ValidateInstallDirectory(string installDirectory)
    {
        string fullPath = Path.GetFullPath(installDirectory);
        string rootPath = Path.GetPathRoot(fullPath) ?? string.Empty;
        if (string.Equals(
            fullPath.TrimEnd(Path.DirectorySeparatorChar),
            rootPath.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Updater cannot target a drive root.");
        }
    }

    private static void RestartInstalledAppIfPresent(string installDirectory)
    {
        try
        {
            string installedAppPath = Path.Combine(installDirectory, InstalledAppName);
            if (!File.Exists(installedAppPath))
            {
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = installedAppPath,
                UseShellExecute = true,
                WorkingDirectory = installDirectory,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            WriteLog("Existing VALOWATCH restarted after updater failure.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            WriteLog("Existing VALOWATCH could not be restarted after updater failure.", exception);
        }
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        string value = ReadOptionalString(element, propertyName);
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"GitHub response is missing {propertyName}.");
    }

    private static string ReadOptionalString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement propertyElement) &&
            propertyElement.ValueKind == JsonValueKind.String
            ? propertyElement.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string NormalizeSha256Digest(string digest)
    {
        const string prefix = "sha256:";
        string normalized = digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? digest[prefix.Length..]
            : digest;
        normalized = normalized.Trim();
        if (normalized.Length != 64 || !normalized.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("GitHub app asset SHA-256 digest is invalid.");
        }

        return normalized.ToUpperInvariant();
    }

    private static string SanitizeFileName(string value)
    {
        string sanitized = value.Trim();
        foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
        {
            sanitized = sanitized.Replace(invalidCharacter, '-');
        }

        return string.IsNullOrWhiteSpace(sanitized) ? "latest" : sanitized;
    }

    private static void WriteLog(string message, Exception? exception = null)
    {
        try
        {
            string logDirectory = Path.Combine(Path.GetTempPath(), "VALOWATCH");
            Directory.CreateDirectory(logDirectory);
            string exceptionText = exception is null ? string.Empty : $" Exception: {exception}";
            File.AppendAllText(
                Path.Combine(logDirectory, "dedicated-updater.log"),
                $"{DateTimeOffset.Now:O} {message}{exceptionText}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch (Exception logException) when (logException is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record ReleaseAppAsset(
        string TagName,
        Uri DownloadUri,
        string ExpectedSha256,
        long ExpectedSize);
}
