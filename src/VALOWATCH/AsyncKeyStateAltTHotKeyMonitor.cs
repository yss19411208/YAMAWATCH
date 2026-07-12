namespace VALOWATCH;

internal sealed class AsyncKeyStateAltTHotKeyMonitor : IDisposable
{
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private readonly Thread monitoringThread;
    private long heartbeatCount;
    private long detectedChordCount;
    private long lastHeartbeatTick;
    private bool disposed;

    public AsyncKeyStateAltTHotKeyMonitor()
    {
        monitoringThread = new Thread(MonitorKeyState)
        {
            IsBackground = true,
            Name = "VALOWATCH Alt+T key-state monitor"
        };
    }

    public event Action? AltTPressed;

    public long HeartbeatCount => Interlocked.Read(ref heartbeatCount);

    public long DetectedChordCount => Interlocked.Read(ref detectedChordCount);

    public bool IsResponsive => monitoringThread.IsAlive &&
        Environment.TickCount64 - Interlocked.Read(ref lastHeartbeatTick) < 2000;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if ((monitoringThread.ThreadState & ThreadState.Unstarted) != 0)
        {
            monitoringThread.Start();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        cancellationTokenSource.Cancel();
        if (monitoringThread.IsAlive && Thread.CurrentThread != monitoringThread)
        {
            monitoringThread.Join(TimeSpan.FromSeconds(2));
        }

        cancellationTokenSource.Dispose();
    }

    private void MonitorKeyState()
    {
        bool chordWasDown = false;
        WaitHandle cancellationHandle = cancellationTokenSource.Token.WaitHandle;
        Interlocked.Exchange(ref lastHeartbeatTick, Environment.TickCount64);
        while (!cancellationTokenSource.IsCancellationRequested)
        {
            Interlocked.Increment(ref heartbeatCount);
            Interlocked.Exchange(ref lastHeartbeatTick, Environment.TickCount64);
            bool chordIsDown = NativeMethods.IsKeyDown(NativeMethods.VirtualKeyMenu) &&
                NativeMethods.IsKeyDown((int)NativeMethods.VirtualKeyT);
            if (chordIsDown && !chordWasDown)
            {
                Interlocked.Increment(ref detectedChordCount);
                try
                {
                    AltTPressed?.Invoke();
                }
                catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
                {
                }
            }

            chordWasDown = chordIsDown;
            cancellationHandle.WaitOne(10);
        }
    }
}
