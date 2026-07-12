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
        int chunkStartLine = startLineIndex + 1;

        for (int lineIndex = startLineIndex; lineIndex < lines.Count; lineIndex++)
        {
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
