using NAudio.Wave;
using System.Diagnostics;

namespace VALOWATCH;

internal sealed class LineProcessLoopbackWaveProvider : IWaveProvider, IDisposable
{
    private static readonly TimeSpan ProcessRefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SilentCandidateSwitchInterval = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan HealthLogInterval = TimeSpan.FromSeconds(30);
    private const float AudiblePeakThreshold = 0.003F;

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
    private DateTimeOffset activeCaptureStartedAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset lastAudibleCaptureAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset lastHealthLogAtUtc = DateTimeOffset.MinValue;
    private long capturedCallbackCount;
    private long capturedByteCount;
    private long capturedAudibleCallbackCount;
    private float capturedPeak;
    private bool loggedFirstAudibleCapture;

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

    public string GetStatusSummary()
    {
        lock (sync)
        {
            return
                $"{sourceLabel}LoopbackCapturing: {activeCapture is not null}. " +
                $"{sourceLabel}Pid: {activeProcessId}. " +
                $"{sourceLabel}Callbacks: {capturedCallbackCount}. " +
                $"{sourceLabel}AudibleCallbacks: {capturedAudibleCallbackCount}. " +
                $"{sourceLabel}Peak: {capturedPeak:0.0000}. " +
                $"{sourceLabel}BufferedMs: {bufferedWaveProvider.BufferedDuration.TotalMilliseconds:0}.";
        }
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

        IReadOnlyList<TargetProcess> targetProcesses = FindTargetProcesses();
        if (targetProcesses.Count == 0)
        {
            StopActiveCapture($"{sourceLabel} process is not running. Waiting for {sourceLabel} audio.");
            CurrentSourceDescription = $"{sourceLabel} process loopback waiting";
            return;
        }

        TargetProcess? nextTargetProcess = null;
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            TargetProcess? currentTargetProcess = targetProcesses
                .FirstOrDefault(targetProcess => targetProcess.ProcessId == activeProcessId);
            if (activeCapture is not null && currentTargetProcess is not null)
            {
                DateTimeOffset lastSignalUtc = lastAudibleCaptureAtUtc == DateTimeOffset.MinValue
                    ? activeCaptureStartedAtUtc
                    : lastAudibleCaptureAtUtc;
                bool remainedSilent = nowUtc - lastSignalUtc >= SilentCandidateSwitchInterval;
                if (!remainedSilent || targetProcesses.Count == 1)
                {
                    MaybeWriteHealthLogUnsafe(nowUtc);
                    return;
                }

                nextTargetProcess = SelectNextTargetProcess(targetProcesses, activeProcessId);
                writeLog(
                    $"{sourceLabel} process loopback remained silent; trying another candidate. " +
                    $"CurrentPid: {activeProcessId}. NextPid: {nextTargetProcess.ProcessId}. " +
                    $"CandidateCount: {targetProcesses.Count}.",
                    null);
            }
        }

        TargetProcess selectedTargetProcess = nextTargetProcess ?? targetProcesses[0];
        StopActiveCapture($"Switching {sourceLabel} process loopback to PID {selectedTargetProcess.ProcessId}.");
        StartCaptureForProcess(selectedTargetProcess);
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
                ResetCaptureStatsUnsafe();
                activeCaptureStartedAtUtc = DateTimeOffset.UtcNow;
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
            ResetCaptureStatsUnsafe();
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
        float peak = DiscordBotVoiceRelay.CalculateAudioPeak(
            ProcessLoopbackCapture.CaptureWaveFormat,
            eventArgs.Buffer,
            0,
            eventArgs.BytesRecorded);
        bool shouldLogFirstAudible = false;
        int logProcessId = 0;
        lock (sync)
        {
            capturedCallbackCount++;
            capturedByteCount += eventArgs.BytesRecorded;
            capturedPeak = Math.Max(capturedPeak, peak);
            if (peak >= AudiblePeakThreshold)
            {
                capturedAudibleCallbackCount++;
                lastAudibleCaptureAtUtc = DateTimeOffset.UtcNow;
                if (!loggedFirstAudibleCapture)
                {
                    loggedFirstAudibleCapture = true;
                    shouldLogFirstAudible = true;
                    logProcessId = activeProcessId;
                }
            }
        }

        if (shouldLogFirstAudible)
        {
            writeLog($"{sourceLabel} process-only loopback became audible. PID: {logProcessId}. Peak: {peak:0.0000}.", null);
        }
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
            ResetCaptureStatsUnsafe();
            return true;
        }
    }

    private IReadOnlyList<TargetProcess> FindTargetProcesses()
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

        if (ShouldDiscoverRelatedLineProcesses())
        {
            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    if (!LooksLikeRelatedLineProcess(process.ProcessName))
                    {
                        continue;
                    }

                    ProcessCandidate? candidate = TryCreateCandidate(process, processNames.Length);
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

        return candidatesById.Values
            .OrderBy(static candidate => candidate.SortKey)
            .Select(candidate => new TargetProcess(
                candidate.ProcessId,
                $"Process: {candidate.ProcessName}. PID: {candidate.ProcessId}. Names: {string.Join(", ", processNames)}"))
            .ToArray();
    }

    private TargetProcess SelectNextTargetProcess(IReadOnlyList<TargetProcess> targetProcesses, int currentProcessId)
    {
        int currentIndex = -1;
        for (int processIndex = 0; processIndex < targetProcesses.Count; processIndex++)
        {
            if (targetProcesses[processIndex].ProcessId == currentProcessId)
            {
                currentIndex = processIndex;
                break;
            }
        }

        if (currentIndex < 0)
        {
            return targetProcesses[0];
        }

        return targetProcesses[(currentIndex + 1) % targetProcesses.Count];
    }

    private void MaybeWriteHealthLogUnsafe(DateTimeOffset nowUtc)
    {
        if (nowUtc - lastHealthLogAtUtc < HealthLogInterval)
        {
            return;
        }

        lastHealthLogAtUtc = nowUtc;
        writeLog($"{sourceLabel} process loopback status. {GetStatusSummary()} Source: {CurrentSourceDescription}.", null);
    }

    private void ResetCaptureStatsUnsafe()
    {
        capturedCallbackCount = 0;
        capturedByteCount = 0;
        capturedAudibleCallbackCount = 0;
        capturedPeak = 0F;
        loggedFirstAudibleCapture = false;
        lastAudibleCaptureAtUtc = DateTimeOffset.MinValue;
        activeCaptureStartedAtUtc = DateTimeOffset.MinValue;
        lastHealthLogAtUtc = DateTimeOffset.MinValue;
    }

    private bool ShouldDiscoverRelatedLineProcesses()
    {
        return string.Equals(sourceLabel, "LINE", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeRelatedLineProcess(string processName)
    {
        string normalizedProcessName = NormalizeProcessName(processName);
        return normalizedProcessName.StartsWith("line", StringComparison.OrdinalIgnoreCase);
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
