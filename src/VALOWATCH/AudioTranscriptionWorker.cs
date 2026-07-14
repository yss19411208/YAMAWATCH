using Discord;
using NAudio.Wave;
using System.Text.Json;
using System.Threading.Channels;

namespace VALOWATCH;

internal sealed class AudioTranscriptionWorker : IAsyncDisposable
{
    private const int DiscordEmbedDescriptionLimit = 4096;
    private const int DiscordEmbedDescriptionSafetyMargin = 160;
    private static readonly TimeSpan WorkerShutdownTimeout = TimeSpan.FromSeconds(3);

    private readonly WaveFormat waveFormat;
    private readonly int targetChunkBytes;
    private readonly float minimumPeak;
    private readonly IMessageChannel destinationChannel;
    private readonly IAudioTranscriber transcriber;
    private readonly Action<string, Exception?> writeLog;
    private readonly Func<string>? conversationLabelProvider;
    private readonly Channel<AudioTranscriptionChunk> chunkChannel;
    private readonly CancellationTokenSource workerCancellationTokenSource = new();
    private readonly Task workerTask;
    private readonly object bufferLock = new();
    private MemoryStream currentPcmBuffer = new();
    private float currentPeak;
    private DateTimeOffset lastDroppedChunkLogAt = DateTimeOffset.MinValue;

    public AudioTranscriptionWorker(
        WaveFormat waveFormat,
        TimeSpan chunkDuration,
        float minimumPeak,
        IMessageChannel destinationChannel,
        IAudioTranscriber transcriber,
        Action<string, Exception?> writeLog,
        Func<string>? conversationLabelProvider = null)
    {
        this.waveFormat = waveFormat;
        this.minimumPeak = minimumPeak;
        this.destinationChannel = destinationChannel;
        this.transcriber = transcriber;
        this.writeLog = writeLog;
        this.conversationLabelProvider = conversationLabelProvider;

        targetChunkBytes = CalculateTargetChunkBytes(waveFormat, chunkDuration);
        chunkChannel = Channel.CreateBounded<AudioTranscriptionChunk>(new BoundedChannelOptions(2)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
        workerTask = Task.Run(() => ProcessChunksAsync(workerCancellationTokenSource.Token));
    }

    internal static int CalculateTargetChunkBytes(WaveFormat waveFormat, TimeSpan chunkDuration)
    {
        int safeChunkMilliseconds = Math.Clamp((int)chunkDuration.TotalMilliseconds, 5_000, 30_000);
        long rawTargetChunkBytes = (long)waveFormat.AverageBytesPerSecond * safeChunkMilliseconds / 1000L;
        int blockAlign = Math.Max(1, waveFormat.BlockAlign);
        long alignedTargetChunkBytes = rawTargetChunkBytes - rawTargetChunkBytes % blockAlign;
        return (int)Math.Clamp(alignedTargetChunkBytes, blockAlign, int.MaxValue);
    }

    public void ObservePcmFrame(byte[] frameBuffer, int byteCount)
    {
        if (byteCount <= 0)
        {
            return;
        }

        int safeByteCount = Math.Min(byteCount, frameBuffer.Length);
        float framePeak = DiscordBotVoiceRelay.CalculateAudioPeak(
            waveFormat,
            frameBuffer,
            0,
            safeByteCount);

        lock (bufferLock)
        {
            currentPcmBuffer.Write(frameBuffer, 0, safeByteCount);
            currentPeak = Math.Max(currentPeak, framePeak);
            if (currentPcmBuffer.Length >= targetChunkBytes)
            {
                FlushCurrentChunkLocked();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        AudioTranscriptionChunk? finalChunk = null;
        lock (bufferLock)
        {
            finalChunk = TakeCurrentChunkLocked();
        }

        if (finalChunk is not null)
        {
            chunkChannel.Writer.TryWrite(finalChunk);
        }

        chunkChannel.Writer.TryComplete();
        try
        {
            await workerTask.WaitAsync(WorkerShutdownTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await workerCancellationTokenSource.CancelAsync().ConfigureAwait(false);
            try
            {
                await workerTask.WaitAsync(WorkerShutdownTimeout).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
            {
                writeLog("Audio transcription worker did not stop quickly; pending transcription was canceled.", null);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            writeLog("Audio transcription worker ended with an error during cleanup.", exception);
        }
        finally
        {
            workerCancellationTokenSource.Dispose();
            currentPcmBuffer.Dispose();
            transcriber.Dispose();
        }
    }

    private void FlushCurrentChunkLocked()
    {
        AudioTranscriptionChunk? chunk = TakeCurrentChunkLocked();
        if (chunk is null)
        {
            return;
        }

        if (!chunkChannel.Writer.TryWrite(chunk))
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (now - lastDroppedChunkLogAt > TimeSpan.FromMinutes(1))
            {
                lastDroppedChunkLogAt = now;
                writeLog("Audio transcription queue was full; an older chunk was dropped to keep voice relay realtime.", null);
            }
        }
    }

    private AudioTranscriptionChunk? TakeCurrentChunkLocked()
    {
        if (currentPcmBuffer.Length == 0)
        {
            return null;
        }

        byte[] pcmBytes = currentPcmBuffer.ToArray();
        float peak = currentPeak;
        currentPcmBuffer.Dispose();
        currentPcmBuffer = new MemoryStream();
        currentPeak = 0F;

        return new AudioTranscriptionChunk(pcmBytes, peak);
    }

    private async Task ProcessChunksAsync(CancellationToken cancellationToken)
    {
        await foreach (AudioTranscriptionChunk chunk in chunkChannel.Reader.ReadAllAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            if (chunk.Peak < minimumPeak)
            {
                continue;
            }

            try
            {
                string transcript = await transcriber
                    .TranscribePcm16Async(waveFormat, chunk.PcmBytes, cancellationToken)
                    .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(transcript))
                {
                    continue;
                }

                await destinationChannel
                    .SendMessageAsync(
                        embed: BuildTranscriptionEmbed(transcript),
                        allowedMentions: AllowedMentions.None)
                    .ConfigureAwait(false);
                writeLog($"Audio transcription sent. Characters: {transcript.Length}.", null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException)
            {
                writeLog("Audio transcription chunk failed; worker will continue with the next chunk.", exception);
            }
        }
    }

    private Embed BuildTranscriptionEmbed(string transcript)
    {
        EmbedBuilder embedBuilder = new()
        {
            Title = "VALOWATCH 文字起こし",
            Description = TrimForDiscordEmbed(transcript),
            Color = new Discord.Color(88, 166, 255),
            Timestamp = DateTimeOffset.Now
        };

        string conversationLabel = conversationLabelProvider?.Invoke() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(conversationLabel))
        {
            embedBuilder.AddField("会話", TrimEmbedField(conversationLabel), inline: false);
        }

        return embedBuilder.Build();
    }

    private static string TrimForDiscordEmbed(string text)
    {
        string trimmedText = text.Trim();
        int maximumLength = DiscordEmbedDescriptionLimit - DiscordEmbedDescriptionSafetyMargin;
        return trimmedText.Length <= maximumLength
            ? trimmedText
            : trimmedText[..maximumLength] + "...";
    }

    private static string TrimEmbedField(string text)
    {
        string trimmedText = text.Trim();
        const int maximumFieldLength = 1024;
        return trimmedText.Length <= maximumFieldLength
            ? trimmedText
            : trimmedText[..(maximumFieldLength - 3)] + "...";
    }

    private sealed record AudioTranscriptionChunk(byte[] PcmBytes, float Peak);
}
