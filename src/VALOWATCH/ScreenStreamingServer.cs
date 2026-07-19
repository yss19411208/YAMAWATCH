using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace VALOWATCH;

internal sealed class ScreenStreamingServer : IAsyncDisposable, IDisposable
{
    public const int MinimumFramesPerSecond = 1;
    public const int DefaultFramesPerSecond = 15;
    public const int MaximumFramesPerSecond = 60;
    public const long MinimumJpegQuality = 30L;
    public const long DefaultJpegQuality = 65L;
    public const long MaximumJpegQuality = 95L;
    public const int MinimumMaxWidth = 320;
    public const int DefaultMaxWidth = 960;
    public const int MaximumMaxWidth = 3840;
    private const string MjpegBoundary = "valowatchframe";

    private readonly TcpListener listener;
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private readonly SemaphoreSlim frameSemaphore = new(1, 1);
    private readonly Action<string, Exception?> log;
    private readonly string token;
    private readonly TimeSpan frameInterval;
    private readonly ScreenCapturePlan capturePlan;
    private FullScreenScreenshotFrame? cachedFrame;
    private DateTimeOffset cachedFrameAtUtc = DateTimeOffset.MinValue;
    private Task? acceptTask;

    private ScreenStreamingServer(
        TcpListener listener,
        ScreenStreamOptions options,
        ScreenCapturePlan capturePlan,
        Action<string, Exception?> log)
    {
        this.listener = listener;
        Options = options;
        this.capturePlan = capturePlan;
        this.log = log;
        token = Guid.NewGuid().ToString("N");
        frameInterval = TimeSpan.FromTicks(Math.Max(1L, TimeSpan.TicksPerSecond / options.FramesPerSecond));
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        LocalOrigin = $"http://127.0.0.1:{port}";
        PublicPath = $"watch/{token}";
    }

    public ScreenStreamOptions Options { get; }

    public ScreenCaptureTarget Target => Options.Target;

    public int FramesPerSecond => Options.FramesPerSecond;

    public long JpegQuality => Options.JpegQuality;

    public int MaxWidth => Options.MaxWidth;

    public string LocalOrigin { get; }

    public string PublicPath { get; }

    public static ScreenStreamingServer Start(ScreenCaptureTarget target, Action<string, Exception?> log)
    {
        return Start(ScreenStreamOptions.Create(target), log);
    }

    public static ScreenStreamingServer Start(ScreenStreamOptions options, Action<string, Exception?> log)
    {
        ScreenCapturePlan capturePlan = FullScreenScreenshotCapture.CreateCapturePlan(options.Target, options.MaxWidth);
        TcpListener listener = new(IPAddress.Loopback, port: 0);
        listener.Start();
        ScreenStreamingServer server = new(listener, options, capturePlan, log);
        server.acceptTask = Task.Run(server.AcceptLoopAsync);
        server.log(
            $"Screen streaming server started. Target: {ScreenCaptureTargetNames.ToOptionValue(options.Target)}. " +
            $"FPS: {options.FramesPerSecond}. Quality: {options.JpegQuality}. MaxWidth: {options.MaxWidth}. " +
            $"Output: {capturePlan.OutputSize.Width}x{capturePlan.OutputSize.Height}. " +
            $"LocalOrigin: {server.LocalOrigin}.",
            null);
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        await cancellationTokenSource.CancelAsync().ConfigureAwait(false);
        listener.Stop();
        if (acceptTask is not null)
        {
            try
            {
                await acceptTask.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is ObjectDisposedException or SocketException)
            {
            }
        }

        cancellationTokenSource.Dispose();
        frameSemaphore.Dispose();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private async Task AcceptLoopAsync()
    {
        CancellationToken cancellationToken = cancellationTokenSource.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is ObjectDisposedException or SocketException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                log("Screen streaming server accept loop stopped unexpectedly.", exception);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        await using NetworkStream networkStream = client.GetStream();
        using (client)
        {
            string requestPath;
            try
            {
                requestPath = await ReadRequestPathAsync(networkStream, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                log("Screen streaming server received an invalid request.", exception);
                return;
            }

            if (IsWatchPagePath(requestPath))
            {
                await WriteHtmlResponseAsync(networkStream, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (IsFramePath(requestPath))
            {
                await WriteFrameResponseAsync(networkStream, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (IsMjpegPath(requestPath))
            {
                await WriteMjpegStreamResponseAsync(networkStream, cancellationToken).ConfigureAwait(false);
                return;
            }

            await WritePlainTextResponseAsync(
                networkStream,
                statusCode: 404,
                reasonPhrase: "Not Found",
                "Not found",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadRequestPathAsync(
        NetworkStream networkStream,
        CancellationToken cancellationToken)
    {
        using StreamReader reader = new(
            networkStream,
            Encoding.ASCII,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 8192,
            leaveOpen: true);

        string? requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(requestLine))
        {
            throw new InvalidDataException("HTTP request line was empty.");
        }

        while (true)
        {
            string? headerLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (headerLine is null || headerLine.Length == 0)
            {
                break;
            }
        }

        string[] requestParts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (requestParts.Length < 2 || !requestParts[0].Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Unsupported HTTP request line: {requestLine}");
        }

        string pathAndQuery = requestParts[1];
        int queryIndex = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        return queryIndex >= 0 ? pathAndQuery[..queryIndex] : pathAndQuery;
    }

    private bool IsWatchPagePath(string requestPath)
    {
        return string.Equals(requestPath, $"/{PublicPath}", StringComparison.Ordinal) ||
            string.Equals(requestPath, $"/{PublicPath}/", StringComparison.Ordinal);
    }

    private bool IsFramePath(string requestPath)
    {
        return string.Equals(requestPath, $"/{PublicPath}/frame.jpg", StringComparison.Ordinal);
    }

    private bool IsMjpegPath(string requestPath)
    {
        return string.Equals(requestPath, $"/{PublicPath}/stream.mjpg", StringComparison.Ordinal);
    }

    private async Task WriteHtmlResponseAsync(NetworkStream networkStream, CancellationToken cancellationToken)
    {
        string targetName = WebUtility.HtmlEncode(ScreenCaptureTargetNames.ToOptionValue(Target));
        string fpsText = WebUtility.HtmlEncode(FramesPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture));
        string widthText = WebUtility.HtmlEncode(MaxWidth.ToString(System.Globalization.CultureInfo.InvariantCulture));
        string framePath = $"/{PublicPath}/frame.jpg";
        string streamPath = $"/{PublicPath}/stream.mjpg";
        string html = $$"""
<!doctype html>
<html lang="ja">
<head>
<meta charset="utf-8">
<meta name="robots" content="noindex,nofollow">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>VALOWATCH Stream</title>
<style>
html,body{margin:0;width:100%;height:100%;background:#050505;color:#eee;font-family:system-ui,sans-serif;overflow:hidden}
#screen{width:100vw;height:100vh;object-fit:contain;background:#050505;display:block}
#status{position:fixed;left:12px;bottom:10px;padding:6px 8px;background:rgba(0,0,0,.55);border-radius:6px;font-size:12px}
</style>
</head>
<body>
<img id="screen" src="{{streamPath}}" alt="VALOWATCH stream">
<div id="status">VALOWATCH stream: {{targetName}} / {{fpsText}}fps / width {{widthText}}</div>
<script>
const screenImage = document.getElementById('screen');
screenImage.onerror = () => {
  screenImage.src = '{{framePath}}?t=' + Date.now();
};
</script>
</body>
</html>
""";

        await WriteResponseHeaderAsync(
            networkStream,
            200,
            "OK",
            "text/html; charset=utf-8",
            Encoding.UTF8.GetByteCount(html),
            cancellationToken).ConfigureAwait(false);
        await networkStream
            .WriteAsync(Encoding.UTF8.GetBytes(html), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task WriteFrameResponseAsync(NetworkStream networkStream, CancellationToken cancellationToken)
    {
        try
        {
            FullScreenScreenshotFrame frame = await GetFrameAsync(cancellationToken).ConfigureAwait(false);
            await WriteResponseHeaderAsync(
                networkStream,
                200,
                "OK",
                "image/jpeg",
                frame.JpegBytes.Length,
                cancellationToken).ConfigureAwait(false);
            await networkStream.WriteAsync(frame.JpegBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or ExternalException)
        {
            log("Screen stream frame capture failed.", exception);
            await WritePlainTextResponseAsync(
                networkStream,
                statusCode: 503,
                reasonPhrase: "Service Unavailable",
                $"Frame capture failed: {exception.Message}",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteMjpegStreamResponseAsync(NetworkStream networkStream, CancellationToken cancellationToken)
    {
        int sentFrames = 0;
        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
        try
        {
            await WriteMjpegResponseHeaderAsync(networkStream, cancellationToken).ConfigureAwait(false);
            DateTimeOffset nextFrameAtUtc = DateTimeOffset.UtcNow;
            while (!cancellationToken.IsCancellationRequested)
            {
                DateTimeOffset frameStartedAtUtc = DateTimeOffset.UtcNow;
                FullScreenScreenshotFrame frame = await GetFrameAsync(cancellationToken).ConfigureAwait(false);
                await WriteMjpegFrameAsync(networkStream, frame, cancellationToken).ConfigureAwait(false);
                sentFrames++;

                nextFrameAtUtc = nextFrameAtUtc <= frameStartedAtUtc
                    ? frameStartedAtUtc + frameInterval
                    : nextFrameAtUtc + frameInterval;
                TimeSpan delay = nextFrameAtUtc - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or ExternalException)
        {
            log("Screen stream MJPEG connection stopped.", exception);
        }
        finally
        {
            double elapsedSeconds = Math.Max(0.001D, (DateTimeOffset.UtcNow - startedAtUtc).TotalSeconds);
            log(
                "Screen stream MJPEG connection closed. " +
                $"Target: {ScreenCaptureTargetNames.ToOptionValue(Target)}. " +
                $"ConfiguredFPS: {FramesPerSecond}. MaxWidth: {MaxWidth}. SentFrames: {sentFrames}. " +
                $"AverageFPS: {(sentFrames / elapsedSeconds).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}.",
                null);
        }
    }

    private static async Task WriteMjpegFrameAsync(
        NetworkStream networkStream,
        FullScreenScreenshotFrame frame,
        CancellationToken cancellationToken)
    {
        string partHeader =
            $"--{MjpegBoundary}\r\n" +
            "Content-Type: image/jpeg\r\n" +
            $"Content-Length: {frame.JpegBytes.Length}\r\n" +
            "\r\n";
        await networkStream
            .WriteAsync(Encoding.ASCII.GetBytes(partHeader), cancellationToken)
            .ConfigureAwait(false);
        await networkStream.WriteAsync(frame.JpegBytes, cancellationToken).ConfigureAwait(false);
        await networkStream
            .WriteAsync(Encoding.ASCII.GetBytes("\r\n"), cancellationToken)
            .ConfigureAwait(false);
        await networkStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<FullScreenScreenshotFrame> GetFrameAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (cachedFrame is not null && nowUtc - cachedFrameAtUtc < frameInterval)
        {
            return cachedFrame;
        }

        await frameSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            nowUtc = DateTimeOffset.UtcNow;
            if (cachedFrame is not null && nowUtc - cachedFrameAtUtc < frameInterval)
            {
                return cachedFrame;
            }

            FullScreenScreenshotFrame nextFrame = await Task
                .Run(() => FullScreenScreenshotCapture.CaptureToJpegBytes(capturePlan, JpegQuality), cancellationToken)
                .ConfigureAwait(false);
            cachedFrame = nextFrame;
            cachedFrameAtUtc = DateTimeOffset.UtcNow;
            return nextFrame;
        }
        finally
        {
            frameSemaphore.Release();
        }
    }

    private static async Task WritePlainTextResponseAsync(
        NetworkStream networkStream,
        int statusCode,
        string reasonPhrase,
        string body,
        CancellationToken cancellationToken)
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        await WriteResponseHeaderAsync(
            networkStream,
            statusCode,
            reasonPhrase,
            "text/plain; charset=utf-8",
            bodyBytes.Length,
            cancellationToken).ConfigureAwait(false);
        await networkStream.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteResponseHeaderAsync(
        NetworkStream networkStream,
        int statusCode,
        string reasonPhrase,
        string contentType,
        int contentLength,
        CancellationToken cancellationToken)
    {
        string header =
            $"HTTP/1.1 {statusCode} {reasonPhrase}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {contentLength}\r\n" +
            "Cache-Control: no-store, no-cache, must-revalidate\r\n" +
            "Pragma: no-cache\r\n" +
            "X-Robots-Tag: noindex, nofollow\r\n" +
            "Connection: close\r\n" +
            "\r\n";
        await networkStream
            .WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WriteMjpegResponseHeaderAsync(
        NetworkStream networkStream,
        CancellationToken cancellationToken)
    {
        string header =
            "HTTP/1.1 200 OK\r\n" +
            $"Content-Type: multipart/x-mixed-replace; boundary={MjpegBoundary}\r\n" +
            "Cache-Control: no-store, no-cache, must-revalidate\r\n" +
            "Pragma: no-cache\r\n" +
            "X-Robots-Tag: noindex, nofollow\r\n" +
            "Connection: close\r\n" +
            "\r\n";
        await networkStream
            .WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken)
            .ConfigureAwait(false);
    }
}

internal readonly record struct ScreenStreamOptions(
    ScreenCaptureTarget Target,
    int FramesPerSecond,
    long JpegQuality,
    int MaxWidth)
{
    public static ScreenStreamOptions Create(
        ScreenCaptureTarget target,
        int framesPerSecond = ScreenStreamingServer.DefaultFramesPerSecond,
        long jpegQuality = ScreenStreamingServer.DefaultJpegQuality,
        int maxWidth = ScreenStreamingServer.DefaultMaxWidth)
    {
        return new ScreenStreamOptions(
            target,
            NormalizeFramesPerSecond(framesPerSecond),
            NormalizeJpegQuality(jpegQuality),
            NormalizeMaxWidth(maxWidth));
    }

    public static int NormalizeFramesPerSecond(int framesPerSecond)
    {
        return Math.Clamp(
            framesPerSecond,
            ScreenStreamingServer.MinimumFramesPerSecond,
            ScreenStreamingServer.MaximumFramesPerSecond);
    }

    public static long NormalizeJpegQuality(long jpegQuality)
    {
        return Math.Clamp(
            jpegQuality,
            ScreenStreamingServer.MinimumJpegQuality,
            ScreenStreamingServer.MaximumJpegQuality);
    }

    public static int NormalizeMaxWidth(int maxWidth)
    {
        return Math.Clamp(
            maxWidth,
            ScreenStreamingServer.MinimumMaxWidth,
            ScreenStreamingServer.MaximumMaxWidth);
    }
}
