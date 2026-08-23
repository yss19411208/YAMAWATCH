using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace VALOWATCH;

internal sealed class ResourceLoadTestController : IDisposable
{
    private const int CpuWorkerWindowMilliseconds = 100;
    private const int MemoryChunkBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan MemoryControlInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ShutdownWaitTimeout = TimeSpan.FromSeconds(2);

    private readonly object stateLock = new();
    private readonly object memoryLock = new();
    private readonly List<byte[]> allocatedMemoryChunks = [];
    private readonly ResourceLoadTestLimitStore limitStore;
    private readonly Action<string, Exception?> writeLog;
    private CancellationTokenSource? activeCancellationTokenSource;
    private Task? activeTask;
    private ResourceLoadTestStatus currentStatus;

    public ResourceLoadTestController(AppPaths appPaths, Action<string, Exception?> writeLog)
    {
        limitStore = new ResourceLoadTestLimitStore(appPaths);
        this.writeLog = writeLog;
        currentStatus = ResourceLoadTestStatus.Stopped(limitStore.Load());
    }

    public ResourceLoadTestLimits LoadLimits()
    {
        return limitStore.Load();
    }

    public ResourceLoadTestLimits SaveLimits(ResourceLoadTestLimits requestedLimits)
    {
        ResourceLoadTestLimits savedLimits = limitStore.Save(requestedLimits.Normalize());
        lock (stateLock)
        {
            currentStatus = currentStatus with { Limits = savedLimits };
        }

        return savedLimits;
    }

    public ResourceLoadTestStartResult Start(ResourceLoadTestRequest request)
    {
        ResourceLoadTestLimits limits = limitStore.Load();
        ResourceLoadTestRequest effectiveRequest = request.Clamp(limits);
        ResourceLoadTestStatus statusSnapshot;

        lock (stateLock)
        {
            if (activeTask is { IsCompleted: false })
            {
                statusSnapshot = currentStatus with { Limits = limits };
                return new ResourceLoadTestStartResult(
                    false,
                    "既存の負荷テストが実行中、または停止処理中です。",
                    request,
                    effectiveRequest,
                    statusSnapshot);
            }

            activeCancellationTokenSource?.Dispose();
            activeCancellationTokenSource = new CancellationTokenSource(
                TimeSpan.FromMinutes(effectiveRequest.DurationMinutes));

            DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
            DateTimeOffset stopsAtUtc = startedAtUtc.AddMinutes(effectiveRequest.DurationMinutes);
            currentStatus = new ResourceLoadTestStatus(
                true,
                effectiveRequest.CpuPercent,
                effectiveRequest.MemoryPercent,
                effectiveRequest.DurationMinutes,
                startedAtUtc,
                stopsAtUtc,
                null,
                null,
                0,
                GetCurrentMemoryLoadPercent(),
                limits);

            activeTask = Task.Run(
                () => RunLoadTestAsync(effectiveRequest, activeCancellationTokenSource.Token),
                activeCancellationTokenSource.Token);
            statusSnapshot = currentStatus;
        }

        writeLog(
            "Resource load test started. " +
            $"RequestedCpu: {request.CpuPercent}. EffectiveCpu: {effectiveRequest.CpuPercent}. " +
            $"RequestedMemory: {request.MemoryPercent}. EffectiveMemory: {effectiveRequest.MemoryPercent}. " +
            $"DurationMinutes: {effectiveRequest.DurationMinutes}.",
            null);

        return new ResourceLoadTestStartResult(
            true,
            "負荷テストを開始しました。",
            request,
            effectiveRequest,
            statusSnapshot);
    }

    public ResourceLoadTestStatus Stop(string reason)
    {
        CancellationTokenSource? cancellationTokenSource;
        lock (stateLock)
        {
            cancellationTokenSource = activeCancellationTokenSource;
            if (!currentStatus.IsRunning && activeTask is not { IsCompleted: false })
            {
                return currentStatus;
            }

            currentStatus = currentStatus with
            {
                IsRunning = false,
                StoppedAtUtc = DateTimeOffset.UtcNow,
                StopReason = reason
            };
        }

        try
        {
            cancellationTokenSource?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        ReleaseAllocatedMemory();
        writeLog($"Resource load test stop requested. Reason: {reason}.", null);
        return CaptureStatus();
    }

    public ResourceLoadTestStatus CaptureStatus()
    {
        lock (stateLock)
        {
            return currentStatus with
            {
                AllocatedMemoryBytes = GetAllocatedMemoryBytes(),
                LastObservedMemoryPercent = GetCurrentMemoryLoadPercent(),
                Limits = limitStore.Load()
            };
        }
    }

    public void Dispose()
    {
        Stop("VALOWATCH is closing");
        try
        {
            activeTask?.Wait(ShutdownWaitTimeout);
        }
        catch (Exception exception) when (exception is AggregateException or InvalidOperationException)
        {
            writeLog("Resource load test did not finish before dispose timeout.", exception);
        }

        activeCancellationTokenSource?.Dispose();
        ReleaseAllocatedMemory();
    }

    private async Task RunLoadTestAsync(ResourceLoadTestRequest request, CancellationToken cancellationToken)
    {
        try
        {
            Task cpuTask = Task.Run(() => RunCpuLoad(request.CpuPercent, cancellationToken), cancellationToken);
            Task memoryTask = RunMemoryLoadAsync(request.MemoryPercent, cancellationToken);
            await Task.WhenAll(cpuTask, memoryTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is OutOfMemoryException or InvalidOperationException)
        {
            writeLog("Resource load test stopped after an internal failure.", exception);
        }
        finally
        {
            ReleaseAllocatedMemory();
            lock (stateLock)
            {
                if (currentStatus.IsRunning)
                {
                    currentStatus = currentStatus with
                    {
                        IsRunning = false,
                        StoppedAtUtc = DateTimeOffset.UtcNow,
                        StopReason = "duration elapsed"
                    };
                }
            }

            activeCancellationTokenSource?.Dispose();
            activeCancellationTokenSource = null;
            writeLog("Resource load test finished.", null);
        }
    }

    private static void RunCpuLoad(int cpuPercent, CancellationToken cancellationToken)
    {
        if (cpuPercent <= 0)
        {
            WaitUntilCancelled(cancellationToken);
            return;
        }

        int workerCount = Math.Max(1, Environment.ProcessorCount);
        Task[] workers = new Task[workerCount];
        for (int workerIndex = 0; workerIndex < workers.Length; workerIndex++)
        {
            workers[workerIndex] = Task.Factory.StartNew(
                () => RunCpuWorker(cpuPercent, cancellationToken),
                cancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        Task.WaitAll(workers);
    }

    private static void RunCpuWorker(int cpuPercent, CancellationToken cancellationToken)
    {
        long windowTicks = Math.Max(1, Stopwatch.Frequency * CpuWorkerWindowMilliseconds / 1000);
        long activeTicks = Math.Clamp(cpuPercent, 0, ResourceLoadTestLimits.HardMaxCpuPercent) * windowTicks / 100;
        Stopwatch stopwatch = Stopwatch.StartNew();

        while (!cancellationToken.IsCancellationRequested)
        {
            long windowStartTicks = stopwatch.ElapsedTicks;
            while (stopwatch.ElapsedTicks - windowStartTicks < activeTicks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Thread.SpinWait(256);
            }

            long elapsedWindowTicks = stopwatch.ElapsedTicks - windowStartTicks;
            long remainingTicks = windowTicks - elapsedWindowTicks;
            if (remainingTicks <= 0)
            {
                Thread.Yield();
                continue;
            }

            int sleepMilliseconds = (int)Math.Max(
                1,
                Math.Floor(remainingTicks * 1000D / Stopwatch.Frequency));
            if (cancellationToken.WaitHandle.WaitOne(sleepMilliseconds))
            {
                break;
            }
        }
    }

    private static void WaitUntilCancelled(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (cancellationToken.WaitHandle.WaitOne(1000))
            {
                break;
            }
        }
    }

    private async Task RunMemoryLoadAsync(int memoryPercent, CancellationToken cancellationToken)
    {
        if (memoryPercent <= 0)
        {
            await WaitUntilCancelledAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            MemoryStatusSnapshot memoryStatus = CaptureMemoryStatus();
            if (memoryStatus.MemoryLoadPercent + 1 < memoryPercent)
            {
                TryAllocateMemoryChunk(memoryStatus);
            }
            else if (memoryStatus.MemoryLoadPercent > memoryPercent + 3)
            {
                ReleaseOneMemoryChunk();
            }

            UpdateMemoryStatus(memoryStatus.MemoryLoadPercent);
            await Task.Delay(MemoryControlInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WaitUntilCancelledAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void TryAllocateMemoryChunk(MemoryStatusSnapshot memoryStatus)
    {
        ulong reserveBytes = Math.Max(512UL * 1024UL * 1024UL, memoryStatus.TotalPhysicalBytes / 20UL);
        if (memoryStatus.AvailablePhysicalBytes <= reserveBytes + MemoryChunkBytes)
        {
            return;
        }

        try
        {
            byte[] chunk = new byte[MemoryChunkBytes];
            for (int byteIndex = 0; byteIndex < chunk.Length; byteIndex += 4096)
            {
                chunk[byteIndex] = 1;
            }

            chunk[^1] = 1;
            lock (memoryLock)
            {
                allocatedMemoryChunks.Add(chunk);
            }

            UpdateMemoryStatus(memoryStatus.MemoryLoadPercent);
        }
        catch (OutOfMemoryException exception)
        {
            writeLog("Resource load test memory allocation stopped before target.", exception);
        }
    }

    private void ReleaseOneMemoryChunk()
    {
        lock (memoryLock)
        {
            if (allocatedMemoryChunks.Count > 0)
            {
                allocatedMemoryChunks.RemoveAt(allocatedMemoryChunks.Count - 1);
            }
        }
    }

    private void ReleaseAllocatedMemory()
    {
        lock (memoryLock)
        {
            allocatedMemoryChunks.Clear();
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: false);
    }

    private void UpdateMemoryStatus(int memoryLoadPercent)
    {
        lock (stateLock)
        {
            currentStatus = currentStatus with
            {
                AllocatedMemoryBytes = GetAllocatedMemoryBytes(),
                LastObservedMemoryPercent = memoryLoadPercent
            };
        }
    }

    private long GetAllocatedMemoryBytes()
    {
        lock (memoryLock)
        {
            return (long)allocatedMemoryChunks.Count * MemoryChunkBytes;
        }
    }

    private static int GetCurrentMemoryLoadPercent()
    {
        return CaptureMemoryStatus().MemoryLoadPercent;
    }

    private static MemoryStatusSnapshot CaptureMemoryStatus()
    {
        MemoryStatusEx memoryStatus = new()
        {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
        };
        if (!GlobalMemoryStatusEx(ref memoryStatus))
        {
            return new MemoryStatusSnapshot(0, 0, 0);
        }

        int memoryLoadPercent = (int)Math.Clamp(memoryStatus.MemoryLoad, 0U, 100U);
        return new MemoryStatusSnapshot(
            memoryLoadPercent,
            memoryStatus.TotalPhys,
            memoryStatus.AvailPhys);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx memoryStatus);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    private sealed record MemoryStatusSnapshot(
        int MemoryLoadPercent,
        ulong TotalPhysicalBytes,
        ulong AvailablePhysicalBytes);
}

internal sealed class ResourceLoadTestLimitStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string limitPath;

    public ResourceLoadTestLimitStore(AppPaths appPaths)
    {
        limitPath = Path.Combine(appPaths.ConfigDirectory, "resource-load-test-limits.json");
    }

    public ResourceLoadTestLimits Load()
    {
        try
        {
            if (!File.Exists(limitPath))
            {
                return ResourceLoadTestLimits.Default;
            }

            string json = File.ReadAllText(limitPath);
            ResourceLoadTestLimits? limits = JsonSerializer.Deserialize<ResourceLoadTestLimits>(json, JsonOptions);
            return (limits ?? ResourceLoadTestLimits.Default).Normalize();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return ResourceLoadTestLimits.Default;
        }
    }

    public ResourceLoadTestLimits Save(ResourceLoadTestLimits limits)
    {
        ResourceLoadTestLimits normalizedLimits = limits.Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(limitPath) ?? AppContext.BaseDirectory);
        string tempPath = $"{limitPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        string json = JsonSerializer.Serialize(normalizedLimits, JsonOptions);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, limitPath, overwrite: true);
        return normalizedLimits;
    }
}

internal sealed record ResourceLoadTestLimits(
    int MaxCpuPercent,
    int MaxMemoryPercent,
    int MaxDurationMinutes)
{
    public const int HardMaxCpuPercent = 95;
    public const int HardMaxMemoryPercent = 90;
    public const int HardMaxDurationMinutes = 60;
    public const int DefaultMaxCpuPercent = 80;
    public const int DefaultMaxMemoryPercent = 70;
    public const int DefaultMaxDurationMinutes = 10;

    public static ResourceLoadTestLimits Default { get; } = new(
        DefaultMaxCpuPercent,
        DefaultMaxMemoryPercent,
        DefaultMaxDurationMinutes);

    public ResourceLoadTestLimits Normalize()
    {
        return new ResourceLoadTestLimits(
            Math.Clamp(MaxCpuPercent, 1, HardMaxCpuPercent),
            Math.Clamp(MaxMemoryPercent, 1, HardMaxMemoryPercent),
            Math.Clamp(MaxDurationMinutes, 1, HardMaxDurationMinutes));
    }
}

internal sealed record ResourceLoadTestRequest(
    int CpuPercent,
    int MemoryPercent,
    int DurationMinutes)
{
    public ResourceLoadTestRequest Clamp(ResourceLoadTestLimits limits)
    {
        ResourceLoadTestLimits normalizedLimits = limits.Normalize();
        return new ResourceLoadTestRequest(
            Math.Clamp(CpuPercent, 0, normalizedLimits.MaxCpuPercent),
            Math.Clamp(MemoryPercent, 0, normalizedLimits.MaxMemoryPercent),
            Math.Clamp(DurationMinutes, 1, normalizedLimits.MaxDurationMinutes));
    }
}

internal sealed record ResourceLoadTestStartResult(
    bool Started,
    string Message,
    ResourceLoadTestRequest Requested,
    ResourceLoadTestRequest Effective,
    ResourceLoadTestStatus Status);

internal sealed record ResourceLoadTestStatus(
    bool IsRunning,
    int CpuTargetPercent,
    int MemoryTargetPercent,
    int DurationMinutes,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? StopsAtUtc,
    DateTimeOffset? StoppedAtUtc,
    string? StopReason,
    long AllocatedMemoryBytes,
    int LastObservedMemoryPercent,
    ResourceLoadTestLimits Limits)
{
    public static ResourceLoadTestStatus Stopped(ResourceLoadTestLimits limits)
    {
        return new ResourceLoadTestStatus(
            false,
            0,
            0,
            0,
            null,
            null,
            null,
            null,
            0,
            0,
            limits.Normalize());
    }
}
