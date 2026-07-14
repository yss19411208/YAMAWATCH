using NAudio.Wave;

namespace VALOWATCH;

internal interface IAudioTranscriber : IDisposable
{
    string Description { get; }

    Task<string> TranscribePcm16Async(
        WaveFormat sourceFormat,
        byte[] sourcePcmBytes,
        CancellationToken cancellationToken);
}
