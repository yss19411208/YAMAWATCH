using NAudio.Wave;
using System.Diagnostics;

namespace VALOWATCH;

internal sealed class LineProcessLoopbackWaveProvider : IWaveProvider, IDisposable
{
    private static readonly TimeSpan ProcessRefreshInterval = TimeSpan.FromSeconds(2);

    private readonly string[] processNames;
    private readonly string sourceLabel;
    private readonly BufferedWaveProvider bufferedWaveProvider;
    private readonly Action<string, Exception?> writeLog;
    private readonly object sync = new();
    private readonly System.Threading.Timer refreshTimer;
    private ProcessLoopbackCapture? activeCapture;
    private int activeProcessId;
    private bool disposed;
    private int refreshInProgress;

    public LineProcessLoopbackWaveProvider(
        IEnumerable<string> processNames,
        TimeSpan bufferDuration,
        Action<string, Exception?> writeLog,
        string sourceLabel = "LINE")
    {
        this.processNames = processNames
            .Select(NormalizeProcessName)
            .Where(static processName => !string.IsNullOrWhiteSpace(processName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .DefaultIfEmpty("LINE")
            .ToArray();
        this.sourceLabel = string.IsNullOrWhiteSpace(sourceLabel)
            ? "process"
            : sourceLabel.Trim();
        this.writeLog = writeLog;
        bufferedWaveProvider = new BufferedWaveProvider(ProcessLoopbackCapture.CaptureWaveFormat)
        {
            BufferDuration = bufferDuration,
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };

        CurrentSourceDescription = $"{this.sourceLabel} process loopback waiting";
        refreshTimer = new System.Threading.Timer(_ => RefreshActiveCaptureSafely(), null, TimeSpan.Zero, ProcessRefreshInterval);
    }

    public WaveFormat WaveFormat => bufferedWaveProvider.WaveFormat;

    public string CurrentSourceDescription { get; private set; }

    public bool IsCapturing
    {
        get
        {
            lock (sync)
            {
                return activeCapture is not null;
            }
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        return bufferedWaveProvider.Read(buffer, offset, count);
    }

    public void Dispose()
    {
        disposed = true;
        refreshTimer.Dispose();
        StopActiveCapture($"{sourceLabel} process loopback disposed.");
    }

    private void RefreshActiveCaptureSafely()
    {
        if (Interlocked.Exchange(ref refreshInProgress, 1) == 1)
        {
            return;
        }

        try
        {
            RefreshActiveCapture();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            writeLog($"{sourceLabel} process loopback refresh failed.", exception);
        }
        finally
        {
            Interlocked.Exchange(ref refreshInProgress, 0);
        }
    }

    private void RefreshActiveCapture()
    {
        if (disposed)
        {
            return;
        }

        TargetProcess? targetProcess = FindTargetProcess();
        if (targetProcess is null)
        {
            StopActiveCapture($"{sourceLabel} process is not running. Waiting for {sourceLabel} audio.");
            CurrentSourceDescription = $"{sourceLabel} process loopback waiting";
            return;
        }

        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            if (activeCapture is not null && activeProcessId == targetProcess.ProcessId)
            {
                return;
            }
        }

        StopActiveCapture($"Switching {sourceLabel} process loopback to PID {targetProcess.ProcessId}.");
        StartCaptureForProcess(targetProcess);
    }

    private void StartCaptureForProcess(TargetProcess targetProcess)
    {
        if (disposed)
        {
            return;
        }

        ProcessLoopbackCapture capture = new(targetProcess.ProcessId);
        capture.DataAvailable += OnCaptureDataAvailable;
        capture.RecordingStopped += OnCaptureRecordingStopped;

        bool shouldDisposeCapture = false;
        lock (sync)
        {
            if (disposed || activeCapture is not null)
            {
                shouldDisposeCapture = true;
            }
            else
            {
                activeCapture = capture;
                activeProcessId = targetProcess.ProcessId;
                CurrentSourceDescription = targetProcess.Description;
            }
        }

        if (shouldDisposeCapture)
        {
            capture.DataAvailable -= OnCaptureDataAvailable;
            capture.RecordingStopped -= OnCaptureRecordingStopped;
            capture.Dispose();
            return;
        }

        try
        {
            capture.StartRecording();
            writeLog(
                $"{sourceLabel} process-only loopback started. {targetProcess.Description}. " +
                $"Format: {capture.WaveFormat}. Buffer: {bufferedWaveProvider.BufferDuration.TotalMilliseconds:0}ms.",
                null);
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidCastException or TimeoutException or System.Runtime.InteropServices.COMException)
        {
            ClearActiveCapture(capture);
            capture.DataAvailable -= OnCaptureDataAvailable;
            capture.RecordingStopped -= OnCaptureRecordingStopped;
            capture.Dispose();
            bufferedWaveProvider.ClearBuffer();
            writeLog(
                $"{sourceLabel} process-only loopback could not start for {targetProcess.Description}. " +
                $"The bot will keep sending microphone audio and retry {sourceLabel} audio automatically.",
                exception);
        }
    }

    private void StopActiveCapture(string reason)
    {
        ProcessLoopbackCapture? captureToStop = null;
        lock (sync)
        {
            if (activeCapture is null)
            {
                return;
            }

            captureToStop = activeCapture;
            activeCapture = null;
            activeProcessId = 0;
            CurrentSourceDescription = $"{sourceLabel} process loopback waiting";
            bufferedWaveProvider.ClearBuffer();
        }

        captureToStop.DataAvailable -= OnCaptureDataAvailable;
        captureToStop.RecordingStopped -= OnCaptureRecordingStopped;

        try
        {
            captureToStop.StopRecording();
        }
        catch (InvalidOperationException exception)
        {
            writeLog($"{sourceLabel} process loopback stop failed.", exception);
        }
        finally
        {
            captureToStop.Dispose();
        }

        writeLog(reason, null);
    }

    private void OnCaptureDataAvailable(object? sender, WaveInEventArgs eventArgs)
    {
        if (eventArgs.BytesRecorded <= 0)
        {
            return;
        }

        lock (sync)
        {
            if (!ReferenceEquals(sender, activeCapture))
            {
                return;
            }
        }

        bufferedWaveProvider.AddSamples(eventArgs.Buffer, 0, eventArgs.BytesRecorded);
    }

    private void OnCaptureRecordingStopped(object? sender, StoppedEventArgs eventArgs)
    {
        bool wasActiveCapture = ClearActiveCapture(sender);
        if (!wasActiveCapture)
        {
            return;
        }

        bufferedWaveProvider.ClearBuffer();
        if (eventArgs.Exception is null)
        {
            writeLog($"{sourceLabel} process-only loopback stopped.", null);
        }
        else
        {
            writeLog($"{sourceLabel} process-only loopback stopped because of an audio error. It will retry automatically.", eventArgs.Exception);
        }
    }

    private bool ClearActiveCapture(object? sender)
    {
        lock (sync)
        {
            if (!ReferenceEquals(sender, activeCapture))
            {
                return false;
            }

            activeCapture = null;
            activeProcessId = 0;
            CurrentSourceDescription = $"{sourceLabel} process loopback waiting";
            return true;
        }
    }

    private TargetProcess? FindTargetProcess()
    {
        Dictionary<int, ProcessCandidate> candidatesById = [];

        for (int nameIndex = 0; nameIndex < processNames.Length; nameIndex++)
        {
            string processName = processNames[nameIndex];
            Process[] matchingProcesses;
            try
            {
                matchingProcesses = Process.GetProcessesByName(processName);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            foreach (Process process in matchingProcesses)
            {
                using (process)
                {
                    ProcessCandidate? candidate = TryCreateCandidate(process, nameIndex);
                    if (candidate is null)
                    {
                        continue;
                    }

                    if (!candidatesById.TryGetValue(candidate.ProcessId, out ProcessCandidate? previousCandidate) ||
                        candidate.SortKey.CompareTo(previousCandidate.SortKey) < 0)
                    {
                        candidatesById[candidate.ProcessId] = candidate;
                    }
                }
            }
        }

        ProcessCandidate? selectedCandidate = candidatesById.Values
            .OrderBy(static candidate => candidate.SortKey)
            .FirstOrDefault();
        if (selectedCandidate is null)
        {
            return null;
        }

        return new TargetProcess(
            selectedCandidate.ProcessId,
            $"Process: {selectedCandidate.ProcessName}. PID: {selectedCandidate.ProcessId}. Names: {string.Join(", ", processNames)}");
    }

    private static ProcessCandidate? TryCreateCandidate(Process process, int nameOrder)
    {
        try
        {
            if (process.HasExited)
            {
                return null;
            }

            DateTime startTime = DateTime.MaxValue;
            try
            {
                startTime = process.StartTime;
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            return new ProcessCandidate(
                process.Id,
                process.ProcessName,
                new ProcessSortKey(nameOrder, startTime, process.Id));
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string NormalizeProcessName(string processName)
    {
        string trimmedProcessName = processName.Trim();
        return Path.GetFileNameWithoutExtension(trimmedProcessName);
    }

    private readonly record struct ProcessSortKey(int NameOrder, DateTime StartTime, int ProcessId)
        : IComparable<ProcessSortKey>
    {
        public int CompareTo(ProcessSortKey other)
        {
            int nameOrderComparison = NameOrder.CompareTo(other.NameOrder);
            if (nameOrderComparison != 0)
            {
                return nameOrderComparison;
            }

            int startTimeComparison = StartTime.CompareTo(other.StartTime);
            if (startTimeComparison != 0)
            {
                return startTimeComparison;
            }

            return ProcessId.CompareTo(other.ProcessId);
        }
    }

    private sealed record ProcessCandidate(int ProcessId, string ProcessName, ProcessSortKey SortKey);

    private sealed record TargetProcess(int ProcessId, string Description);
}
