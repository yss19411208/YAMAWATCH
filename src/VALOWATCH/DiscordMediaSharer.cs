using Discord;
using Discord.Rest;
using System.Diagnostics;
using System.Text;

namespace VALOWATCH;

public sealed class DiscordMediaSharer
{
    private static readonly TimeSpan MediaConversionTimeout = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan DiscordUploadTimeout = TimeSpan.FromMinutes(3);

    private readonly AppPaths appPaths;
    private readonly DiscordBotSettingsStore settingsStore;

    public DiscordMediaSharer(AppPaths appPaths, DiscordBotSettingsStore settingsStore)
    {
        this.appPaths = appPaths;
        this.settingsStore = settingsStore;
        settingsStore.EnsureSampleConfig();
    }

    public async Task<DiscordMediaShareResult> ShareAudioRecordingAsync(
        RecordingHistoryEntry recordingEntry,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(recordingEntry.FilePath))
        {
            return new DiscordMediaShareResult(true, false, $"Audio file missing: {recordingEntry.FilePath}");
        }

        DiscordBotSettings? settings = settingsStore.Load(out string statusText);
        DiscordMediaShareResult? skippedResult = ValidateShareSettings(settings, statusText, requireAudio: true);
        if (skippedResult is not null)
        {
            return skippedResult;
        }

        string mp3FilePath = await ConvertAudioToMp3Async(recordingEntry.FilePath, settings!, cancellationToken)
            .ConfigureAwait(false);
        DiscordMediaShareResult? sizeResult = ValidateFileSize(mp3FilePath, settings!);
        if (sizeResult is not null)
        {
            return sizeResult;
        }

        string messageText = $"VALOWATCH audio MP3 {Path.GetFileName(mp3FilePath)}";
        await SendFileAsync(settings!, mp3FilePath, messageText, cancellationToken).ConfigureAwait(false);
        return new DiscordMediaShareResult(true, true, "Audio MP3 shared to Discord.", mp3FilePath);
    }

    public async Task<DiscordMediaShareResult> ShareVideoCaptureAsync(
        VideoCaptureResult videoCaptureResult,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(videoCaptureResult.FilePath))
        {
            return new DiscordMediaShareResult(true, false, $"Video file missing: {videoCaptureResult.FilePath}");
        }

        DiscordBotSettings? settings = settingsStore.Load(out string statusText);
        DiscordMediaShareResult? skippedResult = ValidateShareSettings(settings, statusText, requireAudio: false);
        if (skippedResult is not null)
        {
            return skippedResult;
        }

        if (!settings!.ShareVideoMp4)
        {
            return new DiscordMediaShareResult(false, false, "Discord MP4 sharing disabled.");
        }

        DiscordMediaShareResult? sizeResult = ValidateFileSize(videoCaptureResult.FilePath, settings);
        if (sizeResult is not null)
        {
            return sizeResult;
        }

        string messageText = $"VALOWATCH {videoCaptureResult.Kind} MP4 {Path.GetFileName(videoCaptureResult.FilePath)}";
        await SendFileAsync(settings, videoCaptureResult.FilePath, messageText, cancellationToken).ConfigureAwait(false);
        return new DiscordMediaShareResult(true, true, "Video MP4 shared to Discord.", videoCaptureResult.FilePath);
    }

    private static DiscordMediaShareResult? ValidateShareSettings(
        DiscordBotSettings? settings,
        string statusText,
        bool requireAudio)
    {
        if (settings is null)
        {
            return new DiscordMediaShareResult(false, false, $"Discord media sharing skipped: {statusText}");
        }

        if (!settings.ShareMediaFiles)
        {
            return new DiscordMediaShareResult(false, false, "Discord media sharing disabled.");
        }

        if (settings.TextChannelId == 0)
        {
            return new DiscordMediaShareResult(false, false, "Discord media sharing skipped: text channel id missing.");
        }

        if (requireAudio && !settings.ShareAudioAsMp3)
        {
            return new DiscordMediaShareResult(false, false, "Discord MP3 sharing disabled.");
        }

        return null;
    }

    private static DiscordMediaShareResult? ValidateFileSize(string filePath, DiscordBotSettings settings)
    {
        long fileLength = new FileInfo(filePath).Length;
        long maxBytes = Math.Clamp(settings.MediaShareMaxBytes, 1L * 1024L * 1024L, 24L * 1024L * 1024L);
        if (fileLength <= maxBytes)
        {
            return null;
        }

        string fileName = Path.GetFileName(filePath);
        return new DiscordMediaShareResult(
            true,
            false,
            $"Discord media sharing skipped because {fileName} is {FormatBytes(fileLength)} and exceeds {FormatBytes(maxBytes)}.",
            filePath);
    }

    private async Task<string> ConvertAudioToMp3Async(
        string sourceWaveFilePath,
        DiscordBotSettings settings,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(appPaths.SharedMediaDirectory);
        string sourceFileNameWithoutExtension = Path.GetFileNameWithoutExtension(sourceWaveFilePath);
        string outputFilePath = Path.Combine(appPaths.SharedMediaDirectory, $"{sourceFileNameWithoutExtension}.mp3");

        if (File.Exists(outputFilePath) &&
            new FileInfo(outputFilePath).Length > 0 &&
            File.GetLastWriteTimeUtc(outputFilePath) >= File.GetLastWriteTimeUtc(sourceWaveFilePath))
        {
            return outputFilePath;
        }

        string ffmpegPath = FfmpegToolLocator.Resolve(appPaths, settings.MediaShareFfmpegPath);
        if (File.Exists(outputFilePath))
        {
            File.Delete(outputFilePath);
        }

        ProcessStartInfo processStartInfo = new()
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        processStartInfo.ArgumentList.Add("-hide_banner");
        processStartInfo.ArgumentList.Add("-loglevel");
        processStartInfo.ArgumentList.Add("warning");
        processStartInfo.ArgumentList.Add("-y");
        processStartInfo.ArgumentList.Add("-i");
        processStartInfo.ArgumentList.Add(sourceWaveFilePath);
        processStartInfo.ArgumentList.Add("-vn");
        processStartInfo.ArgumentList.Add("-codec:a");
        processStartInfo.ArgumentList.Add("libmp3lame");
        processStartInfo.ArgumentList.Add("-b:a");
        processStartInfo.ArgumentList.Add($"{settings.MediaShareAudioBitrateKbps}k");
        processStartInfo.ArgumentList.Add(outputFilePath);

        using Process process = Process.Start(processStartInfo)
            ?? throw new InvalidOperationException("ffmpeg.exe could not be started for MP3 conversion.");

        Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();

        using CancellationTokenSource timeoutCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellationSource.CancelAfter(MediaConversionTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutCancellationSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            throw;
        }

        string standardOutput = await standardOutputTask.ConfigureAwait(false);
        string standardError = await standardErrorTask.ConfigureAwait(false);
        if (process.ExitCode != 0 || !File.Exists(outputFilePath) || new FileInfo(outputFilePath).Length == 0)
        {
            throw new InvalidOperationException(
                "MP3 conversion failed. " +
                $"ExitCode: {process.ExitCode}. " +
                $"Output: {TrimProcessText(standardOutput)} " +
                $"Error: {TrimProcessText(standardError)}");
        }

        return outputFilePath;
    }

    private static async Task SendFileAsync(
        DiscordBotSettings settings,
        string filePath,
        string messageText,
        CancellationToken cancellationToken)
    {
        using DiscordRestClient restClient = new(new DiscordRestConfig
        {
            LogLevel = LogSeverity.Warning
        });

        using CancellationTokenSource uploadCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        uploadCancellationSource.CancelAfter(DiscordUploadTimeout);

        await restClient.LoginAsync(TokenType.Bot, settings.BotToken).ConfigureAwait(false);
        try
        {
            IDiscordClient discordClient = restClient;
            IGuild guild = await discordClient
                .GetGuildAsync(settings.GuildId, CacheMode.AllowDownload, options: null)
                .WaitAsync(uploadCancellationSource.Token)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("Discord guild was not found for media sharing.");

            ITextChannel textChannel = await guild
                .GetTextChannelAsync(settings.TextChannelId, CacheMode.AllowDownload, options: null)
                .WaitAsync(uploadCancellationSource.Token)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("Discord text channel was not found for media sharing.");

            await textChannel.SendFileAsync(
                    filePath,
                    text: messageText,
                    isTTS: false,
                    embed: null,
                    options: null,
                    isSpoiler: false,
                    allowedMentions: AllowedMentions.None,
                    messageReference: null,
                    components: null,
                    stickers: null,
                    embeds: null,
                    flags: MessageFlags.SuppressNotification,
                    poll: null)
                .WaitAsync(uploadCancellationSource.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await restClient.LogoutAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException)
            {
            }
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static string TrimProcessText(string processText)
    {
        string trimmedText = processText.Trim();
        const int maxLength = 1200;
        return trimmedText.Length <= maxLength ? trimmedText : trimmedText[^maxLength..];
    }

    private static string FormatBytes(long byteCount)
    {
        double megabytes = byteCount / 1024D / 1024D;
        return $"{megabytes:0.0} MiB";
    }
}
