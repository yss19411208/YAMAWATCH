using NAudio.Wave;

namespace VALOWATCH;

internal static class Pcm16Mono16KhzConverter
{
    public const int TargetSampleRate = 16000;
    public const int TargetChannelCount = 1;
    public const int TargetBitsPerSample = 16;

    public static byte[] Convert(WaveFormat sourceFormat, byte[] sourcePcmBytes)
    {
        if (sourceFormat.BitsPerSample != TargetBitsPerSample)
        {
            throw new InvalidOperationException(
                $"Offline transcription requires 16-bit PCM input. Format: {sourceFormat}.");
        }

        if (sourceFormat.Channels <= 0 || sourceFormat.SampleRate <= 0)
        {
            throw new InvalidOperationException(
                $"Offline transcription received an invalid PCM format. Format: {sourceFormat}.");
        }

        int sourceBlockAlign = sourceFormat.BlockAlign;
        if (sourceBlockAlign <= 0)
        {
            throw new InvalidOperationException(
                $"Offline transcription received an invalid block alignment. Format: {sourceFormat}.");
        }

        int sourceFrameCount = sourcePcmBytes.Length / sourceBlockAlign;
        if (sourceFrameCount == 0)
        {
            return [];
        }

        int targetFrameCount = (int)((long)sourceFrameCount * TargetSampleRate / sourceFormat.SampleRate);
        if (targetFrameCount <= 0)
        {
            return [];
        }

        byte[] targetPcmBytes = new byte[targetFrameCount * sizeof(short)];
        for (int targetFrameIndex = 0; targetFrameIndex < targetFrameCount; targetFrameIndex++)
        {
            int sourceStartFrameIndex = (int)((long)targetFrameIndex * sourceFormat.SampleRate / TargetSampleRate);
            int sourceEndFrameIndex = (int)((long)(targetFrameIndex + 1) * sourceFormat.SampleRate / TargetSampleRate);
            if (sourceEndFrameIndex <= sourceStartFrameIndex)
            {
                sourceEndFrameIndex = Math.Min(sourceStartFrameIndex + 1, sourceFrameCount);
            }

            long sampleTotal = 0;
            int sampleCount = 0;
            for (int sourceFrameIndex = sourceStartFrameIndex;
                 sourceFrameIndex < sourceEndFrameIndex && sourceFrameIndex < sourceFrameCount;
                 sourceFrameIndex++)
            {
                int frameByteOffset = sourceFrameIndex * sourceBlockAlign;
                for (int channelIndex = 0; channelIndex < sourceFormat.Channels; channelIndex++)
                {
                    int sampleByteOffset = frameByteOffset + channelIndex * sizeof(short);
                    if (sampleByteOffset + 1 >= sourcePcmBytes.Length)
                    {
                        break;
                    }

                    short sourceSample = BitConverter.ToInt16(sourcePcmBytes, sampleByteOffset);
                    sampleTotal += sourceSample;
                    sampleCount++;
                }
            }

            short targetSample = sampleCount == 0
                ? (short)0
                : ClampToInt16(sampleTotal / sampleCount);
            int targetByteOffset = targetFrameIndex * sizeof(short);
            targetPcmBytes[targetByteOffset] = (byte)(targetSample & 0xFF);
            targetPcmBytes[targetByteOffset + 1] = (byte)((targetSample >> 8) & 0xFF);
        }

        return targetPcmBytes;
    }

    private static short ClampToInt16(long sampleValue)
    {
        if (sampleValue > short.MaxValue)
        {
            return short.MaxValue;
        }

        if (sampleValue < short.MinValue)
        {
            return short.MinValue;
        }

        return (short)sampleValue;
    }
}
