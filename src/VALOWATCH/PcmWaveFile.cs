using NAudio.Wave;
using System.Text;

namespace VALOWATCH;

internal static class PcmWaveFile
{
    public static byte[] CreatePcm16WaveFile(WaveFormat waveFormat, byte[] pcmBytes, int byteCount)
    {
        if (waveFormat.Encoding != WaveFormatEncoding.Pcm ||
            waveFormat.BitsPerSample != 16)
        {
            throw new InvalidOperationException(
                $"Transcription WAV export supports PCM16 only. Format: {waveFormat}.");
        }

        if (byteCount < 0 || byteCount > pcmBytes.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(byteCount));
        }

        if (waveFormat.BlockAlign <= 0 || byteCount % waveFormat.BlockAlign != 0)
        {
            throw new InvalidOperationException(
                $"PCM byte count must align to whole audio frames. Bytes: {byteCount}. BlockAlign: {waveFormat.BlockAlign}.");
        }

        const int riffHeaderBytes = 44;
        int riffChunkSize = 36 + byteCount;
        using MemoryStream waveStream = new(riffHeaderBytes + byteCount);
        using BinaryWriter writer = new(waveStream, Encoding.ASCII, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(riffChunkSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)waveFormat.Channels);
        writer.Write(waveFormat.SampleRate);
        writer.Write(waveFormat.AverageBytesPerSecond);
        writer.Write((short)waveFormat.BlockAlign);
        writer.Write((short)waveFormat.BitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(byteCount);
        writer.Write(pcmBytes, 0, byteCount);
        writer.Flush();

        return waveStream.ToArray();
    }
}
