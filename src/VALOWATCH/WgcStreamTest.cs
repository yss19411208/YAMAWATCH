using ScreenRecorderLib;

namespace VALOWATCH;

/// <summary>
/// ScreenRecorderLib が Stream に fMP4 をリアルタイムに書き出せるかを検証するテスト。
/// カスタム Stream で、録画中に何回・どのくらいのデータが書き込まれるかを記録する。
///
/// 配信では「録画中に、fMP4 データが逐次 Stream に来る」必要がある。
/// 最後にまとめて来るのでは、ライブ配信にならない。
/// </summary>
internal static class WgcStreamTest
{
    public static bool TryStreamTest(int seconds, out string status)
    {
        var probe = new ProbeStream();
        bool completed = false;
        bool failed = false;
        string failReason = string.Empty;
        using var doneEvent = new ManualResetEventSlim(false);

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
                Bitrate = 12000 * 1000,
                Framerate = 60,
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

        Recorder recorder = Recorder.CreateRecorder(options);
        recorder.OnRecordingComplete += (_, _) => { completed = true; doneEvent.Set(); };
        recorder.OnRecordingFailed += (_, args) => { failed = true; failReason = args.Error; doneEvent.Set(); };

        try
        {
            recorder.Record(probe);
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
            recorder.Stop();
            doneEvent.Wait(TimeSpan.FromSeconds(15));
        }
        catch (Exception exception)
        {
            status = "exception: " + exception.Message;
            return false;
        }
        finally
        {
            try { recorder.Dispose(); } catch { }
        }

        if (failed)
        {
            status = "failed: " + failReason;
            return false;
        }

        // リアルタイム性の指標：
        // ・書き込み回数（多いほどリアルタイムに来ている）
        // ・最初の書き込みまでの時間（短いほど良い）
        // ・総バイト数
        status = $"completed={completed} writes={probe.WriteCount} " +
            $"totalBytes={probe.TotalBytes} " +
            $"firstWriteMs={probe.FirstWriteMs} " +
            $"lastWriteMs={probe.LastWriteMs}";
        return completed && probe.TotalBytes > 0;
    }

    /// <summary>
    /// 書き込みの回数・タイミング・量を記録する Stream。
    /// fMP4 出力はライブラリが Seek/Length/Position を使うため、
    /// 内部的に MemoryStream で受けて、Seek 等に完全対応させる。
    /// </summary>
    private sealed class ProbeStream : Stream
    {
        private readonly System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        private readonly MemoryStream inner = new();

        public int WriteCount { get; private set; }
        public long TotalBytes { get; private set; }
        public long FirstWriteMs { get; private set; } = -1;
        public long LastWriteMs { get; private set; } = -1;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteCount++;
            TotalBytes += count;
            long ms = stopwatch.ElapsedMilliseconds;
            if (FirstWriteMs < 0)
            {
                FirstWriteMs = ms;
            }

            LastWriteMs = ms;
            inner.Write(buffer, offset, count);
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
    }
}
