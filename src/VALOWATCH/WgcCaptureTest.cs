using ScreenRecorderLib;

namespace VALOWATCH;

/// <summary>
/// Windows.Graphics.Capture（ScreenRecorderLib）が、この環境で動作するかを確認するための
/// 最小テスト。指定秒数だけ画面を録画してファイルに保存する。
///
/// 目的：ddagrab（Desktop Duplication）が RustDesk 等の影響で使えない状況でも、
/// WGC ベースの ScreenRecorderLib なら録画できることを検証する。
/// </summary>
internal static class WgcCaptureTest
{
    /// <summary>
    /// 指定秒数、画面を WGC で録画して outputPath に保存する。
    /// 成功したら true、失敗したらエラーメッセージを errorMessage に入れて false。
    /// </summary>
    public static bool TryRecord(string outputPath, int seconds, out string status)
    {
        string localStatus = "started";
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

        recorder.OnRecordingComplete += (_, _) =>
        {
            completed = true;
            doneEvent.Set();
        };
        recorder.OnRecordingFailed += (_, args) =>
        {
            failed = true;
            failReason = args.Error;
            doneEvent.Set();
        };

        try
        {
            recorder.Record(outputPath);

            // 指定秒数録画してから停止。
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
            recorder.Stop();

            // 停止完了（OnRecordingComplete）を待つ。
            doneEvent.Wait(TimeSpan.FromSeconds(15));
        }
        catch (Exception exception)
        {
            status = "exception: " + exception.Message;
            return false;
        }
        finally
        {
            try
            {
                recorder.Dispose();
            }
            catch
            {
            }
        }

        if (failed)
        {
            status = "failed: " + failReason;
            return false;
        }

        long size = 0;
        try
        {
            if (File.Exists(outputPath))
            {
                size = new FileInfo(outputPath).Length;
            }
        }
        catch
        {
        }

        status = $"completed={completed} size={size} localStatus={localStatus}";
        return completed && size > 0;
    }
}
