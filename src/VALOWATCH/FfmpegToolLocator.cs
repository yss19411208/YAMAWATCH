namespace VALOWATCH;

internal static class FfmpegToolLocator
{
    public static string Resolve(AppPaths appPaths, string? configuredFfmpegPath)
    {
        List<string> candidatePaths = [];
        if (!string.IsNullOrWhiteSpace(configuredFfmpegPath))
        {
            candidatePaths.Add(Environment.ExpandEnvironmentVariables(configuredFfmpegPath.Trim()));
        }

        candidatePaths.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"));
        candidatePaths.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe"));
        candidatePaths.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", "ffmpeg.exe"));
        candidatePaths.Add(Path.Combine(appPaths.DataDirectory, "tools", "ffmpeg.exe"));
        candidatePaths.Add(Path.Combine(appPaths.DataDirectory, "tools", "ffmpeg", "ffmpeg.exe"));
        candidatePaths.Add(Path.Combine(appPaths.DataDirectory, "tools", "ffmpeg", "bin", "ffmpeg.exe"));

        foreach (string candidatePath in candidatePaths)
        {
            if (File.Exists(candidatePath))
            {
                return Path.GetFullPath(candidatePath);
            }
        }

        string? pathCandidate = FindExecutableOnPath("ffmpeg.exe");
        if (!string.IsNullOrWhiteSpace(pathCandidate))
        {
            return pathCandidate;
        }

        throw new FileNotFoundException("ffmpeg.exe was not found. Set VALOWATCH_FFMPEG_PATH or install a bundled update.");
    }

    private static string? FindExecutableOnPath(string executableName)
    {
        string? pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (string directoryPath in pathValue.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                continue;
            }

            string candidatePath;
            try
            {
                candidatePath = Path.Combine(directoryPath.Trim(), executableName);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                continue;
            }

            if (File.Exists(candidatePath))
            {
                return candidatePath;
            }
        }

        return null;
    }
}
