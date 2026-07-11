using System.Diagnostics;
using System.Net.Http.Headers;

namespace VALOWATCH;

public sealed class GitAutoUpdater
{
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);
    private readonly GitUpdateSettingsStore settingsStore;
    private readonly string updatesDirectory;
    private readonly string logFilePath;

    public GitAutoUpdater(GitUpdateSettingsStore settingsStore, AppPaths appPaths)
    {
        this.settingsStore = settingsStore;
        updatesDirectory = Path.Combine(appPaths.DataDirectory, "updates");
        logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");
    }

    public async Task<GitAutoUpdateResult> DownloadAndStartInstallerAsync(
        GitUpdateCheckResult updateResult,
        CancellationToken cancellationToken)
    {
        if (updateResult.DownloadUri is null)
        {
            WriteLog("Auto update skipped because the update result did not contain a download URL.");
            return new GitAutoUpdateResult(
                GitAutoUpdateStatus.NoDownloadUri,
                "Update download URL is missing.",
                DownloadUri: updateResult.DownloadUri);
        }

        if (!LooksLikeInstallerAsset(updateResult.DownloadUri))
        {
            WriteLog($"Auto update skipped because the download URL is not an installer asset: {updateResult.DownloadUri}");
            return new GitAutoUpdateResult(
                GitAutoUpdateStatus.DownloadUriNotInstaller,
                "Update download URL is not a Windows installer asset.",
                DownloadUri: updateResult.DownloadUri);
        }

        Directory.CreateDirectory(updatesDirectory);
        string downloadPath = CreateDownloadPath(updateResult);

        try
        {
            await DownloadInstallerAsync(updateResult.DownloadUri, downloadPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            WriteLog("Auto update download failed.", exception);
            return new GitAutoUpdateResult(
                GitAutoUpdateStatus.DownloadFailed,
                exception.Message,
                downloadPath,
                updateResult.DownloadUri);
        }

        if (!IsWindowsExecutable(downloadPath))
        {
            WriteLog($"Auto update rejected the downloaded file because it is not a Windows PE executable: {downloadPath}");
            TryDeleteFile(downloadPath);
            return new GitAutoUpdateResult(
                GitAutoUpdateStatus.InvalidInstaller,
                "Downloaded update is not a Windows executable.",
                downloadPath,
                updateResult.DownloadUri);
        }

        try
        {
            StartSilentInstaller(downloadPath);
            WriteLog($"Auto update installer started: {downloadPath}");
            return new GitAutoUpdateResult(
                GitAutoUpdateStatus.InstallerStarted,
                "Installer started.",
                downloadPath,
                updateResult.DownloadUri);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            WriteLog("Auto update installer launch failed.", exception);
            return new GitAutoUpdateResult(
                GitAutoUpdateStatus.LaunchFailed,
                exception.Message,
                downloadPath,
                updateResult.DownloadUri);
        }
    }

    private async Task DownloadInstallerAsync(Uri downloadUri, string downloadPath, CancellationToken cancellationToken)
    {
        GitUpdateSettings settings = settingsStore.Load();
        using HttpClient httpClient = new()
        {
            Timeout = DownloadTimeout
        };

        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("VALOWATCH/0.1");
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        if (!string.IsNullOrWhiteSpace(settings.GitHubToken))
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.GitHubToken);
        }

        using HttpResponseMessage response = await httpClient
            .GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        string temporaryPath = $"{downloadPath}.download";
        await using Stream responseStream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using FileStream fileStream = new(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        byte[] buffer = new byte[1024 * 1024];
        long downloadedBytes = 0;
        int readByteCount;
        while ((readByteCount = await responseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, readByteCount), cancellationToken).ConfigureAwait(false);
            downloadedBytes += readByteCount;
        }

        await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (downloadedBytes <= 0)
        {
            throw new IOException("Downloaded update file is empty.");
        }

        if (File.Exists(downloadPath))
        {
            File.Delete(downloadPath);
        }

        File.Move(temporaryPath, downloadPath);
        WriteLog($"Auto update downloaded installer: {downloadPath}. Bytes: {downloadedBytes}.");
    }

    private string CreateDownloadPath(GitUpdateCheckResult updateResult)
    {
        string latestVersion = string.IsNullOrWhiteSpace(updateResult.LatestVersion)
            ? DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss")
            : SanitizeFileName(updateResult.LatestVersion);

        return Path.Combine(updatesDirectory, $"VALOWATCH_Setup_{latestVersion}.exe");
    }

    private static bool LooksLikeInstallerAsset(Uri downloadUri)
    {
        string fileName = Path.GetFileName(downloadUri.LocalPath);
        return fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            (fileName.Contains("setup", StringComparison.OrdinalIgnoreCase) ||
                fileName.Contains("installer", StringComparison.OrdinalIgnoreCase) ||
                fileName.Contains("valowatch", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsWindowsExecutable(string filePath)
    {
        byte[] header = new byte[2];
        using FileStream fileStream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return fileStream.Read(header, 0, header.Length) == header.Length &&
            header[0] == (byte)'M' &&
            header[1] == (byte)'Z';
    }

    private static string SanitizeFileName(string value)
    {
        string sanitized = value.Trim();
        foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
        {
            sanitized = sanitized.Replace(invalidCharacter, '-');
        }

        return string.IsNullOrWhiteSpace(sanitized) ? "update" : sanitized;
    }

    private static void StartSilentInstaller(string installerPath)
    {
        string installDirectory = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        ProcessStartInfo processStartInfo = new()
        {
            FileName = installerPath,
            Arguments = $"--silent --install-dir \"{installDirectory}\"",
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath),
            WindowStyle = ProcessWindowStyle.Hidden
        };

        Process? installerProcess = Process.Start(processStartInfo);
        if (installerProcess is null)
        {
            throw new InvalidOperationException("Silent installer process could not be started.");
        }
    }

    private void WriteLog(string message, Exception? exception = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath) ?? AppContext.BaseDirectory);
            string exceptionText = exception is null ? string.Empty : $" Exception: {exception}";
            File.AppendAllText(logFilePath, $"{DateTimeOffset.Now:O} [Update] {message}{exceptionText}{Environment.NewLine}");
        }
        catch (Exception logException) when (logException is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string filePath)
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
        }
    }
}
