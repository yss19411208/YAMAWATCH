using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace VALOWATCH;

public sealed class GitAutoUpdater
{
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);
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
        GitAutoUpdateResult downloadResult = await DownloadAndValidateInstallerAsync(updateResult, cancellationToken)
            .ConfigureAwait(false);
        if (!downloadResult.InstallerReady || string.IsNullOrWhiteSpace(downloadResult.DownloadPath))
        {
            return downloadResult;
        }

        try
        {
            StartSilentInstaller(downloadResult.DownloadPath);
            WriteLog($"Auto update installer started: {downloadResult.DownloadPath}");
            return new GitAutoUpdateResult(
                GitAutoUpdateStatus.InstallerStarted,
                "Installer started.",
                downloadResult.DownloadPath,
                updateResult.DownloadUri);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            WriteLog("Auto update installer launch failed.", exception);
            return new GitAutoUpdateResult(
                GitAutoUpdateStatus.LaunchFailed,
                exception.Message,
                downloadResult.DownloadPath,
                updateResult.DownloadUri);
        }
    }

    public async Task<GitAutoUpdateResult> DownloadAndValidateInstallerAsync(
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

        if (File.Exists(downloadPath) && TryValidateInstaller(downloadPath, updateResult.ExpectedSha256, out string existingStatus))
        {
            WriteLog($"Auto update reused validated installer: {downloadPath}. {existingStatus}");
            return new GitAutoUpdateResult(
                GitAutoUpdateStatus.InstallerReady,
                existingStatus,
                downloadPath,
                updateResult.DownloadUri);
        }

        TryDeleteFile(downloadPath);

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

        if (!TryValidateInstaller(downloadPath, updateResult.ExpectedSha256, out string validationStatus))
        {
            WriteLog($"Auto update rejected the downloaded installer: {downloadPath}. {validationStatus}");
            TryDeleteFile(downloadPath);
            return new GitAutoUpdateResult(
                GitAutoUpdateStatus.InvalidInstaller,
                validationStatus,
                downloadPath,
                updateResult.DownloadUri);
        }

        WriteLog($"Auto update installer validated: {downloadPath}. {validationStatus}");
        return new GitAutoUpdateResult(
            GitAutoUpdateStatus.InstallerReady,
            validationStatus,
            downloadPath,
            updateResult.DownloadUri);
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

        string temporaryPath = $"{downloadPath}.download";
        long existingBytes = File.Exists(temporaryPath) ? new FileInfo(temporaryPath).Length : 0;
        using HttpRequestMessage request = new(HttpMethod.Get, downloadUri);
        if (existingBytes > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingBytes, null);
        }

        using HttpResponseMessage response = await httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existingBytes > 0)
        {
            if (File.Exists(downloadPath))
            {
                File.Delete(downloadPath);
            }

            File.Move(temporaryPath, downloadPath);
            WriteLog($"Auto update promoted a completed partial installer for validation: {downloadPath}. Bytes: {existingBytes}.");
            return;
        }

        response.EnsureSuccessStatusCode();
        bool appendResponse = existingBytes > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!appendResponse)
        {
            existingBytes = 0;
        }

        long? expectedTotalBytes = response.Content.Headers.ContentRange?.Length;
        if (expectedTotalBytes is null && response.Content.Headers.ContentLength is long contentLength)
        {
            expectedTotalBytes = existingBytes + contentLength;
        }

        long downloadedBytes = existingBytes;
        {
            await using Stream responseStream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using FileStream fileStream = new(
                temporaryPath,
                appendResponse ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            byte[] buffer = new byte[1024 * 1024];
            int readByteCount;
            while ((readByteCount = await responseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, readByteCount), cancellationToken).ConfigureAwait(false);
                downloadedBytes += readByteCount;
            }

            await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (downloadedBytes <= 0)
        {
            throw new IOException("Downloaded update file is empty.");
        }

        if (expectedTotalBytes is long expectedBytes && downloadedBytes != expectedBytes)
        {
            throw new IOException($"Downloaded update size did not match. Expected: {expectedBytes}. Actual: {downloadedBytes}.");
        }

        if (File.Exists(downloadPath))
        {
            File.Delete(downloadPath);
        }

        File.Move(temporaryPath, downloadPath);
        WriteLog($"Auto update downloaded installer: {downloadPath}. Bytes: {downloadedBytes}. Resumed: {appendResponse}.");
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

    private static bool TryValidateInstaller(string filePath, string expectedSha256, out string status)
    {
        if (!IsWindowsExecutable(filePath))
        {
            status = "Downloaded update is not a Windows PE executable.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            status = "Windows PE validation passed; GitHub did not provide a SHA-256 digest.";
            return true;
        }

        string actualSha256 = ComputeSha256(filePath);
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            status = $"SHA-256 mismatch. Expected: {expectedSha256}. Actual: {actualSha256}.";
            return false;
        }

        status = $"Windows PE and SHA-256 validation passed: {actualSha256}.";
        return true;
    }

    private static string ComputeSha256(string filePath)
    {
        using FileStream fileStream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(fileStream));
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
