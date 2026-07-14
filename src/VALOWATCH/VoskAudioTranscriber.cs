using NAudio.Wave;
using System.Text;
using System.Text.Json;
using Vosk;

namespace VALOWATCH;

internal sealed class VoskAudioTranscriber : IAudioTranscriber
{
    private const float VoskSampleRate = Pcm16Mono16KhzConverter.TargetSampleRate;
    private readonly Model model;
    private readonly object recognitionLock = new();

    public VoskAudioTranscriber(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            throw new ArgumentException("Vosk model path is required.", nameof(modelPath));
        }

        Vosk.Vosk.SetLogLevel(-1);
        model = new Model(modelPath);
        Description = $"vosk:{Path.GetFileName(modelPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}";
    }

    public string Description { get; }

    public Task<string> TranscribePcm16Async(
        WaveFormat sourceFormat,
        byte[] sourcePcmBytes,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () => TranscribePcm16(sourceFormat, sourcePcmBytes, cancellationToken),
            cancellationToken);
    }

    public void Dispose()
    {
        model.Dispose();
    }

    private string TranscribePcm16(
        WaveFormat sourceFormat,
        byte[] sourcePcmBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] mono16KhzPcmBytes = Pcm16Mono16KhzConverter.Convert(sourceFormat, sourcePcmBytes);
        if (mono16KhzPcmBytes.Length < Pcm16Mono16KhzConverter.TargetSampleRate)
        {
            return string.Empty;
        }

        lock (recognitionLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using VoskRecognizer recognizer = new(model, VoskSampleRate);
            recognizer.SetMaxAlternatives(0);
            recognizer.SetWords(false);

            StringBuilder recognizedTextBuilder = new();
            if (recognizer.AcceptWaveform(mono16KhzPcmBytes, mono16KhzPcmBytes.Length))
            {
                AppendRecognizedText(recognizedTextBuilder, recognizer.Result());
            }

            AppendRecognizedText(recognizedTextBuilder, recognizer.FinalResult());
            return NormalizeRecognizedText(recognizedTextBuilder.ToString());
        }
    }

    private static void AppendRecognizedText(StringBuilder recognizedTextBuilder, string resultJson)
    {
        string recognizedText = ExtractText(resultJson);
        if (string.IsNullOrWhiteSpace(recognizedText))
        {
            return;
        }

        if (recognizedTextBuilder.Length > 0)
        {
            recognizedTextBuilder.Append(' ');
        }

        recognizedTextBuilder.Append(recognizedText);
    }

    private static string ExtractText(string resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
        {
            return string.Empty;
        }

        using JsonDocument resultDocument = JsonDocument.Parse(resultJson);
        if (!resultDocument.RootElement.TryGetProperty("text", out JsonElement textElement) ||
            textElement.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return textElement.GetString()?.Trim() ?? string.Empty;
    }

    private static string NormalizeRecognizedText(string recognizedText)
    {
        return string.Join(
            ' ',
            recognizedText.Split(
                [' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
