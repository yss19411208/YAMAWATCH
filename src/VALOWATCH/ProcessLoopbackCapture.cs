using NAudio.Wave;
using System.Runtime.InteropServices;

namespace VALOWATCH;

internal sealed class ProcessLoopbackCapture : IDisposable
{
    private const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";
    private const int AudioClientActivationTypeProcessLoopback = 1;
    private const int ProcessLoopbackModeIncludeTargetProcessTree = 0;
    private const int AudclntSharemodeShared = 0;
    private const int AudclntStreamflagsLoopback = 0x00020000;
    private const int AudclntStreamflagsEventCallback = 0x00040000;
    private const int AudclntStreamflagsSrcDefaultQuality = 0x08000000;
    private const uint AudclntBufferflagsSilent = 0x00000002;
    private const int ActivationTimeoutMilliseconds = 8000;

    private static readonly int AudclntStreamflagsAutoconvertPcm = unchecked((int)0x80000000);
    private static readonly Guid IAudioClientGuid = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IAudioCaptureClientGuid = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    private readonly int targetProcessId;
    private readonly object captureLock = new();
    private IAudioClient? audioClient;
    private IAudioCaptureClient? audioCaptureClient;
    private AutoResetEvent? sampleReadyEvent;
    private ManualResetEventSlim? stopEvent;
    private Task? captureTask;
    private bool isRecording;

    public ProcessLoopbackCapture(int targetProcessId)
    {
        if (targetProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetProcessId), "Target process id must be positive.");
        }

        this.targetProcessId = targetProcessId;
    }

    public static WaveFormat CaptureWaveFormat { get; } = new(48000, 16, 2);

    public WaveFormat WaveFormat => CaptureWaveFormat;

    public event EventHandler<WaveInEventArgs>? DataAvailable;

    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public void StartRecording()
    {
        lock (captureLock)
        {
            if (isRecording)
            {
                return;
            }

            sampleReadyEvent = new AutoResetEvent(false);
            stopEvent = new ManualResetEventSlim(false);

            try
            {
                RunOnMta(StartAudioClient);
                isRecording = true;
                captureTask = Task.Run(CaptureLoop);
            }
            catch
            {
                DisposeCaptureObjects();
                throw;
            }
        }
    }

    public void StopRecording()
    {
        Task? taskToWait;
        lock (captureLock)
        {
            if (!isRecording && captureTask is null)
            {
                return;
            }

            isRecording = false;
            stopEvent?.Set();
            taskToWait = captureTask;
        }

        try
        {
            taskToWait?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException aggregateException)
        {
            RecordingStopped?.Invoke(this, new StoppedEventArgs(aggregateException.Flatten().InnerException));
        }
        finally
        {
            lock (captureLock)
            {
                RunOnMta(DisposeCaptureObjects);
            }
        }
    }

    public void Dispose()
    {
        StopRecording();
    }

    private void CaptureLoop()
    {
        Exception? stopException = null;

        try
        {
            AutoResetEvent localSampleReadyEvent = sampleReadyEvent
                ?? throw new InvalidOperationException("Sample event is not initialized.");
            ManualResetEventSlim localStopEvent = stopEvent
                ?? throw new InvalidOperationException("Stop event is not initialized.");

            WaitHandle[] waitHandles = [localSampleReadyEvent, localStopEvent.WaitHandle];
            while (!localStopEvent.IsSet)
            {
                int signaledIndex = WaitHandle.WaitAny(waitHandles, TimeSpan.FromSeconds(1));
                if (signaledIndex == 1)
                {
                    break;
                }

                if (signaledIndex == 0)
                {
                    ReadAvailablePackets();
                }
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or InvalidCastException or ArithmeticException)
        {
            stopException = exception;
        }
        finally
        {
            try
            {
                audioClient?.Stop();
            }
            catch (COMException exception)
            {
                stopException ??= exception;
            }

            RecordingStopped?.Invoke(this, new StoppedEventArgs(stopException));
        }
    }

    private void ReadAvailablePackets()
    {
        IAudioCaptureClient captureClient = audioCaptureClient
            ?? throw new InvalidOperationException("Audio capture client is not initialized.");

        ThrowIfFailed(captureClient.GetNextPacketSize(out uint framesAvailable), "IAudioCaptureClient.GetNextPacketSize");
        while (framesAvailable > 0)
        {
            IntPtr dataPointer = IntPtr.Zero;
            uint framesToRead = 0;
            bool bufferAcquired = false;

            try
            {
                ThrowIfFailed(
                    captureClient.GetBuffer(
                        out dataPointer,
                        out framesToRead,
                        out uint captureFlags,
                        out _,
                        out _),
                    "IAudioCaptureClient.GetBuffer");
                bufferAcquired = true;

                if (framesToRead > 0)
                {
                    int bytesToRead = checked((int)(framesToRead * (uint)CaptureWaveFormat.BlockAlign));
                    byte[] buffer = new byte[bytesToRead];
                    if ((captureFlags & AudclntBufferflagsSilent) == 0 && dataPointer != IntPtr.Zero)
                    {
                        Marshal.Copy(dataPointer, buffer, 0, bytesToRead);
                    }

                    DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, bytesToRead));
                }
            }
            finally
            {
                if (bufferAcquired)
                {
                    ThrowIfFailed(captureClient.ReleaseBuffer(framesToRead), "IAudioCaptureClient.ReleaseBuffer");
                }
            }

            ThrowIfFailed(captureClient.GetNextPacketSize(out framesAvailable), "IAudioCaptureClient.GetNextPacketSize");
        }
    }

    private static IAudioClient ActivateAudioClient(int processId)
    {
        AudioClientActivationParams activationParams = new()
        {
            ActivationType = AudioClientActivationTypeProcessLoopback,
            TargetProcessId = processId,
            ProcessLoopbackMode = ProcessLoopbackModeIncludeTargetProcessTree
        };

        IntPtr activationParamsPointer = IntPtr.Zero;
        IActivateAudioInterfaceAsyncOperation? asyncOperation = null;
        ActivateAudioInterfaceCompletionHandler completionHandler = new();

        try
        {
            activationParamsPointer = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
            Marshal.StructureToPtr(activationParams, activationParamsPointer, fDeleteOld: false);
            PropVariant activatePropVariant = PropVariant.FromBlob(
                activationParamsPointer,
                Marshal.SizeOf<AudioClientActivationParams>());

            Guid audioClientGuid = IAudioClientGuid;
            int hr = ActivateAudioInterfaceAsync(
                VirtualAudioDeviceProcessLoopback,
                ref audioClientGuid,
                ref activatePropVariant,
                completionHandler,
                out asyncOperation);
            ThrowIfFailed(hr, "ActivateAudioInterfaceAsync");

            if (!completionHandler.Wait(ActivationTimeoutMilliseconds))
            {
                throw new TimeoutException("Process loopback audio activation timed out.");
            }

            return completionHandler.GetAudioClient();
        }
        finally
        {
            if (asyncOperation is not null)
            {
                Marshal.FinalReleaseComObject(asyncOperation);
            }

            if (activationParamsPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(activationParamsPointer);
            }
        }
    }

    private void StartAudioClient()
    {
        try
        {
            AutoResetEvent localSampleReadyEvent = sampleReadyEvent
                ?? throw new InvalidOperationException("Sample event is not initialized.");
            audioClient = ActivateAudioClient(targetProcessId);
            InitializeAudioClient(audioClient, localSampleReadyEvent);
            audioCaptureClient = GetAudioCaptureClient(audioClient);
            ThrowIfFailed(audioClient.Start(), "IAudioClient.Start");
        }
        catch
        {
            DisposeCaptureObjects();
            throw;
        }
    }

    private static void InitializeAudioClient(IAudioClient client, AutoResetEvent readyEvent)
    {
        IntPtr waveFormatPointer = IntPtr.Zero;
        try
        {
            waveFormatPointer = WaveFormat.MarshalToPtr(CaptureWaveFormat);
            int flags = AudclntStreamflagsLoopback |
                AudclntStreamflagsEventCallback |
                AudclntStreamflagsAutoconvertPcm |
                AudclntStreamflagsSrcDefaultQuality;

            ThrowIfFailed(
                client.Initialize(
                    AudclntSharemodeShared,
                    flags,
                    0,
                    0,
                    waveFormatPointer,
                    IntPtr.Zero),
                "IAudioClient.Initialize");
            ThrowIfFailed(client.SetEventHandle(readyEvent.SafeWaitHandle.DangerousGetHandle()), "IAudioClient.SetEventHandle");
        }
        finally
        {
            if (waveFormatPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(waveFormatPointer);
            }
        }
    }

    private static IAudioCaptureClient GetAudioCaptureClient(IAudioClient client)
    {
        Guid captureClientGuid = IAudioCaptureClientGuid;
        ThrowIfFailed(client.GetService(ref captureClientGuid, out IAudioCaptureClient service), "IAudioClient.GetService");
        return service;
    }

    private static void ThrowIfFailed(int hresult, string operation)
    {
        if (hresult < 0)
        {
            throw new COMException($"{operation} failed with HRESULT 0x{hresult:X8}.", hresult);
        }
    }

    private static void RunOnMta(Action action)
    {
        Task.Run(action).GetAwaiter().GetResult();
    }

    private void DisposeCaptureObjects()
    {
        try
        {
            audioClient?.Stop();
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException)
        {
        }

        if (audioCaptureClient is not null)
        {
            Marshal.FinalReleaseComObject(audioCaptureClient);
        }

        if (audioClient is not null)
        {
            Marshal.FinalReleaseComObject(audioClient);
        }

        audioCaptureClient = null;
        audioClient = null;
        captureTask = null;
        sampleReadyEvent?.Dispose();
        stopEvent?.Dispose();
        sampleReadyEvent = null;
        stopEvent = null;
        isRecording = false;
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        ref PropVariant activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        public int ActivationType;
        public int TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int Size;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        private const ushort VtBlob = 65;

        [FieldOffset(0)]
        private ushort valueType;

        [FieldOffset(8)]
        private Blob blob;

        public static PropVariant FromBlob(IntPtr data, int size)
        {
            return new PropVariant
            {
                valueType = VtBlob,
                blob = new Blob
                {
                    Size = size,
                    Data = data
                }
            };
        }
    }

    [ComImport]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig]
        int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig]
        int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.Interface)] out IAudioClient activatedInterface);
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig]
        int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);

        [PreserveSig]
        int GetBufferSize(out uint bufferFrames);

        [PreserveSig]
        int GetStreamLatency(out long latency);

        [PreserveSig]
        int GetCurrentPadding(out uint paddingFrames);

        [PreserveSig]
        int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);

        [PreserveSig]
        int GetMixFormat(out IntPtr deviceFormat);

        [PreserveSig]
        int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);

        [PreserveSig]
        int Start();

        [PreserveSig]
        int Stop();

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int SetEventHandle(IntPtr eventHandle);

        [PreserveSig]
        int GetService(ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IAudioCaptureClient service);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig]
        int GetBuffer(
            out IntPtr data,
            out uint framesToRead,
            out uint flags,
            out ulong devicePosition,
            out ulong qpcPosition);

        [PreserveSig]
        int ReleaseBuffer(uint framesRead);

        [PreserveSig]
        int GetNextPacketSize(out uint framesInNextPacket);
    }

    private sealed class ActivateAudioInterfaceCompletionHandler : IActivateAudioInterfaceCompletionHandler
    {
        private readonly ManualResetEventSlim activationCompletedEvent = new();
        private IAudioClient? audioClient;
        private Exception? activationException;

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                ThrowIfFailed(activateOperation.GetActivateResult(out int activateResult, out IAudioClient activatedInterface), "GetActivateResult");
                ThrowIfFailed(activateResult, "ActivateAudioInterfaceAsync result");
                audioClient = activatedInterface;
            }
            catch (Exception exception) when (exception is COMException or InvalidCastException or NullReferenceException)
            {
                activationException = exception;
            }
            finally
            {
                activationCompletedEvent.Set();
            }

            return 0;
        }

        public bool Wait(int millisecondsTimeout)
        {
            return activationCompletedEvent.Wait(millisecondsTimeout);
        }

        public IAudioClient GetAudioClient()
        {
            if (activationException is not null)
            {
                throw activationException;
            }

            return audioClient ?? throw new InvalidOperationException("Activated audio client was null.");
        }
    }
}
