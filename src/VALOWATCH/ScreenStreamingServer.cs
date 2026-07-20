using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace VALOWATCH;

internal sealed class ScreenStreamingServer : IAsyncDisposable, IDisposable
{
    public const int MinimumFramesPerSecond = 1;
    public const int DefaultFramesPerSecond = 60;
    public const int MaximumFramesPerSecond = 120;
    public const long MinimumJpegQuality = 30L;
    public const long DefaultJpegQuality = 90L;
    public const long MaximumJpegQuality = 95L;
    public const int MinimumMaxWidth = 320;
    public const int DefaultMaxWidth = 1920;
    public const int MaximumMaxWidth = 3840;
    public const double H264Fmp4TargetLatencySeconds = 0.8D;
    public const double H264Fmp4CatchUpLatencySeconds = 1.0D;
    public const double H264Fmp4MaximumLatencySeconds = 2.65D;
    public const int H264Fmp4LatencyCheckIntervalMilliseconds = 100;
    public const int H264Fmp4SeekCooldownMilliseconds = 1400;
    public const int H264Fmp4ReconnectStallMilliseconds = 1800;
    public const int H264Fmp4FragmentDurationMicroseconds = 200000;
    public const int H264Fmp4MinimumFragmentDurationMicroseconds = 100000;
    public const double H264KeyframeIntervalSeconds = 0.5D;
    private const string MjpegBoundary = "valowatchframe";
    public const int MjpegFfmpegThresholdFramesPerSecond = 60;
    private const int FrameCaptureRetryCount = 3;
    private static readonly TimeSpan FrameCaptureRetryDelay = TimeSpan.FromMilliseconds(8);

    private readonly TcpListener listener;
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private readonly SemaphoreSlim frameSemaphore = new(1, 1);
    private readonly Action<string, Exception?> log;
    private readonly string token;
    private readonly TimeSpan frameInterval;
    private readonly ScreenCapturePlan capturePlan;
    private readonly string? ffmpegPath;
    private readonly string streamWorkDirectory;
    private readonly string? hlsDirectory;
    private Process? hlsProcess;
    private FullScreenScreenshotFrame? cachedFrame;
    private DateTimeOffset cachedFrameAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset lastCaptureFailureLoggedAtUtc = DateTimeOffset.MinValue;
    private Task? acceptTask;

    private ScreenStreamingServer(
        TcpListener listener,
        ScreenStreamOptions options,
        ScreenCapturePlan capturePlan,
        string? ffmpegPath,
        string streamWorkDirectory,
        Action<string, Exception?> log)
    {
        this.listener = listener;
        Options = options;
        this.capturePlan = capturePlan;
        this.ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? null : ffmpegPath;
        this.streamWorkDirectory = streamWorkDirectory;
        this.log = log;
        token = Guid.NewGuid().ToString("N");
        hlsDirectory = options.Method == ScreenStreamMethod.H264Hls
            ? Path.Combine(streamWorkDirectory, $"hls-{token}")
            : null;
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

    public ScreenStreamMethod Method => Options.Method;

    public string EngineName => Options.Method switch
    {
        ScreenStreamMethod.Mjpeg when ffmpegPath is null => "dotnet-mjpeg",
        ScreenStreamMethod.Mjpeg => "ffmpeg-mjpeg",
        ScreenStreamMethod.H264Fmp4 => "ffmpeg-h264-fmp4",
        ScreenStreamMethod.H264Hls => "ffmpeg-h264-hls",
        _ => "unknown"
    };

    public string LocalOrigin { get; }

    public string PublicPath { get; }

    public static ScreenStreamingServer Start(ScreenCaptureTarget target, Action<string, Exception?> log)
    {
        return Start(ScreenStreamOptions.Create(target, method: ScreenStreamMethod.Mjpeg), log);
    }

    public static ScreenStreamingServer Start(ScreenStreamOptions options, Action<string, Exception?> log)
    {
        return Start(options, ffmpegPath: null, log);
    }

    public static ScreenStreamingServer Start(ScreenStreamOptions options, string? ffmpegPath, Action<string, Exception?> log)
    {
        string defaultWorkDirectory = Path.Combine(Path.GetTempPath(), "VALOWATCH", "streaming");
        return Start(options, ffmpegPath, defaultWorkDirectory, log);
    }

    public static ScreenStreamingServer Start(
        ScreenStreamOptions options,
        string? ffmpegPath,
        string streamWorkDirectory,
        Action<string, Exception?> log)
    {
        ScreenCapturePlan capturePlan = FullScreenScreenshotCapture.CreateCapturePlan(options.Target, options.MaxWidth);
        if (options.RequiresFfmpeg && string.IsNullOrWhiteSpace(ffmpegPath))
        {
            throw new InvalidOperationException($"FFmpeg is required for stream method {ScreenStreamMethodNames.ToOptionValue(options.Method)}.");
        }

        Directory.CreateDirectory(streamWorkDirectory);
        TcpListener listener = new(IPAddress.Loopback, port: 0);
        listener.Start();
        ScreenStreamingServer server = new(listener, options, capturePlan, ffmpegPath, streamWorkDirectory, log);
        if (options.Method == ScreenStreamMethod.H264Hls)
        {
            server.StartHlsEncoder();
        }

        server.acceptTask = Task.Run(server.AcceptLoopAsync);
        server.log(
            $"Screen streaming server started. Target: {ScreenCaptureTargetNames.ToOptionValue(options.Target)}. " +
            $"FPS: {options.FramesPerSecond}. Quality: {options.JpegQuality}. MaxWidth: {options.MaxWidth}. " +
            $"Output: {capturePlan.OutputSize.Width}x{capturePlan.OutputSize.Height}. " +
            $"Engine: {server.EngineName}. " +
            $"LocalOrigin: {server.LocalOrigin}.",
            null);
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        await cancellationTokenSource.CancelAsync().ConfigureAwait(false);
        listener.Stop();
        if (hlsProcess is not null)
        {
            StopProcess(hlsProcess, "ffmpeg hls", log);
            hlsProcess = null;
        }

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

        TryDeleteHlsDirectory();
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
        client.NoDelay = true;
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

            if (IsFmp4Path(requestPath))
            {
                await WriteFmp4StreamResponseAsync(networkStream, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (IsHlsPlaylistPath(requestPath))
            {
                await WriteHlsFileResponseAsync(
                    networkStream,
                    Path.Combine(hlsDirectory ?? string.Empty, "stream.m3u8"),
                    "application/vnd.apple.mpegurl",
                    rewritePlaylist: true,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (TryGetHlsSegmentFilePath(requestPath, out string hlsSegmentPath))
            {
                await WriteHlsFileResponseAsync(
                    networkStream,
                    hlsSegmentPath,
                    "video/mp2t",
                    rewritePlaylist: false,
                    cancellationToken).ConfigureAwait(false);
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

    private bool IsFmp4Path(string requestPath)
    {
        return string.Equals(requestPath, $"/{PublicPath}/stream.mp4", StringComparison.Ordinal);
    }

    private bool IsHlsPlaylistPath(string requestPath)
    {
        return string.Equals(requestPath, $"/{PublicPath}/stream.m3u8", StringComparison.Ordinal);
    }

    private bool TryGetHlsSegmentFilePath(string requestPath, out string segmentFilePath)
    {
        segmentFilePath = string.Empty;
        if (hlsDirectory is null)
        {
            return false;
        }

        string prefix = $"/{PublicPath}/hls/";
        if (!requestPath.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string segmentFileName = WebUtility.UrlDecode(requestPath[prefix.Length..]);
        if (string.IsNullOrWhiteSpace(segmentFileName) ||
            segmentFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !segmentFileName.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        segmentFilePath = Path.Combine(hlsDirectory, segmentFileName);
        return true;
    }

    private async Task WriteHtmlResponseAsync(NetworkStream networkStream, CancellationToken cancellationToken)
    {
        string targetName = WebUtility.HtmlEncode(ScreenCaptureTargetNames.ToOptionValue(Target));
        string fpsText = WebUtility.HtmlEncode(FramesPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture));
        string widthText = WebUtility.HtmlEncode(MaxWidth.ToString(System.Globalization.CultureInfo.InvariantCulture));
        string engineText = WebUtility.HtmlEncode(EngineName);
        string framePath = $"/{PublicPath}/frame.jpg";
        string streamPath = Options.Method switch
        {
            ScreenStreamMethod.H264Fmp4 => $"/{PublicPath}/stream.mp4",
            ScreenStreamMethod.H264Hls => $"/{PublicPath}/stream.m3u8",
            _ => $"/{PublicPath}/stream.mjpg"
        };
        string streamPathHtml = WebUtility.HtmlEncode(streamPath);
        string streamPathJavaScript = streamPath.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
        string mediaElement = Options.Method == ScreenStreamMethod.Mjpeg
            ? $"<img id=\"screen\" src=\"{streamPathHtml}\" alt=\"VALOWATCH stream\">"
            : Options.Method == ScreenStreamMethod.H264Hls
                ? "<video id=\"screen\" autoplay muted playsinline></video>"
                : $"<video id=\"screen\" src=\"{streamPathHtml}\" autoplay muted playsinline></video>";
        string targetLatencySecondsText = H264Fmp4TargetLatencySeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        string catchUpLatencySecondsText = H264Fmp4CatchUpLatencySeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        string maximumLatencySecondsText = H264Fmp4MaximumLatencySeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        string latencyCheckIntervalMillisecondsText = H264Fmp4LatencyCheckIntervalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string seekCooldownMillisecondsText = H264Fmp4SeekCooldownMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string reconnectStallMillisecondsText = H264Fmp4ReconnectStallMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string mediaScript = Options.Method switch
        {
            ScreenStreamMethod.H264Hls => $$"""
<script src="https://cdn.jsdelivr.net/npm/hls.js@1"></script>
<script>
const screenVideo = document.getElementById('screen');
const streamPath = '{{streamPathJavaScript}}';
if (window.Hls && Hls.isSupported()) {
  const hls = new Hls({ lowLatencyMode: true, liveSyncDurationCount: 2, maxLiveSyncPlaybackRate: 1.5 });
  hls.loadSource(streamPath);
  hls.attachMedia(screenVideo);
} else if (screenVideo.canPlayType('application/vnd.apple.mpegurl') || screenVideo.canPlayType('application/x-mpegURL')) {
  screenVideo.src = streamPath;
} else {
  document.getElementById('status').textContent += ' / HLS browser support is required';
}
</script>
""",
            ScreenStreamMethod.H264Fmp4 => $$"""
<script>
const screenVideo = document.getElementById('screen');
const streamPath = '{{streamPathJavaScript}}';
const targetLatencySeconds = {{targetLatencySecondsText}};
const catchUpLatencySeconds = {{catchUpLatencySecondsText}};
const maximumLatencySeconds = {{maximumLatencySecondsText}};
const latencyCheckMilliseconds = {{latencyCheckIntervalMillisecondsText}};
const seekCooldownMilliseconds = {{seekCooldownMillisecondsText}};
const reconnectStallMilliseconds = {{reconnectStallMillisecondsText}};
let lastSeekAtMilliseconds = -seekCooldownMilliseconds;
let lastPlayAttemptAtMilliseconds = 0;
let lastReconnectAtMilliseconds = 0;
let lastPlaybackTime = 0;
let lastPlaybackMovedAtMilliseconds = performance.now();
let reconnectNonce = 0;
let frameMonitorScheduled = false;
let hiddenStreamSuspended = false;
screenVideo.preload = 'auto';
screenVideo.controls = false;
screenVideo.muted = true;
screenVideo.defaultPlaybackRate = 1;
function isFmp4PageHidden() {
  return document.hidden || document.visibilityState === 'hidden';
}
function suspendFmp4ForHiddenPage() {
  if (!isFmp4PageHidden()) {
    return false;
  }

  const nowMilliseconds = performance.now();
  lastPlaybackMovedAtMilliseconds = nowMilliseconds;
  if (hiddenStreamSuspended) {
    return true;
  }

  hiddenStreamSuspended = true;
  screenVideo.playbackRate = 1;
  try {
    screenVideo.pause();
    screenVideo.removeAttribute('src');
    screenVideo.load();
  } catch {
  }

  return true;
}
function resumeFmp4FromHiddenPage() {
  if (isFmp4PageHidden()) {
    return;
  }

  const shouldReconnect = hiddenStreamSuspended || !screenVideo.currentSrc;
  hiddenStreamSuspended = false;
  frameMonitorScheduled = false;
  lastPlaybackMovedAtMilliseconds = performance.now();
  try {
    lastPlaybackTime = screenVideo.currentTime;
  } catch {
    lastPlaybackTime = 0;
  }
  if (shouldReconnect) {
    reconnectFmp4Stream(true);
    return;
  }

  keepFmp4Playing(true);
  keepFmp4LatencyLow();
}
function markFmp4PlaybackMovement(nowMilliseconds) {
  if (Math.abs(screenVideo.currentTime - lastPlaybackTime) > 0.015) {
    lastPlaybackMovedAtMilliseconds = nowMilliseconds;
    lastPlaybackTime = screenVideo.currentTime;
  }
}
function readFmp4LatencySeconds() {
  if (!screenVideo.buffered || screenVideo.buffered.length === 0) {
    return null;
  }

  const bufferedEnd = screenVideo.buffered.end(screenVideo.buffered.length - 1);
  const latencySeconds = bufferedEnd - screenVideo.currentTime;
  if (!Number.isFinite(latencySeconds) || latencySeconds < 0) {
    return null;
  }

  return { bufferedEnd, latencySeconds };
}
function seekFmp4NearLiveEdge(bufferedEnd, nowMilliseconds) {
  if (nowMilliseconds - lastSeekAtMilliseconds < seekCooldownMilliseconds) {
    return false;
  }

  const correctedTime = Math.max(0, bufferedEnd - targetLatencySeconds);
  try {
    if (typeof screenVideo.fastSeek === 'function') {
      screenVideo.fastSeek(correctedTime);
    } else {
      screenVideo.currentTime = correctedTime;
    }

    lastSeekAtMilliseconds = nowMilliseconds;
    lastPlaybackMovedAtMilliseconds = nowMilliseconds;
    lastPlaybackTime = correctedTime;
    return true;
  } catch {
    return false;
  }
}
function reconnectFmp4Stream(forceReconnect = false) {
  const nowMilliseconds = performance.now();
  if (suspendFmp4ForHiddenPage()) {
    return;
  }

  if (!forceReconnect &&
      nowMilliseconds - lastReconnectAtMilliseconds < Math.max(3000, reconnectStallMilliseconds)) {
    return;
  }

  lastReconnectAtMilliseconds = nowMilliseconds;
  lastPlaybackMovedAtMilliseconds = nowMilliseconds;
  reconnectNonce += 1;
  screenVideo.playbackRate = 1;
  try {
    screenVideo.pause();
    screenVideo.removeAttribute('src');
    screenVideo.load();
  } catch {
  }

  screenVideo.src = streamPath + '?r=' + reconnectNonce + '&t=' + Date.now();
  try {
    screenVideo.load();
  } catch {
  }

  keepFmp4Playing(true);
}
function keepFmp4LatencyLow() {
  if (suspendFmp4ForHiddenPage()) {
    return;
  }

  const nowMilliseconds = performance.now();
  markFmp4PlaybackMovement(nowMilliseconds);
  const latencyState = readFmp4LatencySeconds();
  if (!latencyState) {
    if (!screenVideo.paused && nowMilliseconds - lastPlaybackMovedAtMilliseconds > reconnectStallMilliseconds) {
      reconnectFmp4Stream();
    }

    return;
  }

  const { bufferedEnd, latencySeconds } = latencyState;
  if (latencySeconds > maximumLatencySeconds) {
    const seeked = seekFmp4NearLiveEdge(bufferedEnd, nowMilliseconds);
    screenVideo.playbackRate = seeked ? 1 : 1.35;
    return;
  }

  if (!screenVideo.paused &&
      screenVideo.readyState < 3 &&
      nowMilliseconds - lastPlaybackMovedAtMilliseconds > reconnectStallMilliseconds) {
    reconnectFmp4Stream();
    return;
  }

  if (latencySeconds > catchUpLatencySeconds + 0.9) {
    screenVideo.playbackRate = 1.25;
  } else if (latencySeconds > catchUpLatencySeconds + 0.35) {
    screenVideo.playbackRate = 1.15;
  } else if (latencySeconds > catchUpLatencySeconds) {
    screenVideo.playbackRate = 1.06;
  } else {
    screenVideo.playbackRate = 1;
  }
}
async function keepFmp4Playing(forcePlay = false) {
  if (isFmp4PageHidden()) {
    return;
  }

  const nowMilliseconds = performance.now();
  if (!forcePlay && !screenVideo.paused && !screenVideo.ended) {
    return;
  }

  if (!forcePlay && nowMilliseconds - lastPlayAttemptAtMilliseconds < 1000) {
    return;
  }

  lastPlayAttemptAtMilliseconds = nowMilliseconds;
  try {
    await screenVideo.play();
  } catch {
  }
}
function scheduleFmp4FrameMonitor() {
  if (frameMonitorScheduled || typeof screenVideo.requestVideoFrameCallback !== 'function') {
    return;
  }

  frameMonitorScheduled = true;
  screenVideo.requestVideoFrameCallback(() => {
    frameMonitorScheduled = false;
    const nowMilliseconds = performance.now();
    lastPlaybackMovedAtMilliseconds = nowMilliseconds;
    lastPlaybackTime = screenVideo.currentTime;
    keepFmp4LatencyLow();
    scheduleFmp4FrameMonitor();
  });
}
screenVideo.addEventListener('loadedmetadata', () => keepFmp4Playing(true));
screenVideo.addEventListener('loadeddata', keepFmp4LatencyLow);
screenVideo.addEventListener('canplay', keepFmp4Playing);
screenVideo.addEventListener('playing', () => {
  lastPlaybackMovedAtMilliseconds = performance.now();
  scheduleFmp4FrameMonitor();
  keepFmp4LatencyLow();
});
screenVideo.addEventListener('progress', keepFmp4LatencyLow);
screenVideo.addEventListener('timeupdate', keepFmp4LatencyLow);
screenVideo.addEventListener('waiting', () => window.setTimeout(keepFmp4LatencyLow, 250));
screenVideo.addEventListener('stalled', reconnectFmp4Stream);
screenVideo.addEventListener('seeked', keepFmp4LatencyLow);
screenVideo.addEventListener('ended', reconnectFmp4Stream);
document.addEventListener('visibilitychange', () => {
  if (isFmp4PageHidden()) {
    suspendFmp4ForHiddenPage();
  } else {
    resumeFmp4FromHiddenPage();
  }
});
window.addEventListener('pageshow', resumeFmp4FromHiddenPage);
window.addEventListener('focus', resumeFmp4FromHiddenPage);
window.addEventListener('pagehide', suspendFmp4ForHiddenPage);
window.setInterval(() => {
  if (suspendFmp4ForHiddenPage()) {
    return;
  }

  keepFmp4LatencyLow();
  if (screenVideo.paused || screenVideo.ended) {
    keepFmp4Playing();
  }
}, latencyCheckMilliseconds);
screenVideo.onerror = () => {
  reconnectFmp4Stream();
};
</script>
""",
            _ => $$"""
<script>
const screenImage = document.getElementById('screen');
screenImage.onerror = () => {
  screenImage.src = '{{framePath}}?t=' + Date.now();
};
</script>
"""
        };
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
{{mediaElement}}
<div id="status">VALOWATCH stream: {{targetName}} / {{fpsText}}fps / width {{widthText}} / {{engineText}}</div>
{{mediaScript}}
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
        if (ffmpegPath is not null)
        {
            await WriteFfmpegMjpegStreamResponseAsync(networkStream, cancellationToken).ConfigureAwait(false);
            return;
        }

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
        catch (Exception exception) when (IsExpectedClientDisconnectException(exception))
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

    private async Task WriteFfmpegMjpegStreamResponseAsync(NetworkStream networkStream, CancellationToken cancellationToken)
    {
        Process? process = null;
        long copiedBytes = 0;
        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
        try
        {
            process = StartFfmpegMjpegProcess();
            _ = Task.Run(() => ReadFfmpegErrorLinesAsync(process, cancellationToken), CancellationToken.None);
            await WriteFfmpegMjpegResponseHeaderAsync(networkStream, cancellationToken).ConfigureAwait(false);

            byte[] copyBuffer = new byte[1024 * 128];
            Stream ffmpegOutputStream = process.StandardOutput.BaseStream;
            while (!cancellationToken.IsCancellationRequested)
            {
                int readByteCount = await ffmpegOutputStream
                    .ReadAsync(copyBuffer.AsMemory(0, copyBuffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (readByteCount == 0)
                {
                    break;
                }

                await networkStream
                    .WriteAsync(copyBuffer.AsMemory(0, readByteCount), cancellationToken)
                    .ConfigureAwait(false);
                await networkStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                copiedBytes += readByteCount;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedClientDisconnectException(exception))
        {
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            log("FFmpeg screen stream connection stopped.", exception);
        }
        finally
        {
            if (process is not null)
            {
                StopProcess(process, "ffmpeg", log);
            }

            double elapsedSeconds = Math.Max(0.001D, (DateTimeOffset.UtcNow - startedAtUtc).TotalSeconds);
            log(
                "FFmpeg screen stream connection closed. " +
                $"Target: {ScreenCaptureTargetNames.ToOptionValue(Target)}. " +
                $"ConfiguredFPS: {FramesPerSecond}. MaxWidth: {MaxWidth}. CopiedBytes: {copiedBytes}. " +
                $"AverageBytesPerSecond: {(copiedBytes / elapsedSeconds).ToString("0", System.Globalization.CultureInfo.InvariantCulture)}.",
                null);
        }
    }

    private async Task WriteFmp4StreamResponseAsync(NetworkStream networkStream, CancellationToken cancellationToken)
    {
        if (Options.Method != ScreenStreamMethod.H264Fmp4)
        {
            await WritePlainTextResponseAsync(
                networkStream,
                statusCode: 404,
                reasonPhrase: "Not Found",
                "fMP4 stream is not enabled for this session.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        Process? process = null;
        long copiedBytes = 0;
        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
        try
        {
            process = StartFfmpegFmp4Process();
            _ = Task.Run(() => ReadFfmpegErrorLinesAsync(process, cancellationToken), CancellationToken.None);
            await WriteFmp4ResponseHeaderAsync(networkStream, cancellationToken).ConfigureAwait(false);

            byte[] copyBuffer = new byte[1024 * 256];
            Stream ffmpegOutputStream = process.StandardOutput.BaseStream;
            while (!cancellationToken.IsCancellationRequested)
            {
                int readByteCount = await ffmpegOutputStream
                    .ReadAsync(copyBuffer.AsMemory(0, copyBuffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (readByteCount == 0)
                {
                    break;
                }

                await networkStream
                    .WriteAsync(copyBuffer.AsMemory(0, readByteCount), cancellationToken)
                    .ConfigureAwait(false);
                await networkStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                copiedBytes += readByteCount;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedClientDisconnectException(exception))
        {
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            log("FFmpeg fMP4 screen stream connection stopped.", exception);
        }
        finally
        {
            if (process is not null)
            {
                StopProcess(process, "ffmpeg fmp4", log);
            }

            double elapsedSeconds = Math.Max(0.001D, (DateTimeOffset.UtcNow - startedAtUtc).TotalSeconds);
            log(
                "FFmpeg fMP4 screen stream connection closed. " +
                $"Target: {ScreenCaptureTargetNames.ToOptionValue(Target)}. " +
                $"ConfiguredFPS: {FramesPerSecond}. MaxWidth: {MaxWidth}. CopiedBytes: {copiedBytes}. " +
                $"AverageBytesPerSecond: {(copiedBytes / elapsedSeconds).ToString("0", System.Globalization.CultureInfo.InvariantCulture)}.",
                null);
        }
    }

    private async Task WriteHlsFileResponseAsync(
        NetworkStream networkStream,
        string filePath,
        string contentType,
        bool rewritePlaylist,
        CancellationToken cancellationToken)
    {
        if (Options.Method != ScreenStreamMethod.H264Hls)
        {
            await WritePlainTextResponseAsync(
                networkStream,
                statusCode: 404,
                reasonPhrase: "Not Found",
                "HLS stream is not enabled for this session.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            bool fileReady = await WaitForFileReadyAsync(filePath, cancellationToken).ConfigureAwait(false);
            if (!fileReady)
            {
                await WritePlainTextResponseAsync(
                    networkStream,
                    statusCode: 503,
                    reasonPhrase: "Service Unavailable",
                    "HLS stream is warming up.",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            byte[] bytes = rewritePlaylist
                ? Encoding.UTF8.GetBytes(RewriteHlsPlaylist(await ReadAllTextSharedAsync(filePath, cancellationToken).ConfigureAwait(false)))
                : await ReadAllBytesSharedAsync(filePath, cancellationToken).ConfigureAwait(false);
            await WriteResponseHeaderAsync(
                networkStream,
                200,
                "OK",
                contentType,
                bytes.Length,
                cancellationToken).ConfigureAwait(false);
            await networkStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log("HLS stream file response failed.", exception);
            await WritePlainTextResponseAsync(
                networkStream,
                statusCode: 503,
                reasonPhrase: "Service Unavailable",
                "HLS stream file is unavailable.",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private Process StartFfmpegMjpegProcess()
    {
        string executablePath = ffmpegPath ?? throw new InvalidOperationException("FFmpeg path is unavailable.");
        ProcessStartInfo startInfo = new()
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("warning");
        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("gdigrab");
        startInfo.ArgumentList.Add("-draw_mouse");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-framerate");
        startInfo.ArgumentList.Add(FramesPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-offset_x");
        startInfo.ArgumentList.Add(capturePlan.Bounds.Left.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-offset_y");
        startInfo.ArgumentList.Add(capturePlan.Bounds.Top.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-video_size");
        startInfo.ArgumentList.Add($"{capturePlan.Bounds.Width}x{capturePlan.Bounds.Height}");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add("desktop");

        if (capturePlan.OutputSize.Width != capturePlan.Bounds.Width ||
            capturePlan.OutputSize.Height != capturePlan.Bounds.Height)
        {
            startInfo.ArgumentList.Add("-vf");
            startInfo.ArgumentList.Add($"scale={capturePlan.OutputSize.Width}:{capturePlan.OutputSize.Height}:flags=fast_bilinear");
        }

        startInfo.ArgumentList.Add("-an");
        startInfo.ArgumentList.Add("-c:v");
        startInfo.ArgumentList.Add("mjpeg");
        startInfo.ArgumentList.Add("-q:v");
        startInfo.ArgumentList.Add(ToFfmpegMjpegQualityScale(JpegQuality).ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("mpjpeg");
        startInfo.ArgumentList.Add("pipe:1");

        Process process = new()
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        if (!process.Start())
        {
            throw new InvalidOperationException("FFmpeg process could not be started.");
        }

        log(
            "FFmpeg screen stream process started. " +
            $"Target: {ScreenCaptureTargetNames.ToOptionValue(Target)}. FPS: {FramesPerSecond}. " +
            $"Capture: {capturePlan.Bounds.Width}x{capturePlan.Bounds.Height}+{capturePlan.Bounds.Left}+{capturePlan.Bounds.Top}. " +
            $"Output: {capturePlan.OutputSize.Width}x{capturePlan.OutputSize.Height}.",
            null);
        return process;
    }

    private Process StartFfmpegFmp4Process()
    {
        ProcessStartInfo startInfo = CreateFfmpegStartInfo(redirectOutput: true);
        AddFfmpegCaptureInputArguments(startInfo);
        AddFfmpegH264OutputArguments(startInfo);
        startInfo.ArgumentList.Add("-movflags");
        startInfo.ArgumentList.Add("+frag_keyframe+empty_moov+default_base_moof+dash");
        startInfo.ArgumentList.Add("-frag_duration");
        startInfo.ArgumentList.Add(H264Fmp4FragmentDurationMicroseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-min_frag_duration");
        startInfo.ArgumentList.Add(H264Fmp4MinimumFragmentDurationMicroseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-muxdelay");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("-muxpreload");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("-flush_packets");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("mp4");
        startInfo.ArgumentList.Add("pipe:1");

        Process process = StartFfmpegProcess(startInfo);
        log(
            "FFmpeg fMP4 screen stream process started. " +
            $"Target: {ScreenCaptureTargetNames.ToOptionValue(Target)}. FPS: {FramesPerSecond}. " +
            $"Capture: {capturePlan.Bounds.Width}x{capturePlan.Bounds.Height}+{capturePlan.Bounds.Left}+{capturePlan.Bounds.Top}. " +
            $"Output: {capturePlan.OutputSize.Width}x{capturePlan.OutputSize.Height}. " +
            $"CRF: {ToH264Crf(JpegQuality)}. " +
            $"GopFrames: {GetH264GroupOfPicturesSize()}. " +
            $"FragmentDurationUs: {H264Fmp4FragmentDurationMicroseconds}. " +
            $"TargetLatencySeconds: {H264Fmp4TargetLatencySeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}. " +
            $"CatchUpLatencySeconds: {H264Fmp4CatchUpLatencySeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}. " +
            $"MaxLatencySeconds: {H264Fmp4MaximumLatencySeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}. " +
            $"LatencyCheckMs: {H264Fmp4LatencyCheckIntervalMilliseconds}. " +
            $"SeekCooldownMs: {H264Fmp4SeekCooldownMilliseconds}. " +
            $"ReconnectStallMs: {H264Fmp4ReconnectStallMilliseconds}.",
            null);
        return process;
    }

    private void StartHlsEncoder()
    {
        if (hlsDirectory is null)
        {
            throw new InvalidOperationException("HLS work directory is unavailable.");
        }

        Directory.CreateDirectory(hlsDirectory);
        foreach (string staleFilePath in Directory.EnumerateFiles(hlsDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                File.Delete(staleFilePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                log($"Stale HLS file could not be deleted. Path: {staleFilePath}.", exception);
            }
        }

        hlsProcess = StartFfmpegHlsProcess();
        _ = Task.Run(() => ReadFfmpegErrorLinesAsync(hlsProcess, cancellationTokenSource.Token), CancellationToken.None);
    }

    private Process StartFfmpegHlsProcess()
    {
        string directory = hlsDirectory ?? throw new InvalidOperationException("HLS work directory is unavailable.");
        string playlistPath = Path.Combine(directory, "stream.m3u8");
        string segmentPattern = Path.Combine(directory, "segment_%05d.ts");

        ProcessStartInfo startInfo = CreateFfmpegStartInfo(redirectOutput: false);
        AddFfmpegCaptureInputArguments(startInfo);
        AddFfmpegH264OutputArguments(startInfo);
        startInfo.ArgumentList.Add("-flags");
        startInfo.ArgumentList.Add("+cgop");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("hls");
        startInfo.ArgumentList.Add("-hls_time");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-hls_list_size");
        startInfo.ArgumentList.Add("6");
        startInfo.ArgumentList.Add("-hls_flags");
        startInfo.ArgumentList.Add("delete_segments+omit_endlist+independent_segments+temp_file");
        startInfo.ArgumentList.Add("-hls_segment_filename");
        startInfo.ArgumentList.Add(segmentPattern);
        startInfo.ArgumentList.Add(playlistPath);

        Process process = StartFfmpegProcess(startInfo);
        log(
            "FFmpeg HLS screen stream process started. " +
            $"Target: {ScreenCaptureTargetNames.ToOptionValue(Target)}. FPS: {FramesPerSecond}. " +
            $"Capture: {capturePlan.Bounds.Width}x{capturePlan.Bounds.Height}+{capturePlan.Bounds.Left}+{capturePlan.Bounds.Top}. " +
            $"Output: {capturePlan.OutputSize.Width}x{capturePlan.OutputSize.Height}. " +
            $"CRF: {ToH264Crf(JpegQuality)}. Playlist: {playlistPath}.",
            null);
        return process;
    }

    private ProcessStartInfo CreateFfmpegStartInfo(bool redirectOutput)
    {
        string executablePath = ffmpegPath ?? throw new InvalidOperationException("FFmpeg path is unavailable.");
        ProcessStartInfo startInfo = new()
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("warning");
        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-fflags");
        startInfo.ArgumentList.Add("nobuffer");
        startInfo.ArgumentList.Add("-probesize");
        startInfo.ArgumentList.Add("32");
        startInfo.ArgumentList.Add("-analyzeduration");
        startInfo.ArgumentList.Add("0");
        return startInfo;
    }

    private static Process StartFfmpegProcess(ProcessStartInfo startInfo)
    {
        Process process = new()
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        if (!process.Start())
        {
            throw new InvalidOperationException("FFmpeg process could not be started.");
        }

        return process;
    }

    private void AddFfmpegCaptureInputArguments(ProcessStartInfo startInfo)
    {
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("gdigrab");
        startInfo.ArgumentList.Add("-draw_mouse");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-framerate");
        startInfo.ArgumentList.Add(FramesPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-offset_x");
        startInfo.ArgumentList.Add(capturePlan.Bounds.Left.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-offset_y");
        startInfo.ArgumentList.Add(capturePlan.Bounds.Top.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-video_size");
        startInfo.ArgumentList.Add($"{capturePlan.Bounds.Width}x{capturePlan.Bounds.Height}");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add("desktop");
    }

    private void AddFfmpegH264OutputArguments(ProcessStartInfo startInfo)
    {
        List<string> videoFilters = [];
        if (capturePlan.OutputSize.Width != capturePlan.Bounds.Width ||
            capturePlan.OutputSize.Height != capturePlan.Bounds.Height)
        {
            videoFilters.Add($"scale={capturePlan.OutputSize.Width}:{capturePlan.OutputSize.Height}:flags=fast_bilinear");
        }

        videoFilters.Add("format=yuv420p");
        startInfo.ArgumentList.Add("-vf");
        startInfo.ArgumentList.Add(string.Join(",", videoFilters));
        startInfo.ArgumentList.Add("-an");
        startInfo.ArgumentList.Add("-c:v");
        startInfo.ArgumentList.Add("libx264");
        startInfo.ArgumentList.Add("-preset");
        startInfo.ArgumentList.Add("superfast");
        startInfo.ArgumentList.Add("-tune");
        startInfo.ArgumentList.Add("zerolatency");
        startInfo.ArgumentList.Add("-x264-params");
        startInfo.ArgumentList.Add("rc-lookahead=0:sync-lookahead=0:sliced-threads=1");
        startInfo.ArgumentList.Add("-pix_fmt");
        startInfo.ArgumentList.Add("yuv420p");
        startInfo.ArgumentList.Add("-r");
        startInfo.ArgumentList.Add(FramesPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-g");
        startInfo.ArgumentList.Add(GetH264GroupOfPicturesSize().ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-keyint_min");
        startInfo.ArgumentList.Add(GetH264GroupOfPicturesSize().ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-bf");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("-sc_threshold");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("-crf");
        startInfo.ArgumentList.Add(ToH264Crf(JpegQuality).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private async Task ReadFfmpegErrorLinesAsync(Process process, CancellationToken cancellationToken)
    {
        int loggedLineCount = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                loggedLineCount++;
                if (loggedLineCount <= 12 || loggedLineCount % 30 == 0)
                {
                    log($"FFmpeg stream stderr: {line.Trim()}", null);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                log("FFmpeg stderr reader stopped.", exception);
            }
        }
    }

    private static int ToFfmpegMjpegQualityScale(long jpegQuality)
    {
        double normalizedQuality = (Math.Clamp(jpegQuality, MinimumJpegQuality, MaximumJpegQuality) - MinimumJpegQuality) /
            (double)(MaximumJpegQuality - MinimumJpegQuality);
        return Math.Clamp((int)Math.Round(18D - normalizedQuality * 16D), 2, 18);
    }

    private static int ToH264Crf(long quality)
    {
        double normalizedQuality = (Math.Clamp(quality, MinimumJpegQuality, MaximumJpegQuality) - MinimumJpegQuality) /
            (double)(MaximumJpegQuality - MinimumJpegQuality);
        return Math.Clamp((int)Math.Round(34D - normalizedQuality * 16D), 18, 34);
    }

    private int GetH264GroupOfPicturesSize()
    {
        int calculatedKeyframeIntervalFrames = (int)Math.Round(FramesPerSecond * H264KeyframeIntervalSeconds);
        return Math.Clamp(calculatedKeyframeIntervalFrames, 1, 120);
    }

    private static async Task<bool> WaitForFileReadyAsync(string filePath, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 80; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                FileInfo fileInfo = new(filePath);
                if (fileInfo.Exists && fileInfo.Length > 16)
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task<byte[]> ReadAllBytesSharedAsync(string filePath, CancellationToken cancellationToken)
    {
        await using FileStream fileStream = new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1024 * 128,
            useAsync: true);
        using MemoryStream memoryStream = new();
        await fileStream.CopyToAsync(memoryStream, cancellationToken).ConfigureAwait(false);
        return memoryStream.ToArray();
    }

    private static async Task<string> ReadAllTextSharedAsync(string filePath, CancellationToken cancellationToken)
    {
        byte[] bytes = await ReadAllBytesSharedAsync(filePath, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    private static string RewriteHlsPlaylist(string playlistText)
    {
        string[] lines = playlistText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        StringBuilder rewrittenPlaylist = new();
        foreach (string line in lines)
        {
            string trimmedLine = line.Trim();
            if (trimmedLine.Length > 0 &&
                !trimmedLine.StartsWith('#') &&
                trimmedLine.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
            {
                rewrittenPlaylist.Append("hls/");
                rewrittenPlaylist.AppendLine(Path.GetFileName(trimmedLine));
                continue;
            }

            rewrittenPlaylist.AppendLine(line);
        }

        return rewrittenPlaylist.ToString();
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

            Exception? captureException = null;
            for (int attempt = 1; attempt <= FrameCaptureRetryCount; attempt++)
            {
                try
                {
                    FullScreenScreenshotFrame nextFrame = await Task
                        .Run(() => FullScreenScreenshotCapture.CaptureToJpegBytes(capturePlan, JpegQuality), cancellationToken)
                        .ConfigureAwait(false);
                    cachedFrame = nextFrame;
                    cachedFrameAtUtc = DateTimeOffset.UtcNow;
                    return nextFrame;
                }
                catch (Exception exception) when (IsTransientCaptureException(exception))
                {
                    captureException = exception;
                    if (attempt < FrameCaptureRetryCount)
                    {
                        await Task.Delay(FrameCaptureRetryDelay, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            if (cachedFrame is not null)
            {
                cachedFrameAtUtc = DateTimeOffset.UtcNow;
                LogCaptureFailureThrottled(
                    "Screen capture failed after retries; reusing the last captured frame to keep the stream connected.",
                    captureException);

                return cachedFrame;
            }

            if (captureException is not null)
            {
                throw captureException;
            }

            throw new InvalidOperationException("Screen capture failed without a captured frame.");
        }
        finally
        {
            frameSemaphore.Release();
        }
    }

    private static bool IsTransientCaptureException(Exception exception)
    {
        return exception is InvalidOperationException or ExternalException or System.ComponentModel.Win32Exception;
    }

    private static bool IsExpectedClientDisconnectException(Exception exception)
    {
        return exception.GetBaseException() is SocketException socketException &&
            socketException.SocketErrorCode is SocketError.ConnectionAborted or
                SocketError.ConnectionReset or
                SocketError.NetworkReset or
                SocketError.OperationAborted or
                SocketError.Shutdown;
    }

    private void LogCaptureFailureThrottled(string message, Exception? exception)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (nowUtc - lastCaptureFailureLoggedAtUtc <= TimeSpan.FromSeconds(10))
        {
            return;
        }

        lastCaptureFailureLoggedAtUtc = nowUtc;
        log(message, exception);
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
        await networkStream.FlushAsync(cancellationToken).ConfigureAwait(false);
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

    private static async Task WriteFfmpegMjpegResponseHeaderAsync(
        NetworkStream networkStream,
        CancellationToken cancellationToken)
    {
        string header =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: multipart/x-mixed-replace; boundary=ffmpeg\r\n" +
            "Cache-Control: no-store, no-cache, must-revalidate\r\n" +
            "Pragma: no-cache\r\n" +
            "X-Robots-Tag: noindex, nofollow\r\n" +
            "Connection: close\r\n" +
            "\r\n";
        await networkStream
            .WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WriteFmp4ResponseHeaderAsync(
        NetworkStream networkStream,
        CancellationToken cancellationToken)
    {
        string header =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: video/mp4\r\n" +
            "Cache-Control: no-store, no-cache, must-revalidate\r\n" +
            "Pragma: no-cache\r\n" +
            "X-Robots-Tag: noindex, nofollow\r\n" +
            "Connection: close\r\n" +
            "\r\n";
        await networkStream
            .WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken)
            .ConfigureAwait(false);
    }

    private void TryDeleteHlsDirectory()
    {
        if (hlsDirectory is null)
        {
            return;
        }

        try
        {
            string workDirectoryFullPath = Path.GetFullPath(streamWorkDirectory);
            string hlsDirectoryFullPath = Path.GetFullPath(hlsDirectory);
            if (!hlsDirectoryFullPath.StartsWith(workDirectoryFullPath, StringComparison.OrdinalIgnoreCase))
            {
                log($"HLS directory cleanup skipped because the path is outside the stream work directory. Path: {hlsDirectory}.", null);
                return;
            }

            if (Directory.Exists(hlsDirectory))
            {
                Directory.Delete(hlsDirectory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            log($"HLS directory cleanup failed. Path: {hlsDirectory}.", exception);
        }
    }

    private static void StopProcess(Process process, string processName, Action<string, Exception?> log)
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
            log($"{processName} process could not be stopped cleanly.", exception);
        }
        finally
        {
            process.Dispose();
        }
    }
}

internal readonly record struct ScreenStreamOptions(
    ScreenCaptureTarget Target,
    int FramesPerSecond,
    long JpegQuality,
    int MaxWidth,
    ScreenStreamMethod Method)
{
    public bool RequiresFfmpeg =>
        Method is ScreenStreamMethod.H264Fmp4 or ScreenStreamMethod.H264Hls ||
        Method == ScreenStreamMethod.Mjpeg && FramesPerSecond >= ScreenStreamingServer.MjpegFfmpegThresholdFramesPerSecond;

    public static ScreenStreamOptions Create(
        ScreenCaptureTarget target,
        int framesPerSecond = ScreenStreamingServer.DefaultFramesPerSecond,
        long jpegQuality = ScreenStreamingServer.DefaultJpegQuality,
        int maxWidth = ScreenStreamingServer.DefaultMaxWidth,
        ScreenStreamMethod method = ScreenStreamMethod.H264Fmp4)
    {
        return new ScreenStreamOptions(
            target,
            NormalizeFramesPerSecond(framesPerSecond),
            NormalizeJpegQuality(jpegQuality),
            NormalizeMaxWidth(maxWidth),
            method);
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

internal enum ScreenStreamMethod
{
    Mjpeg,
    H264Fmp4,
    H264Hls
}

internal static class ScreenStreamMethodNames
{
    public const string Mjpeg = "mjpeg";
    public const string H264Fmp4 = "h264-fmp4";
    public const string H264Hls = "h264-hls";

    public static bool TryParse(string? text, out ScreenStreamMethod method)
    {
        method = ScreenStreamMethod.H264Fmp4;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (text.Equals(Mjpeg, StringComparison.OrdinalIgnoreCase))
        {
            method = ScreenStreamMethod.Mjpeg;
            return true;
        }

        if (text.Equals(H264Fmp4, StringComparison.OrdinalIgnoreCase) ||
            text.Equals("fmp4", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("mp4", StringComparison.OrdinalIgnoreCase))
        {
            method = ScreenStreamMethod.H264Fmp4;
            return true;
        }

        if (text.Equals(H264Hls, StringComparison.OrdinalIgnoreCase) ||
            text.Equals("hls", StringComparison.OrdinalIgnoreCase))
        {
            method = ScreenStreamMethod.H264Hls;
            return true;
        }

        return false;
    }

    public static string ToOptionValue(ScreenStreamMethod method)
    {
        return method switch
        {
            ScreenStreamMethod.H264Fmp4 => H264Fmp4,
            ScreenStreamMethod.H264Hls => H264Hls,
            _ => Mjpeg
        };
    }
}
