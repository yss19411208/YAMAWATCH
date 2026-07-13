using System.Text;
using System.Text.Json;

namespace VALOWATCH;

internal sealed record RuntimeLogFileDelta(
    string CursorKey,
    int PreviousLineCount,
    int CurrentLineCount,
    IReadOnlyList<string> DiscordMessages);

internal static class RuntimeLogMessageCollector
{
    private const int DiscordMessageLimit = 2000;
    private const int MaximumPayloadLength = 1850;
    private const int MaximumInitialSyncLines = 120;

    public static IReadOnlyList<RuntimeLogFileDelta> Collect(
        string cursorPath,
        string versionLabel,
        params (string SourceDirectory, string SourceLabel)[] logSources)
    {
        Dictionary<string, int> cursors = LoadCursors(cursorPath);
        List<RuntimeLogFileDelta> deltas = [];
        HashSet<string> includedPaths = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string sourceDirectory, string sourceLabel) in logSources)
        {
            if (!Directory.Exists(sourceDirectory))
            {
                continue;
            }

            string fullSourceDirectory = Path.GetFullPath(sourceDirectory);
            foreach (string sourcePath in Directory.EnumerateFiles(
                fullSourceDirectory,
                "*",
                SearchOption.AllDirectories))
            {
                string extension = Path.GetExtension(sourcePath);
                string fullSourcePath = Path.GetFullPath(sourcePath);
                if ((!extension.Equals(".log", StringComparison.OrdinalIgnoreCase) &&
                        !extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)) ||
                    !includedPaths.Add(fullSourcePath))
                {
                    continue;
                }

                string relativePath = Path.GetRelativePath(fullSourceDirectory, fullSourcePath)
                    .Replace(Path.DirectorySeparatorChar, '/');
                string cursorKey = $"{sourceLabel.Trim('/')}/{relativePath}";
                int previousLineCount = cursors.GetValueOrDefault(cursorKey);
                string[] lines = ReadSanitizedLines(fullSourcePath);
                if (lines.Length < previousLineCount)
                {
                    previousLineCount = 0;
                }

                IReadOnlyList<string> messages = BuildDiscordMessages(
                    versionLabel,
                    cursorKey,
                    lines,
                    previousLineCount);
                if (messages.Count > 0 || previousLineCount != lines.Length)
                {
                    deltas.Add(new RuntimeLogFileDelta(
                        cursorKey,
                        previousLineCount,
                        lines.Length,
                        messages));
                }
            }
        }

        return deltas;
    }

    public static void Commit(string cursorPath, string cursorKey, int lineCount)
    {
        Dictionary<string, int> cursors = LoadCursors(cursorPath);
        cursors[cursorKey] = Math.Max(0, lineCount);

        string fullCursorPath = Path.GetFullPath(cursorPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullCursorPath) ?? AppContext.BaseDirectory);
        string temporaryPath = fullCursorPath + $".{Environment.ProcessId}.writing";
        byte[] cursorJson = JsonSerializer.SerializeToUtf8Bytes(
            cursors,
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllBytes(temporaryPath, cursorJson);
        File.Move(temporaryPath, fullCursorPath, overwrite: true);
    }

    internal static string SanitizeLine(string line)
    {
        if (line.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("AUTHORIZATION", StringComparison.OrdinalIgnoreCase) ||
            line.Contains(".env", StringComparison.OrdinalIgnoreCase))
        {
            return "[redacted secret-related log line]";
        }

        string sanitizedLine = line.Replace("```", "'''", StringComparison.Ordinal);
        string[] profileDirectories =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetEnvironmentVariable("USERPROFILE") ?? string.Empty
        ];
        foreach (string profileDirectory in profileDirectories
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            sanitizedLine = sanitizedLine.Replace(
                profileDirectory,
                "%USERPROFILE%",
                StringComparison.OrdinalIgnoreCase);
        }

        return sanitizedLine;
    }

    private static string[] ReadSanitizedLines(string sourcePath)
    {
        List<string> lines = [];
        try
        {
            using FileStream logStream = new(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using StreamReader logReader = new(logStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (logReader.ReadLine() is { } line)
            {
                lines.Add(SanitizeLine(line));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            lines.Add($"[log could not be read: {exception.GetType().Name}]");
        }

        return lines.ToArray();
    }

    private static IReadOnlyList<string> BuildDiscordMessages(
        string versionLabel,
        string cursorKey,
        IReadOnlyList<string> lines,
        int startLineIndex)
    {
        if (startLineIndex >= lines.Count)
        {
            return [];
        }

        List<string> messages = [];
        StringBuilder payload = new();
        if (startLineIndex == 0 && lines.Count > MaximumInitialSyncLines)
        {
            startLineIndex = lines.Count - MaximumInitialSyncLines;
        }

        int chunkStartLine = startLineIndex + 1;

        for (int lineIndex = startLineIndex; lineIndex < lines.Count; lineIndex++)
        {
            if (!ShouldMirrorLineToDiscord(lines[lineIndex]))
            {
                continue;
            }

            string numberedLine = $"{lineIndex + 1}: {lines[lineIndex]}{Environment.NewLine}";
            int consumedCharacters = 0;
            while (consumedCharacters < numberedLine.Length)
            {
                int remainingCapacity = MaximumPayloadLength - payload.Length;
                if (remainingCapacity <= 0)
                {
                    messages.Add(CreateDiscordMessage(
                        versionLabel,
                        cursorKey,
                        chunkStartLine,
                        lineIndex + 1,
                        payload.ToString()));
                    payload.Clear();
                    chunkStartLine = lineIndex + 1;
                    remainingCapacity = MaximumPayloadLength;
                }

                int copyLength = Math.Min(remainingCapacity, numberedLine.Length - consumedCharacters);
                payload.Append(numberedLine, consumedCharacters, copyLength);
                consumedCharacters += copyLength;
            }
        }

        if (payload.Length > 0)
        {
            messages.Add(CreateDiscordMessage(
                versionLabel,
                cursorKey,
                chunkStartLine,
                lines.Count,
                payload.ToString()));
        }

        return messages;
    }

    private static bool ShouldMirrorLineToDiscord(string line)
    {
        string trimmedLine = line.TrimStart();
        if (trimmedLine.Length == 0)
        {
            return false;
        }

        string[] highVolumeMarkers =
        [
            "[Discord] Requested Discord notification sent.",
            "[Discord] Requested Discord notification failed.",
            "[Discord] Requested Discord notification could not be sent",
            "[Discord] Discord diagnostic notification queued.",
            "[Discord] Runtime log code block",
            "[Discord] Runtime log code blocks",
            "[Discord] Audio stats.",
            "[Overlay] Dedicated key-state monitor health.",
            "Discord.Net Warning: Gateway:",
            "Discord.Net Warning: Audio #",
            "Discord.Net Warning: Dave decrypt",
            "Discord gateway disconnected; Discord.Net will reconnect automatically.",
            "transient network reconnect warning",
            "Failed to decrypt audio packet",
            "DecryptionFailure",
            "WebSocketException",
            "WebSocket connection was closed",
            "Unable to read data from the transport connection",
            "SocketException (10054)",
            "SocketException (11001)",
            "HttpRequestException",
            "そのようなホストは不明です",
            "GITHUB agent release lookup attempt",
            "latest release lookup attempt",
            "GITHUB agent download attempt",
            "app download attempt",
            "--- End of stack trace from previous location ---",
            "--- End of inner exception stack trace ---"
        ];

        if (highVolumeMarkers.Any(marker =>
                line.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (trimmedLine.StartsWith("at Discord.", StringComparison.Ordinal) ||
            trimmedLine.StartsWith("at System.Net.", StringComparison.Ordinal) ||
            trimmedLine.StartsWith("at System.Threading.Tasks.", StringComparison.Ordinal))
        {
            return false;
        }

        string[] notificationContinuationMarkers =
        [
            "Device:",
            "ActiveDevices:",
            "CapturedPeak:",
            "WrittenPeak:",
            "SilenceFrames:",
            "ShortFrames:"
        ];
        if (notificationContinuationMarkers.Any(marker =>
                trimmedLine.StartsWith(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        string[] routineMarkers =
        [
            "VALORANT trigger received.",
            "Discord settings loaded.",
            "Runtime diagnostic.",
            "Discord gateway connected.",
            "Discord gateway is ready.",
            "Discord voice permissions.",
            "Connecting to Discord voice channel",
            "Joined Discord voice channel",
            "Pending update notification was sent and cleared.",
            "Ordered physical microphone candidates:",
            "Microphone capture selected.",
            "LINE process-only loopback provider started.",
            "LINE process-only loopback started.",
            "Microphone audio relay started.",
            "Microphone relay start buffer ready.",
            "Microphone input became audible.",
            "Discord audio relay started sending audible PCM.",
            "GITHUB agent is already current.",
            "Dedicated update skipped because the installed app is already current.",
            "GITHUB background update check completed. ExitCode: 0.",
            "App PE and SHA-256 validation passed:",
            "App download progress:",
            "Dedicated update completed.",
            "GITHUB watch agent started.",
            "GITHUB watch agent launch requested:",
            "GITHUB agent replacement completed:",
            "GITHUB is exiting so the validated replacement can be installed."
        ];
        if (routineMarkers.Any(marker =>
                line.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        string[] importantMarkers =
        [
            "failed",
            "failure",
            "could not",
            "missing",
            "denied",
            "unauthorized",
            "exception",
            "timeout",
            "timed out",
            "mismatch",
            "invalid",
            "not a Windows PE",
            "returned exit code",
            "crash",
            "faulted",
            "recovery pending",
            "remained unavailable"
        ];

        return importantMarkers.Any(marker =>
            line.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static string CreateDiscordMessage(
        string versionLabel,
        string cursorKey,
        int startLine,
        int endLine,
        string payload)
    {
        string header = $"VALOWATCH {versionLabel} | {cursorKey} | lines {startLine}-{endLine}";
        string message = $"```text{Environment.NewLine}{header}{Environment.NewLine}{payload}```";
        if (message.Length > DiscordMessageLimit)
        {
            throw new InvalidOperationException(
                $"Runtime log message exceeded Discord's {DiscordMessageLimit}-character limit.");
        }

        return message;
    }

    private static Dictionary<string, int> LoadCursors(string cursorPath)
    {
        if (!File.Exists(cursorPath))
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            Dictionary<string, int>? cursors = JsonSerializer.Deserialize<Dictionary<string, int>>(
                File.ReadAllBytes(cursorPath));
            return cursors is null
                ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, int>(cursors, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
