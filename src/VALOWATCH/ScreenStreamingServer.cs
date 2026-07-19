using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace VALOWATCH;

internal sealed class ScreenStreamingServer : IAsyncDisposable, IDisposable
{
    private static readonly TimeSpan FrameCacheDuration = TimeSpan.FromMilliseconds(500);

    private readonly TcpListener listener;
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private readonly SemaphoreSlim frameSemaphore = new(1, 1);
    private readonly Action<string, Exception?> log;
    private readonly string token;
    private FullScreenScreenshotFrame? cachedFrame;
    private DateTimeOffset cachedFrameAtUtc = DateTimeOffset.MinValue;
    private Task? acceptTask;

    private ScreenStreamingServer(
        TcpListener listener,
        ScreenCaptureTarget target,
        Action<string, Exception?> log)
    {
        this.listener = listener;
        Target = target;
        this.log = log;
        token = Guid.NewGuid().ToString("N");
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        LocalOrigin = $"http://127.0.0.1:{port}";
        PublicPath = $"watch/{token}";
    }

    public ScreenCaptureTarget Target { get; }

    public string LocalOrigin { get; }

    public string PublicPath { get; }

    public static ScreenStreamingServer Start(ScreenCaptureTarget target, Action<string, Exception?> log)
    {
        FullScreenScreenshotCapture.ValidateCaptureTarget(target);
        TcpListener listener = new(IPAddress.Loopback, port: 0);
        listener.Start();
        ScreenStreamingServer server = new(listener, target, log);
        server.acceptTask = Task.Run(server.AcceptLoopAsync);
        server.log(
            $"Screen streaming server started. Target: {ScreenCaptureTargetNames.ToOptionValue(target)}. LocalOrigin: {server.LocalOrigin}.",
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

    private async Task WriteHtmlResponseAsync(NetworkStream networkStream, CancellationToken cancellationToken)
    {
        string targetName = WebUtility.HtmlEncode(ScreenCaptureTargetNames.ToOptionValue(Target));
        string framePath = $"/{PublicPath}/frame.jpg";
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
<img id="screen" alt="VALOWATCH stream">
<div id="status">VALOWATCH stream: {{targetName}}</div>
<script>
const screenImage = document.getElementById('screen');
function refreshFrame() {
  screenImage.src = '{{framePath}}?t=' + Date.now();
  setTimeout(refreshFrame, 500);
}
refreshFrame();
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

    private async Task<FullScreenScreenshotFrame> GetFrameAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (cachedFrame is not null && nowUtc - cachedFrameAtUtc < FrameCacheDuration)
        {
            return cachedFrame;
        }

        await frameSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            nowUtc = DateTimeOffset.UtcNow;
            if (cachedFrame is not null && nowUtc - cachedFrameAtUtc < FrameCacheDuration)
            {
                return cachedFrame;
            }

            FullScreenScreenshotFrame nextFrame = await Task
                .Run(() => FullScreenScreenshotCapture.CaptureToJpegBytes(Target), cancellationToken)
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
}
