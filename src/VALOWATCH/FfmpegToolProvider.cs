using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace VALOWATCH;

internal static class FfmpegToolProvider
{
    private const string FfmpegWindowsZipUrl =
        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    private const string FfmpegWindowsZipSha256Url =
        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip.sha256";

    private const string EmbeddedFfmpegResourceName = "Tools/ffmpeg.exe";

    private static readonly Regex Sha256Regex = new(
        @"\b[a-fA-F0-9]{64}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static async Task<string> ResolveFfmpegPathAsync(
        AppPaths appPaths,
        Action<string, Exception?> log,
        CancellationToken cancellationToken)
    {
        if (IsExistingFfmpegUsable(appPaths.FfmpegPath))
        {
            return appPaths.FfmpegPath;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Automatic FFmpeg setup is only supported on Windows.");
        }

        if (!Environment.Is64BitOperatingSystem)
        {
            throw new PlatformNotSupportedException("Automatic FFmpeg setup requires 64-bit Windows.");
        }

        if (TryInstallEmbeddedFfmpeg(appPaths, log))
        {
            return appPaths.FfmpegPath;
        }

        string? pathExecutable = FindExecutableOnPath("ffmpeg.exe") ?? FindExecutableOnPath("ffmpeg");
        if (!string.IsNullOrWhiteSpace(pathExecutable) && IsExistingFfmpegUsable(pathExecutable))
        {
            return pathExecutable;
        }

        Directory.CreateDirectory(appPaths.FfmpegDirectory);
        string downloadPath = Path.Combine(
            appPaths.FfmpegDirectory,
            $"ffmpeg-release-essentials.{Environment.ProcessId}.{Guid.NewGuid():N}.zip.part");
        string extractDirectory = Path.Combine(
            appPaths.FfmpegDirectory,
            $"extract-{Environment.ProcessId}-{Guid.NewGuid():N}");

        try
        {
            log($"FFmpeg was not found; downloading Windows essentials build. Url: {FfmpegWindowsZipUrl}.", null);
            using SocketsHttpHandler handler = new()
            {
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(15),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            };
            using HttpClient httpClient = new(handler)
            {
                Timeout = TimeSpan.FromMinutes(3)
            };

            string expectedSha256 = await DownloadExpectedSha256Async(httpClient, cancellationToken)
                .ConfigureAwait(false);
            await DownloadFileAsync(httpClient, FfmpegWindowsZipUrl, downloadPath, log, cancellationToken)
                .ConfigureAwait(false);
            ValidateDownloadedSha256(downloadPath, expectedSha256);

            ZipFile.ExtractToDirectory(downloadPath, extractDirectory);
            string extractedFfmpegPath = Directory
                .EnumerateFiles(extractDirectory, "ffmpeg.exe", SearchOption.AllDirectories)
                .FirstOrDefault()
                ?? throw new IOException("Downloaded FFmpeg archive did not contain ffmpeg.exe.");

            InstallFfmpegExecutable(extractedFfmpegPath, appPaths.FfmpegPath);

            log($"FFmpeg installed for 60fps streaming. Path: {appPaths.FfmpegPath}.", null);
            return appPaths.FfmpegPath;
        }
        finally
        {
            TryDeleteFile(downloadPath, log);
            TryDeleteDirectory(extractDirectory, log);
        }
    }

    private static bool TryInstallEmbeddedFfmpeg(AppPaths appPaths, Action<string, Exception?> log)
    {
        using Stream? embeddedStream = OpenEmbeddedFfmpegStream();
        if (embeddedStream is null)
        {
            return false;
        }

        Directory.CreateDirectory(appPaths.FfmpegDirectory);
        try
        {
            log($"Embedded FFmpeg resource was found; installing to {appPaths.FfmpegPath}.", null);
            using (FileStream targetStream = new(
                appPaths.FfmpegPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 1024,
                useAsync: false))
            {
                embeddedStream.CopyTo(targetStream);
                targetStream.Flush(flushToDisk: true);
            }

            ValidateExecutableHeader(appPaths.FfmpegPath);
            log($"Embedded FFmpeg installed for 60fps streaming. Path: {appPaths.FfmpegPath}.", null);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDeleteFile(appPaths.FfmpegPath, log);
            throw new IOException($"Embedded FFmpeg could not be installed to {appPaths.FfmpegPath}.", exception);
        }
    }

    private static Stream? OpenEmbeddedFfmpegStream()
    {
        Assembly assembly = typeof(FfmpegToolProvider).Assembly;
        Stream? exactStream = assembly.GetManifestResourceStream(EmbeddedFfmpegResourceName);
        if (exactStream is not null)
        {
            return exactStream;
        }

        string? fallbackName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(resourceName =>
                resourceName.EndsWith("ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
        return fallbackName is null ? null : assembly.GetManifestResourceStream(fallbackName);
    }

    private static bool IsExistingFfmpegUsable(string ffmpegPath)
    {
        try
        {
            if (!File.Exists(ffmpegPath) || new FileInfo(ffmpegPath).Length < 1024 * 1024)
            {
                return false;
            }

            ValidateExecutableHeader(ffmpegPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? FindExecutableOnPath(string executableName)
    {
        string? pathText = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathText))
        {
            return null;
        }

        foreach (string directoryPath in pathText.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidatePath = Path.Combine(directoryPath, executableName);
                if (File.Exists(candidatePath))
                {
                    return candidatePath;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
            }
        }

        return null;
    }

    private static async Task<string> DownloadExpectedSha256Async(
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        string sha256Text = await httpClient
            .GetStringAsync(FfmpegWindowsZipSha256Url, cancellationToken)
            .ConfigureAwait(false);
        Match match = Sha256Regex.Match(sha256Text);
        if (!match.Success)
        {
            throw new IOException("FFmpeg SHA-256 file did not contain a valid SHA-256 digest.");
        }

        return match.Value.ToUpperInvariant();
    }

    private static async Task DownloadFileAsync(
        HttpClient httpClient,
        string url,
        string targetPath,
        Action<string, Exception?> log,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await httpClient
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long? expectedBytes = response.Content.Headers.ContentLength;
        log($"FFmpeg download response received. ExpectedBytes: {expectedBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}.", null);

        await using Stream sourceStream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using FileStream targetStream = new(
            targetPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 256,
            useAsync: true);

        byte[] buffer = new byte[1024 * 256];
        long totalBytes = 0;
        long nextLogBytes = 10L * 1024L * 1024L;
        while (true)
        {
            int readBytes = await sourceStream
                .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (readBytes == 0)
            {
                break;
            }

            await targetStream
                .WriteAsync(buffer.AsMemory(0, readBytes), cancellationToken)
                .ConfigureAwait(false);
            totalBytes += readBytes;

            if (totalBytes >= nextLogBytes)
            {
                log($"FFmpeg download progress. Bytes: {totalBytes}.", null);
                nextLogBytes += 10L * 1024L * 1024L;
            }
        }

        await targetStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (totalBytes <= 0)
        {
            throw new IOException("FFmpeg download produced an empty file.");
        }
    }

    private static void InstallFfmpegExecutable(string sourcePath, string targetPath)
    {
        ValidateExecutableHeader(sourcePath);
        File.Copy(sourcePath, targetPath, overwrite: true);
        ValidateExecutableHeader(targetPath);
    }

    private static void ValidateDownloadedSha256(string filePath, string expectedSha256)
    {
        using FileStream fileStream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        string actualSha256 = Convert.ToHexString(SHA256.HashData(fileStream));
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"FFmpeg download SHA-256 mismatch. Expected: {expectedSha256}. Actual: {actualSha256}.");
        }
    }

    private static void ValidateExecutableHeader(string filePath)
    {
        using FileStream fileStream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fileStream.ReadByte() != 'M' || fileStream.ReadByte() != 'Z')
        {
            throw new IOException($"FFmpeg executable is not a Windows PE executable: {filePath}.");
        }
    }

    private static void TryDeleteFile(string filePath, Action<string, Exception?> log)
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
            log($"Temporary FFmpeg download could not be deleted: {filePath}.", exception);
        }
    }

    private static void TryDeleteDirectory(string directoryPath, Action<string, Exception?> log)
    {
        try
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log($"Temporary FFmpeg extraction directory could not be deleted: {directoryPath}.", exception);
        }
    }
}
