using System.Diagnostics;
using System.Net;
using System.Text;
using ScreenRecorderLib;

namespace VALOWATCH;

/// <summary>
/// Windows.Graphics.Capture（ScreenRecorderLib）で画面をキャプチャし、
/// fragmented MP4 を HTTP でライブ配信するサーバー。
///
/// ScreenRecorderLib の fMP4 は TFHD base-data-offset を使うため
/// そのままでは MSE(ブラウザ) で再生できない。そこで ffmpeg で
/// -c copy（再エンコード無し・低負荷）+ default_base_moof により
/// MSE 互換の fMP4 に remux してから配信する。
///
/// パイプライン：
///   ScreenRecorderLib(WGC,H264/GPU) → RawStream
///     → ffmpeg(-i pipe:0 -c copy -movflags ... -f mp4 pipe:1)
///     → FanoutStream → HTTP(fMP4) → ブラウザ(MSE)
/// </summary>
internal sealed class WgcStreamingServer : IDisposable
{
    private readonly Action<string, Exception?> log;
    private readonly string ffmpegPath;
    private readonly int bitrate;
    private readonly int framerate;

    private Recorder? recorder;
    private RawStream? rawStream;
    private FanoutStream? fanout;
    private Process? ffmpeg;
    private Process? cloudflared;
    private HttpListener? httpListener;
    private CancellationTokenSource? cancellation;
    private string publicPath = string.Empty;
    private int listenPort;
    public string? PublicUrl { get; private set; }

    private static readonly System.Text.RegularExpressions.Regex TryCloudflareUrlRegex =
        new(@"https://[a-zA-Z0-9-]+\.trycloudflare\.com", System.Text.RegularExpressions.RegexOptions.Compiled);

    public WgcStreamingServer(string ffmpegPath, Action<string, Exception?> log, int bitrate = 12_000_000, int framerate = 60)
    {
        this.ffmpegPath = ffmpegPath;
        this.log = log;
        this.bitrate = bitrate;
        this.framerate = framerate;
    }

    public string LocalUrl => $"http://127.0.0.1:{listenPort}/{publicPath}/";

    /// <summary>
    /// cloudflared で公開リンクを取得する（アカウント不要、誰でも見れる）。
    /// cloudflaredPath が渡され、起動できたら trycloudflare の公開URLを PublicUrl に入れる。
    /// </summary>
    public async Task<string?> StartPublicTunnelAsync(string cloudflaredPath, CancellationToken token)
    {
        if (!File.Exists(cloudflaredPath))
        {
            log("cloudflared not found; public URL unavailable. Local only.", null);
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = cloudflaredPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("tunnel");
        startInfo.ArgumentList.Add("--no-autoupdate");
        startInfo.ArgumentList.Add("--url");
        startInfo.ArgumentList.Add($"http://127.0.0.1:{listenPort}");

        cloudflared = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var urlSource = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            System.Text.RegularExpressions.Match m = TryCloudflareUrlRegex.Match(line);
            if (m.Success)
            {
                urlSource.TrySetResult(m.Value);
            }
        }

        if (!cloudflared.Start())
        {
            return null;
        }

        _ = Task.Run(async () =>
        {
            try { string? l; while ((l = await cloudflared.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null) OnLine(l); } catch { }
        }, CancellationToken.None);
        _ = Task.Run(async () =>
        {
            try { string? l; while ((l = await cloudflared.StandardError.ReadLineAsync().ConfigureAwait(false)) != null) OnLine(l); } catch { }
        }, CancellationToken.None);

        try
        {
            string url = await urlSource.Task.WaitAsync(TimeSpan.FromSeconds(25), token).ConfigureAwait(false);
            // 公開URLは、ローカルの publicPath を付けた形で視聴。
            PublicUrl = $"{url}/{publicPath}/";
            log($"WGC public tunnel ready: {PublicUrl}", null);
            return PublicUrl;
        }
        catch (Exception exception)
        {
            log("cloudflared tunnel URL not obtained in time.", exception);
            return null;
        }
    }

    public string Start()
    {
        cancellation = new CancellationTokenSource();
        publicPath = Guid.NewGuid().ToString("N");
        rawStream = new RawStream();
        fanout = new FanoutStream();

        listenPort = FindFreePort();
        httpListener = new HttpListener();
        httpListener.Prefixes.Add($"http://127.0.0.1:{listenPort}/");
        httpListener.Start();
        _ = Task.Run(() => AcceptLoopAsync(cancellation.Token), CancellationToken.None);

        StartFfmpegRemux();
        StartRecorder();

        log($"WGC streaming server started. Url: {LocalUrl} Bitrate: {bitrate} Fps: {framerate}", null);
        return LocalUrl;
    }

    private void StartFfmpegRemux()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // 入力：ScreenRecorderLib の fMP4（標準入力）
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-fflags");
        startInfo.ArgumentList.Add("+nobuffer+genpts");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add("pipe:0");
        // 再エンコード無しでコピー（低負荷・低遅延）
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("copy");
        // MSE 互換の fMP4（default_base_moof で base-data-offset を使わない）
        startInfo.ArgumentList.Add("-movflags");
        startInfo.ArgumentList.Add("cmaf+frag_keyframe+empty_moov+default_base_moof+dash");
        startInfo.ArgumentList.Add("-frag_duration");
        startInfo.ArgumentList.Add("100000");
        startInfo.ArgumentList.Add("-muxdelay");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("-muxpreload");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("-flush_packets");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("mp4");
        startInfo.ArgumentList.Add("pipe:1");

        ffmpeg = new Process { StartInfo = startInfo };
        ffmpeg.Start();

        // ffmpeg の標準エラーをログ（真っ黒系ノイズは抑制）。
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await ffmpeg.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    if (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("Invalid", StringComparison.OrdinalIgnoreCase))
                    {
                        log("WGC ffmpeg: " + line, null);
                    }
                }
            }
            catch { }
        }, CancellationToken.None);

        // ffmpeg の標準出力（MSE互換fMP4）を FanoutStream へポンプ。
        _ = Task.Run(async () =>
        {
            try
            {
                Stream ffout = ffmpeg.StandardOutput.BaseStream;
                byte[] buf = new byte[65536];
                int read;
                while ((read = await ffout.ReadAsync(buf.AsMemory(0, buf.Length)).ConfigureAwait(false)) > 0)
                {
                    fanout!.Write(buf, 0, read);
                }
            }
            catch { }
        }, CancellationToken.None);

        // RawStream（ScreenRecorderLibの出力）を ffmpeg の標準入力へポンプ。
        _ = Task.Run(async () =>
        {
            try
            {
                Stream ffin = ffmpeg.StandardInput.BaseStream;
                await rawStream!.PumpToAsync(ffin, cancellation!.Token).ConfigureAwait(false);
            }
            catch { }
        }, CancellationToken.None);
    }

    private void StartRecorder()
    {
        var options = new RecorderOptions
        {
            SourceOptions = new SourceOptions
            {
                RecordingSources = new List<RecordingSourceBase>
                {
                    new DisplayRecordingSource(DisplayRecordingSource.MainMonitor),
                },
            },
            OutputOptions = new OutputOptions
            {
                RecorderMode = RecorderMode.Video,
            },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Bitrate = bitrate,
                Framerate = framerate,
                IsFixedFramerate = false,
                IsHardwareEncodingEnabled = true,
                IsLowLatencyEnabled = true,
                IsMp4FastStartEnabled = true,
                IsFragmentedMp4Enabled = true,
                Encoder = new H264VideoEncoder
                {
                    BitrateMode = H264BitrateControlMode.CBR,
                    EncoderProfile = H264Profile.High,
                },
            },
            AudioOptions = new AudioOptions
            {
                IsAudioEnabled = false,
            },
        };

        log("WGC: creating recorder...", null);
        recorder = Recorder.CreateRecorder(options);
        log("WGC: recorder created = " + (recorder != null), null);
        if (recorder == null) { throw new InvalidOperationException("Recorder.CreateRecorder returned null (ScreenRecorderLib init failed)."); }
        recorder.OnRecordingFailed += (_, args) => log("WGC recorder failed: " + args.Error, null);
        log("WGC: rawStream = " + (rawStream != null) + ", starting record...", null);
        recorder.Record(rawStream!);
        log("WGC: recorder.Record called.", null);
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && httpListener is not null)
        {
            HttpListenerContext context;
            try
            {
                context = await httpListener.GetContextAsync().ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => HandleRequestAsync(context, token), CancellationToken.None);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken token)
    {
        string path = context.Request.Url?.AbsolutePath ?? "/";
        try
        {
            if (path.EndsWith("/stream.mp4", StringComparison.OrdinalIgnoreCase))
            {
                await ServeStreamAsync(context, token).ConfigureAwait(false);
            }
            else
            {
                ServeHtml(context);
            }
        }
        catch (Exception exception)
        {
            log("WGC request handling failed.", exception);
            try { context.Response.Abort(); } catch { }
        }
    }

    private void ServeHtml(HttpListenerContext context)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(BuildHtml());
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        context.Response.OutputStream.Close();
    }

    private async Task ServeStreamAsync(HttpListenerContext context, CancellationToken token)
    {
        context.Response.ContentType = "video/mp4";
        context.Response.SendChunked = true;
        context.Response.Headers.Add("Cache-Control", "no-cache, no-store");

        Stream output = context.Response.OutputStream;
        FanoutStream.Subscriber subscriber = fanout!.Subscribe();
        try
        {
            byte[]? initSegment = fanout.GetInitSegment();
            if (initSegment is not null)
            {
                await output.WriteAsync(initSegment, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
            }

            while (!token.IsCancellationRequested)
            {
                byte[]? chunk = await subscriber.TakeAsync(token).ConfigureAwait(false);
                if (chunk is null)
                {
                    break;
                }

                await output.WriteAsync(chunk, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
            }
        }
        catch { }
        finally
        {
            fanout.Unsubscribe(subscriber);
            try { output.Close(); } catch { }
        }
    }

    private string BuildHtml()
    {
        return $$"""
<!DOCTYPE html>
<html lang="ja">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>Live</title>
<style>
  html,body{margin:0;background:#000;height:100%;}
  video{width:100%;height:100%;object-fit:contain;background:#000;}
  #msg{position:fixed;top:8px;left:8px;color:#0f0;font-family:monospace;font-size:12px;white-space:pre;z-index:9;}
</style>
</head>
<body>
<video id="v" autoplay muted playsinline></video>
<div id="msg">connecting...</div>
<script>
const video = document.getElementById('v');
const msg = document.getElementById('msg');
function log(t){ msg.textContent = t; }
const mimeCandidates = [
  'video/mp4; codecs="avc1.640033"',
  'video/mp4; codecs="avc1.640032"',
  'video/mp4; codecs="avc1.64002A"',
  'video/mp4; codecs="avc1.640028"',
  'video/mp4; codecs="avc1.4D4028"',
  'video/mp4',
];
let mime = mimeCandidates.find(m => window.MediaSource && MediaSource.isTypeSupported(m));
if (!mime) { log('MSE not supported'); }
else {
  log('using ' + mime);
  const mediaSource = new MediaSource();
  video.src = URL.createObjectURL(mediaSource);
  mediaSource.addEventListener('sourceopen', async () => {
    let sb;
    try { sb = mediaSource.addSourceBuffer(mime); }
    catch (e) { log('addSourceBuffer failed: ' + e); return; }
    sb.mode = 'sequence';
    const queue = [];
    let errorCount = 0;
    function pump() {
      if (sb.updating || queue.length === 0) return;
      if (video.error) { log('VIDEO ERROR code=' + video.error.code + ' msg=' + (video.error.message||'')); return; }
      const chunk = queue.shift();
      try { sb.appendBuffer(chunk); }
      catch (e) {
        errorCount++;
        if (errorCount <= 2) log('append error(' + errorCount + '): ' + e.message + ' vErr=' + (video.error?video.error.code:'none'));
        if (e.name === 'QuotaExceededError' && video.buffered.length > 0) { try { sb.remove(0, video.currentTime - 1); } catch (_) {} }
      }
    }
    // 目標遅延（秒）。通信の揺らぎを吸収するためのバッファ量。
    // 小さいほど低遅延だが、通信が不安定だとカクつく。0.6秒あたりが安定と低遅延の両立点。
    const TARGET_DELAY = 0.6;
    // これを超えて遅れたら（通信が詰まって大きく遅延したら）静かにジャンプして復帰。
    const MAX_DELAY = 2.5;

    sb.addEventListener('updateend', () => {
      if (video.buffered.length > 0) {
        const end = video.buffered.end(video.buffered.length - 1);
        const behind = end - video.currentTime;

        // 倍速・減速はしない（常に等速）。再生位置は基本いじらない。
        // ただし、遅延が大きくなりすぎた場合だけ、一度だけ目標位置へジャンプして復帰する。
        if (behind > MAX_DELAY) {
          video.currentTime = end - TARGET_DELAY;
        }
        video.playbackRate = 1.0;

        if (video.paused) video.play().catch(()=>{});
        msg.textContent = 'delay ' + behind.toFixed(2) + 's';
      }
      pump();
    });
    try {
      const resp = await fetch('/{{publicPath}}/stream.mp4');
      const reader = resp.body.getReader();
      while (true) {
        const { done, value } = await reader.read();
        if (done) { log('stream ended'); break; }
        queue.push(value);
        pump();
      }
    } catch (e) { log('fetch error: ' + e); }
  });
}
</script>
</body>
</html>
""";
    }

    private static int FindFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        try { cancellation?.Cancel(); } catch { }
        try { recorder?.Stop(); } catch { }
        try { recorder?.Dispose(); } catch { }
        try { if (cloudflared is { HasExited: false }) cloudflared.Kill(entireProcessTree: true); } catch { }
        try { cloudflared?.Dispose(); } catch { }
        try { if (ffmpeg is { HasExited: false }) ffmpeg.Kill(); } catch { }
        try { ffmpeg?.Dispose(); } catch { }
        try { httpListener?.Stop(); } catch { }
        try { httpListener?.Close(); } catch { }
        try { rawStream?.Dispose(); } catch { }
        try { fanout?.Dispose(); } catch { }
    }
}
