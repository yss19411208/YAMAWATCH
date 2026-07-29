using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Runtime.InteropServices;

namespace VALOWATCH;

internal sealed class SystemLoopbackWaveProvider : IWaveProvider, IDisposable
{
    private static readonly TimeSpan HealthLogInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RecentAudibleSignalDuration = TimeSpan.FromSeconds(6);
    private const float AudiblePeakThreshold = 0.003F;

    private readonly WasapiLoopbackCapture capture;
    private readonly MMDevice renderDevice;
    private readonly BufferedWaveProvider bufferedWaveProvider;
    private readonly Action<string, Exception?> writeLog;
    private readonly object sync = new();
    private bool disposed;
    private bool captureStopped;
    private DateTimeOffset lastAudibleCaptureAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset lastHealthLogAtUtc = DateTimeOffset.MinValue;
    private long capturedCallbackCount;
    private long capturedByteCount;
    private long capturedAudibleCallbackCount;
    private float capturedPeak;
    private bool loggedFirstAudibleCapture;

    public SystemLoopbackWaveProvider(
        TimeSpan bufferDuration,
        Action<string, Exception?> writeLog)
    {
        this.writeLog = writeLog;

        using MMDeviceEnumerator deviceEnumerator = new();
        renderDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        capture = new WasapiLoopbackCapture(renderDevice);
        bufferedWaveProvider = new BufferedWaveProvider(capture.WaveFormat)
        {
            BufferDuration = bufferDuration,
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };

        CurrentSourceDescription = $"System loopback: {renderDevice.FriendlyName}";
        capture.DataAvailable += OnCaptureDataAvailable;
        capture.RecordingStopped += OnCaptureRecordingStopped;

        try
        {
            capture.StartRecording();
        }
        catch
        {
            capture.DataAvailable -= OnCaptureDataAvailable;
            capture.RecordingStopped -= OnCaptureRecordingStopped;
            capture.Dispose();
            renderDevice.Dispose();
            throw;
        }
    }

    public WaveFormat WaveFormat => bufferedWaveProvider.WaveFormat;

    public string CurrentSourceDescription { get; }

    public bool HasRecentAudibleSignal
    {
        get
        {
            lock (sync)
            {
                return lastAudibleCaptureAtUtc != DateTimeOffset.MinValue &&
                    DateTimeOffset.UtcNow - lastAudibleCaptureAtUtc <= RecentAudibleSignalDuration;
            }
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        return bufferedWaveProvider.Read(buffer, offset, count);
    }

    public string GetStatusSummary()
    {
        lock (sync)
        {
            return
                $"SystemLoopbackCapturing: {!disposed && !captureStopped}. " +
                $"SystemCallbacks: {capturedCallbackCount}. " +
                $"SystemAudibleCallbacks: {capturedAudibleCallbackCount}. " +
                $"SystemPeak: {capturedPeak:0.0000}. " +
                $"SystemBufferedMs: {bufferedWaveProvider.BufferedDuration.TotalMilliseconds:0}.";
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        capture.DataAvailable -= OnCaptureDataAvailable;
        capture.RecordingStopped -= OnCaptureRecordingStopped;
        try
        {
            capture.StopRecording();
        }
        catch (InvalidOperationException exception)
        {
            writeLog("System loopback stop failed.", exception);
        }
        catch (COMException exception)
        {
            writeLog("System loopback stop failed.", exception);
        }
        finally
        {
            capture.Dispose();
            renderDevice.Dispose();
        }
    }

    private void OnCaptureDataAvailable(object? sender, WaveInEventArgs eventArgs)
    {
        if (eventArgs.BytesRecorded <= 0)
        {
            return;
        }

        lock (sync)
        {
            if (disposed || captureStopped || !ReferenceEquals(sender, capture))
            {
                return;
            }
        }

        bufferedWaveProvider.AddSamples(eventArgs.Buffer, 0, eventArgs.BytesRecorded);
        float peak = DiscordBotVoiceRelay.CalculateAudioPeak(
            capture.WaveFormat,
            eventArgs.Buffer,
            0,
            eventArgs.BytesRecorded);
        bool shouldLogFirstAudible = false;
        bool shouldLogHealth = false;
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        lock (sync)
        {
            capturedCallbackCount++;
            capturedByteCount += eventArgs.BytesRecorded;
            capturedPeak = Math.Max(capturedPeak, peak);
            if (peak >= AudiblePeakThreshold)
            {
                capturedAudibleCallbackCount++;
                lastAudibleCaptureAtUtc = nowUtc;
                if (!loggedFirstAudibleCapture)
                {
                    loggedFirstAudibleCapture = true;
                    shouldLogFirstAudible = true;
                }
            }

            if (nowUtc - lastHealthLogAtUtc >= HealthLogInterval)
            {
                lastHealthLogAtUtc = nowUtc;
                shouldLogHealth = true;
            }
        }

        if (shouldLogFirstAudible)
        {
            writeLog($"System loopback became audible. Peak: {peak:0.0000}.", null);
        }
        else if (shouldLogHealth)
        {
            writeLog(GetStatusSummary(), null);
        }
    }

    private void OnCaptureRecordingStopped(object? sender, StoppedEventArgs eventArgs)
    {
        bool wasActive;
        lock (sync)
        {
            wasActive = !disposed && ReferenceEquals(sender, capture);
            captureStopped = true;
        }

        if (!wasActive)
        {
            return;
        }

        bufferedWaveProvider.ClearBuffer();
        if (eventArgs.Exception is null)
        {
            writeLog("System loopback stopped.", null);
        }
        else
        {
            writeLog("System loopback stopped because of an audio error.", eventArgs.Exception);
        }
    }
}
