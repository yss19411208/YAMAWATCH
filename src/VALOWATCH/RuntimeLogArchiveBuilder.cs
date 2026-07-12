using System.IO.Compression;
using System.Text;

namespace VALOWATCH;

internal static class RuntimeLogArchiveBuilder
{
    public static void Create(
        string archivePath,
        string versionLabel,
        params (string SourceDirectory, string ArchiveDirectory)[] logSources)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionLabel);

        string fullArchivePath = Path.GetFullPath(archivePath);
        string temporaryArchivePath = fullArchivePath + ".writing";
        Directory.CreateDirectory(Path.GetDirectoryName(fullArchivePath) ?? AppContext.BaseDirectory);
        if (File.Exists(temporaryArchivePath))
        {
            File.Delete(temporaryArchivePath);
        }

        using (FileStream archiveStream = new(
            temporaryArchivePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None))
        using (ZipArchive archive = new(archiveStream, ZipArchiveMode.Create, leaveOpen: false))
        {
            ZipArchiveEntry metadataEntry = archive.CreateEntry("runtime-metadata.txt", CompressionLevel.Optimal);
            using (StreamWriter metadataWriter = new(
                metadataEntry.Open(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                metadataWriter.WriteLine("VALOWATCH runtime diagnostic");
                metadataWriter.WriteLine($"TimestampUtc={DateTimeOffset.UtcNow:O}");
                metadataWriter.WriteLine($"Version={versionLabel}");
            }

            HashSet<string> includedPaths = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string sourceDirectory, string archiveDirectory) in logSources)
            {
                AddLogDirectoryToArchive(
                    archive,
                    sourceDirectory,
                    archiveDirectory,
                    fullArchivePath,
                    includedPaths);
            }
        }

        File.Move(temporaryArchivePath, fullArchivePath, overwrite: true);
    }

    private static void AddLogDirectoryToArchive(
        ZipArchive archive,
        string sourceDirectory,
        string archiveDirectory,
        string outputArchivePath,
        HashSet<string> includedPaths)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return;
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
                fullSourcePath.Equals(outputArchivePath, StringComparison.OrdinalIgnoreCase) ||
                fullSourcePath.Equals(outputArchivePath + ".writing", StringComparison.OrdinalIgnoreCase) ||
                !includedPaths.Add(fullSourcePath))
            {
                continue;
            }

            string relativePath = Path.GetRelativePath(fullSourceDirectory, fullSourcePath)
                .Replace(Path.DirectorySeparatorChar, '/');
            string entryName = $"{archiveDirectory.Trim('/')}/{relativePath}";
            ZipArchiveEntry logEntry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using StreamWriter entryWriter = new(
                logEntry.Open(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            try
            {
                using FileStream logStream = new(
                    fullSourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using StreamReader logReader = new(logStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                while (logReader.ReadLine() is { } line)
                {
                    entryWriter.WriteLine(SanitizeLine(line));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                entryWriter.WriteLine($"[log could not be read: {exception.GetType().Name}]");
            }
        }
    }

    internal static string SanitizeLine(string line)
    {
        if (line.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("AUTHORIZATION", StringComparison.OrdinalIgnoreCase) ||
            line.Contains(".env", StringComparison.OrdinalIgnoreCase))
        {
            return "[redacted secret-related log line]";
        }

        string sanitizedLine = line;
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
}
