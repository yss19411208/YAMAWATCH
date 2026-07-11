using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VALOWATCH;

public sealed class LoopbackRecorder : IDisposable
{
    private readonly object recorderLock = new();
    private readonly DiscordBotSettingsStore? discordBotSettingsStore;
    private WasapiCapture? activeCapture;
    private WaveFileWriter? activeWriter;

    public LoopbackRecorder(DiscordBotSettingsStore? discordBotSettingsStore = null)
    {
        this.discordBotSettingsStore = discordBotSettingsStore;
    }

    public bool IsRecording { get; private set; }

    public string? ActiveFilePath { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public void Start(string recordingFilePath)
    {
        lock (recorderLock)
        {
            if (IsRecording)
            {
                throw new InvalidOperationException("録音はすでに開始されています。");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(recordingFilePath) ?? AppContext.BaseDirectory);

            DiscordBotSettings? discordSettings = discordBotSettingsStore?.Load();
            MMDevice captureDevice = DiscordBotVoiceRelay.GetDefaultMicrophoneDevice(discordSettings?.MicrophoneDeviceName);
            WasapiCapture capture = new(captureDevice);
            WaveFileWriter writer = new(recordingFilePath, capture.WaveFormat);

            activeCapture = capture;
            activeWriter = writer;
            ActiveFilePath = recordingFilePath;
            StartedAt = DateTimeOffset.Now;

            capture.DataAvailable += OnAudioDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;

            try
            {
                capture.StartRecording();
                IsRecording = true;
            }
            catch
            {
                DisposeActiveObjects();
                throw;
            }
        }
    }

    public void Stop()
    {
        lock (recorderLock)
        {
            if (!IsRecording || activeCapture is null)
            {
                return;
            }

            try
            {
                activeCapture.StopRecording();
            }
            finally
            {
                DisposeActiveObjects();
            }
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private void OnAudioDataAvailable(object? sender, WaveInEventArgs eventArgs)
    {
        lock (recorderLock)
        {
            if (activeWriter is null || eventArgs.BytesRecorded <= 0)
            {
                return;
            }

            activeWriter.Write(eventArgs.Buffer, 0, eventArgs.BytesRecorded);
            activeWriter.Flush();
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs eventArgs)
    {
        lock (recorderLock)
        {
            DisposeActiveObjects();
        }
    }

    private void DisposeActiveObjects()
    {
        if (activeCapture is not null)
        {
            activeCapture.DataAvailable -= OnAudioDataAvailable;
            activeCapture.RecordingStopped -= OnRecordingStopped;
            activeCapture.Dispose();
        }

        // WAVヘッダーを確定させるため、停止時は必ずwriterを閉じます。
        activeWriter?.Dispose();
        activeCapture = null;
        activeWriter = null;
        IsRecording = false;
        StartedAt = null;
    }
}
