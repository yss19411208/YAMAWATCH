using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace VALOWATCH;

internal sealed class ScreenStreamSession : IAsyncDisposable, IDisposable
{
    private static readonly Regex TryCloudflareUrlRegex = new(
        @"https://[a-zA-Z0-9-]+\.trycloudflare\.com",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const string CloudflaredWindowsAmd64DownloadUrl =
        "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";

    private readonly ScreenStreamingServer streamingServer;
    private readonly Process cloudflaredProcess;
    private readonly Action<string, Exception?> log;
    private bool disposed;

    private ScreenStreamSession(
        ScreenStreamingServer streamingServer,
        Process cloudflaredProcess,
        string tunnelBaseUrl,
        Action<string, Exception?> log)
    {
        this.streamingServer = streamingServer;
        this.cloudflaredProcess = cloudflaredProcess;
        this.log = log;
        TunnelBaseUrl = tunnelBaseUrl.TrimEnd('/');
        PublicUrl = $"{TunnelBaseUrl}/{streamingServer.PublicPath}";
        Target = streamingServer.Target;
        FramesPerSecond = streamingServer.FramesPerSecond;
        JpegQuality = streamingServer.JpegQuality;
        MaxWidth = streamingServer.MaxWidth;
        Method = streamingServer.Method;
        EngineName = streamingServer.EngineName;
        StartedAtUtc = DateTimeOffset.UtcNow;
    }

    public ScreenCaptureTarget Target { get; }

    public int FramesPerSecond { get; }

    public long JpegQuality { get; }

    public int MaxWidth { get; }

    public ScreenStreamMethod Method { get; }

    public string EngineName { get; }

    public string TunnelBaseUrl { get; }

    public string PublicUrl { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public static async Task<ScreenStreamSession> StartAsync(
        AppPaths appPaths,
        ScreenCaptureTarget target,
        Action<string, Exception?> log,
        CancellationToken cancellationToken)
    {
        return await StartAsync(appPaths, ScreenStreamOptions.Create(target), log, cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<ScreenStreamSession> StartAsync(
        AppPaths appPaths,
        ScreenStreamOptions options,
        Action<string, Exception?> log,
        CancellationToken cancellationToken)
    {
        ScreenStreamingServer? streamingServer = null;
        Process? tunnelProcess = null;
        try
        {
            string? ffmpegPath = null;
            if (options.RequiresFfmpeg)
            {
                ffmpegPath = await FfmpegToolProvider
                    .ResolveFfmpegPathAsync(appPaths, log, cancellationToken)
                    .ConfigureAwait(false);
            }

            string streamWorkDirectory = Path.Combine(appPaths.DataDirectory, "streaming");
            streamingServer = ScreenStreamingServer.Start(options, ffmpegPath, streamWorkDirectory, log);
            string cloudflaredPath = await ResolveCloudflaredPathAsync(appPaths, log, cancellationToken)
                .ConfigureAwait(false);
            (tunnelProcess, string tunnelBaseUrl) = await StartQuickTunnelAsync(
                    cloudflaredPath,
                    streamingServer.LocalOrigin,
                    log,
                    cancellationToken)
                .ConfigureAwait(false);

            return new ScreenStreamSession(streamingServer, tunnelProcess, tunnelBaseUrl, log);
        }
        catch
        {
            if (tunnelProcess is not null)
            {
                StopProcess(tunnelProcess, log);
            }

            if (streamingServer is not null)
            {
                await streamingServer.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        StopProcess(cloudflaredProcess, log);
        await streamingServer.DisposeAsync().ConfigureAwait(false);
        log(
            $"Screen stream session stopped. Target: {ScreenCaptureTargetNames.ToOptionValue(Target)}. Url: {PublicUrl}.",
            null);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static async Task<string> ResolveCloudflaredPathAsync(
        AppPaths appPaths,
        Action<string, Exception?> log,
        CancellationToken cancellationToken)
    {
        if (File.Exists(appPaths.CloudflaredPath) && new FileInfo(appPaths.CloudflaredPath).Length > 1024 * 1024)
        {
            return appPaths.CloudflaredPath;
        }

        string? pathExecutable = FindExecutableOnPath("cloudflared.exe") ?? FindExecutableOnPath("cloudflared");
        if (!string.IsNullOrWhiteSpace(pathExecutable))
        {
            return pathExecutable;
        }

        if (!Environment.Is64BitOperatingSystem)
        {
            throw new PlatformNotSupportedException("Automatic cloudflared download currently requires 64-bit Windows.");
        }

        Directory.CreateDirectory(appPaths.ToolDirectory);
        string temporaryPath = $"{appPaths.CloudflaredPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.download";
        log($"cloudflared was not found; downloading official Windows amd64 binary. Url: {CloudflaredWindowsAmd64DownloadUrl}.", null);

        using HttpClient httpClient = new()
        {
            Timeout = TimeSpan.FromMinutes(3)
        };
        using HttpResponseMessage response = await httpClient
            .GetAsync(CloudflaredWindowsAmd64DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using (Stream downloadStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (FileStream fileStream = new(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 1024 * 128,
                         useAsync: true))
        {
            await downloadStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
        }

        FileInfo downloadedFile = new(temporaryPath);
        if (!downloadedFile.Exists || downloadedFile.Length < 1024 * 1024)
        {
            throw new IOException("Downloaded cloudflared executable is missing or too small.");
        }

        File.Move(temporaryPath, appPaths.CloudflaredPath, overwrite: true);
        log($"cloudflared downloaded. Path: {appPaths.CloudflaredPath}. Bytes: {downloadedFile.Length}.", null);
        return appPaths.CloudflaredPath;
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

    private static async Task<(Process Process, string TunnelBaseUrl)> StartQuickTunnelAsync(
        string cloudflaredPath,
        string localOrigin,
        Action<string, Exception?> log,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = cloudflaredPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("tunnel");
        startInfo.ArgumentList.Add("--no-autoupdate");
        startInfo.ArgumentList.Add("--url");
        startInfo.ArgumentList.Add(localOrigin);

        Process process = new()
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        ConcurrentQueue<string> outputLines = new();
        TaskCompletionSource<string> tunnelUrlSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!process.Start())
        {
            throw new InvalidOperationException("cloudflared process could not be started.");
        }

        _ = Task.Run(() => ReadCloudflaredLinesAsync(process.StandardOutput, outputLines, tunnelUrlSource, log));
        _ = Task.Run(() => ReadCloudflaredLinesAsync(process.StandardError, outputLines, tunnelUrlSource, log));

        try
        {
            Task<string> tunnelUrlTask = tunnelUrlSource.Task.WaitAsync(TimeSpan.FromSeconds(45), cancellationToken);
            Task processExitTask = process.WaitForExitAsync(cancellationToken);
            Task completedTask = await Task.WhenAny(tunnelUrlTask, processExitTask).ConfigureAwait(false);
            if (ReferenceEquals(completedTask, tunnelUrlTask))
            {
                string tunnelUrl = await tunnelUrlTask.ConfigureAwait(false);
                log($"cloudflared quick tunnel is ready. Url: {tunnelUrl}.", null);
                return (process, tunnelUrl);
            }

            string outputTail = string.Join(" | ", outputLines.TakeLast(12));
            throw new InvalidOperationException($"cloudflared exited before a tunnel URL was reported. Output: {outputTail}");
        }
        catch
        {
            StopProcess(process, log);
            throw;
        }
    }

    private static async Task ReadCloudflaredLinesAsync(
        StreamReader reader,
        ConcurrentQueue<string> outputLines,
        TaskCompletionSource<string> tunnelUrlSource,
        Action<string, Exception?> log)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                outputLines.Enqueue(line.Trim());
                while (outputLines.Count > 30 && outputLines.TryDequeue(out _))
                {
                }

                Match match = TryCloudflareUrlRegex.Match(line);
                if (match.Success)
                {
                    tunnelUrlSource.TrySetResult(match.Value);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            log("cloudflared output reader stopped.", exception);
        }
    }

    private static void StopProcess(Process process, Action<string, Exception?> log)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            log("cloudflared process could not be stopped cleanly.", exception);
        }
        finally
        {
            process.Dispose();
        }
    }
}
