namespace VALOWATCH;

internal sealed class AsyncKeyStateAltTHotKeyMonitor : IDisposable
{
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private readonly Thread monitoringThread;
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

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (monitoringThread.ThreadState == ThreadState.Unstarted)
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
        while (!cancellationTokenSource.IsCancellationRequested)
        {
            bool chordIsDown = NativeMethods.IsKeyDown(NativeMethods.VirtualKeyMenu) &&
                NativeMethods.IsKeyDown((int)NativeMethods.VirtualKeyT);
            if (chordIsDown && !chordWasDown)
            {
                AltTPressed?.Invoke();
            }

            chordWasDown = chordIsDown;
            cancellationHandle.WaitOne(10);
        }
    }
}
