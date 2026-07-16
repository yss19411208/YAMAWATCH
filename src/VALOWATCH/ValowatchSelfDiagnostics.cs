using Discord;
using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace VALOWATCH;

internal static class ValowatchSelfDiagnostics
{
    private const int DirectoryScanEntryLimit = 20000;
    private const int DiscordDescriptionLimit = 4096;
    private const int DiscordDescriptionSafetyMargin = 350;
    private static readonly TimeSpan DefaultDiagnosticTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan NetworkDiagnosticTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan DownloadDiagnosticTimeout = TimeSpan.FromMinutes(3);

    public static async Task<IReadOnlyList<Embed>> BuildDiscordEmbedsAsync(
        AppPaths appPaths,
        bool includeUpdateDownload,
        CancellationToken cancellationToken)
    {
        DateTimeOffset startedAt = DateTimeOffset.Now;
        string workspaceRoot = ResolveWorkspaceRoot(appPaths);
        string? appExecutablePath = ResolveAppExecutablePath(workspaceRoot);
        string? githubAgentPath = ResolveFirstExistingFile(
            Path.Combine(workspaceRoot, "GITHUB.exe"),
            Path.Combine(workspaceRoot, "github", "GITHUB.exe"),
            Path.Combine(workspaceRoot, "app", "GITHUB.exe"));
        string? startAgentPath = ResolveFirstExistingFile(
            Path.Combine(workspaceRoot, "VALOWATCH_Start.exe"),
            Path.Combine(workspaceRoot, "start", "VALOWATCH_Start.exe"),
            Path.Combine(workspaceRoot, "app", "VALOWATCH_Start.exe"));

        List<DiagnosticCheckResult> checkResults = [];
        foreach (DiagnosticCheckSpec diagnosticCheck in BuildDiagnosticChecks(
            appExecutablePath,
            githubAgentPath,
            startAgentPath,
            includeUpdateDownload))
        {
            cancellationToken.ThrowIfCancellationRequested();
            DiagnosticCheckResult result = await RunDiagnosticCheckAsync(
                    diagnosticCheck,
                    cancellationToken)
                .ConfigureAwait(false);
            checkResults.Add(result);
        }

        FolderStatus folderStatus = CaptureFolderStatus(
            appPaths,
            workspaceRoot,
            appExecutablePath,
            githubAgentPath,
            startAgentPath);

        DateTimeOffset finishedAt = DateTimeOffset.Now;
        return BuildEmbeds(
            checkResults,
            folderStatus,
            includeUpdateDownload,
            startedAt,
            finishedAt);
    }

    private static IReadOnlyList<DiagnosticCheckSpec> BuildDiagnosticChecks(
        string? appExecutablePath,
        string? githubAgentPath,
        string? startAgentPath,
        bool includeUpdateDownload)
    {
        List<DiagnosticCheckSpec> checks =
        [
            new("Alt+T入力", appExecutablePath, AppContext.BaseDirectory, ["--check-alt-t-input"]),
            new("更新スケジュール", appExecutablePath, AppContext.BaseDirectory, ["--check-update-schedule"]),
            new("設定保存", appExecutablePath, AppContext.BaseDirectory, ["--check-durable-config"]),
            new("GitHub更新確認", appExecutablePath, AppContext.BaseDirectory, ["--check-git-update"], NetworkDiagnosticTimeout),
            new("Discord音声DLL", appExecutablePath, AppContext.BaseDirectory, ["--check-discord-voice-native"]),
            new("Discord再接続方針", appExecutablePath, AppContext.BaseDirectory, ["--check-discord-retry-policy"]),
            new("自己更新ロールバック", appExecutablePath, AppContext.BaseDirectory, ["--check-self-update-rollback"]),
            new("埋め込みエージェント", appExecutablePath, AppContext.BaseDirectory, ["--check-embedded-agent-resources"]),
            new("エージェント修復", appExecutablePath, AppContext.BaseDirectory, ["--check-embedded-agent-repair"]),
            new("既存エージェント保持", appExecutablePath, AppContext.BaseDirectory, ["--check-embedded-agent-existing-skip"]),
            new("マイク一覧", appExecutablePath, AppContext.BaseDirectory, ["--list-microphones"]),
            new("マイク入力", appExecutablePath, AppContext.BaseDirectory, ["--check-microphone"]),
            new("LINE音声取得", appExecutablePath, AppContext.BaseDirectory, ["--check-line-loopback"]),
            new("Discord音声ミックス", appExecutablePath, AppContext.BaseDirectory, ["--check-discord-audio-mix"]),
            new("文字起こし", appExecutablePath, AppContext.BaseDirectory, ["--check-transcription-local"]),
            new("タスクバーアプリ", appExecutablePath, AppContext.BaseDirectory, ["--check-running-app-snapshot"]),
            new("実行中プログラム", appExecutablePath, AppContext.BaseDirectory, ["--check-running-process-snapshot"]),
            new("LINE起動トリガー", appExecutablePath, AppContext.BaseDirectory, ["--check-line-voice-trigger"]),
            new("Discord VC表示", appExecutablePath, AppContext.BaseDirectory, ["--check-discord-voice-context"]),
            new("Discord VC検知対象", appExecutablePath, AppContext.BaseDirectory, ["--check-discord-voice-state-filter"]),
            new("監視エージェント起動計画", appExecutablePath, AppContext.BaseDirectory, ["--check-watch-agent-supervisor"]),
            new("ランタイムログ送信", appExecutablePath, AppContext.BaseDirectory, ["--check-runtime-log-messages"]),
            new("GITHUB更新パス回復", githubAgentPath, Path.GetDirectoryName(githubAgentPath) ?? AppContext.BaseDirectory, ["--check-download-path-recovery"]),
            new("GITHUB互換ブートストラップ", githubAgentPath, Path.GetDirectoryName(githubAgentPath) ?? AppContext.BaseDirectory, ["--check-compat-agent-bootstrap"]),
            new("StartAgent", startAgentPath, Path.GetDirectoryName(startAgentPath) ?? AppContext.BaseDirectory, ["--check-start-agent"])
        ];

        if (includeUpdateDownload)
        {
            checks.Add(new(
                "更新ファイル実ダウンロード",
                appExecutablePath,
                AppContext.BaseDirectory,
                ["--check-update-download"],
                DownloadDiagnosticTimeout));
        }

        return checks;
    }

    private static async Task<DiagnosticCheckResult> RunDiagnosticCheckAsync(
        DiagnosticCheckSpec checkSpec,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(checkSpec.ExecutablePath) ||
            !File.Exists(checkSpec.ExecutablePath))
        {
            return new DiagnosticCheckResult(
                checkSpec.Label,
                DiagnosticCheckStatus.Skipped,
                null,
                TimeSpan.Zero,
                "実行ファイルが見つかりません");
        }

        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        try
        {
            using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(checkSpec.Timeout);

            ProcessStartInfo processStartInfo = new()
            {
                FileName = checkSpec.ExecutablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Directory.Exists(checkSpec.WorkingDirectory)
                    ? checkSpec.WorkingDirectory
                    : AppContext.BaseDirectory
            };
            foreach (string argument in checkSpec.Arguments)
            {
                processStartInfo.ArgumentList.Add(argument);
            }

            using Process process = new() { StartInfo = processStartInfo };
            if (!process.Start())
            {
                return CreateFinishedResult(
                    checkSpec,
                    DiagnosticCheckStatus.Failed,
                    null,
                    startedAt,
                    "プロセスを開始できませんでした");
            }

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKillProcess(process);
                return CreateFinishedResult(
                    checkSpec,
                    DiagnosticCheckStatus.TimedOut,
                    null,
                    startedAt,
                    $"timeout {checkSpec.Timeout.TotalSeconds:0}s");
            }

            string outputSummary = SummarizeProcessOutput(
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false));
            return CreateFinishedResult(
                checkSpec,
                process.ExitCode == 0 ? DiagnosticCheckStatus.Passed : DiagnosticCheckStatus.Failed,
                process.ExitCode,
                startedAt,
                outputSummary);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return CreateFinishedResult(
                checkSpec,
                DiagnosticCheckStatus.Failed,
                null,
                startedAt,
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static DiagnosticCheckResult CreateFinishedResult(
        DiagnosticCheckSpec checkSpec,
        DiagnosticCheckStatus status,
        int? exitCode,
        DateTimeOffset startedAtUtc,
        string message)
    {
        return new DiagnosticCheckResult(
            checkSpec.Label,
            status,
            exitCode,
            DateTimeOffset.UtcNow - startedAtUtc,
            message);
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

    private static string SummarizeProcessOutput(string stdout, string stderr)
    {
        string combinedOutput = string.Join(
                " ",
                new[] { stdout, stderr }
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Replace("\r\n", " ", StringComparison.Ordinal)
                        .Replace('\n', ' ')
                        .Replace('\r', ' ')
                        .Trim()))
            .Trim();
        if (string.IsNullOrWhiteSpace(combinedOutput))
        {
            return string.Empty;
        }

        return combinedOutput.Length <= 220 ? combinedOutput : combinedOutput[..220] + "...";
    }

    private static FolderStatus CaptureFolderStatus(
        AppPaths appPaths,
        string workspaceRoot,
        string? appExecutablePath,
        string? githubAgentPath,
        string? startAgentPath)
    {
        string localAppDataValowatch = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VALOWATCH");
        string tempValowatch = Path.Combine(Path.GetTempPath(), "VALOWATCH");
        List<FolderSummary> keyFolders = [];
        foreach ((string label, string path) in new (string Label, string Path)[]
        {
            ("root", workspaceRoot),
            ("app", Path.GetDirectoryName(appExecutablePath ?? string.Empty) ?? Path.Combine(workspaceRoot, "app")),
            ("data", appPaths.DataDirectory),
            ("logs", Path.Combine(appPaths.DataDirectory, "logs")),
            ("updates", Path.Combine(appPaths.DataDirectory, "updates")),
            ("config", appPaths.ConfigDirectory),
            ("localappdata", localAppDataValowatch),
            ("temp", tempValowatch)
        })
        {
            if (!keyFolders.Any(folder => folder.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                keyFolders.Add(SummarizeFolder(label, path));
            }
        }

        List<FolderSummary> rootChildren = [];
        if (Directory.Exists(workspaceRoot))
        {
            foreach (string childDirectory in SafeEnumerateDirectories(workspaceRoot)
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .Take(16))
            {
                rootChildren.Add(SummarizeFolder(Path.GetFileName(childDirectory), childDirectory));
            }

            foreach (string childFile in SafeEnumerateFiles(workspaceRoot)
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(0, 16 - rootChildren.Count)))
            {
                rootChildren.Add(SummarizeFile(Path.GetFileName(childFile), childFile));
            }
        }

        IReadOnlyList<FileStatus> importantFiles =
        [
            CreateFileStatus("VALOWATCH.exe", appExecutablePath),
            CreateFileStatus("GITHUB.exe", githubAgentPath),
            CreateFileStatus("VALOWATCH_Start.exe", startAgentPath),
            CreateFileStatus("installer/.env", Path.Combine(workspaceRoot, "installer", ".env"), showSize: false),
            CreateFileStatus("settings.protected", appPaths.DurableEnvPath, showSize: false),
            CreateFileStatus("valowatch.log", Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log")),
            CreateFileStatus("self-update.log", Path.Combine(tempValowatch, "self-update.log")),
            CreateFileStatus("dedicated-updater.log", Path.Combine(tempValowatch, "dedicated-updater.log"))
        ];

        return new FolderStatus(
            MaskUserPath(workspaceRoot),
            MaskUserPath(appPaths.DataDirectory),
            importantFiles,
            keyFolders,
            rootChildren);
    }

    private static FolderSummary SummarizeFolder(string label, string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return new FolderSummary(label, MaskUserPath(directoryPath), false, 0, 0, 0, null, false, "missing");
        }

        long totalBytes = 0;
        int fileCount = 0;
        int directoryCount = 0;
        bool truncated = false;
        DateTimeOffset? newestWrite = null;
        Queue<string> pendingDirectories = new();
        pendingDirectories.Enqueue(directoryPath);

        while (pendingDirectories.Count > 0)
        {
            string currentDirectory = pendingDirectories.Dequeue();
            if (fileCount + directoryCount >= DirectoryScanEntryLimit)
            {
                truncated = true;
                break;
            }

            foreach (string childDirectory in SafeEnumerateDirectories(currentDirectory))
            {
                if (fileCount + directoryCount >= DirectoryScanEntryLimit)
                {
                    truncated = true;
                    break;
                }

                directoryCount++;
                if (!IsReparsePoint(childDirectory))
                {
                    pendingDirectories.Enqueue(childDirectory);
                }

                newestWrite = NewerOf(newestWrite, TryGetLastWriteTime(childDirectory));
            }

            foreach (string childFile in SafeEnumerateFiles(currentDirectory))
            {
                if (fileCount + directoryCount >= DirectoryScanEntryLimit)
                {
                    truncated = true;
                    break;
                }

                fileCount++;
                try
                {
                    FileInfo fileInfo = new(childFile);
                    totalBytes += Math.Max(0, fileInfo.Length);
                    newestWrite = NewerOf(newestWrite, fileInfo.LastWriteTime);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    truncated = true;
                }
            }
        }

        string note = truncated ? $"先頭{DirectoryScanEntryLimit}件まで集計" : string.Empty;
        return new FolderSummary(
            label,
            MaskUserPath(directoryPath),
            true,
            totalBytes,
            fileCount,
            directoryCount,
            newestWrite,
            truncated,
            note);
    }

    private static FolderSummary SummarizeFile(string label, string filePath)
    {
        try
        {
            FileInfo fileInfo = new(filePath);
            return new FolderSummary(
                label,
                MaskUserPath(filePath),
                fileInfo.Exists,
                fileInfo.Exists ? fileInfo.Length : 0,
                fileInfo.Exists ? 1 : 0,
                0,
                fileInfo.Exists ? fileInfo.LastWriteTime : null,
                false,
                fileInfo.Exists ? "file" : "missing");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new FolderSummary(label, MaskUserPath(filePath), false, 0, 0, 0, null, true, exception.GetType().Name);
        }
    }

    private static FileStatus CreateFileStatus(string label, string? filePath, bool showSize = true)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return new FileStatus(label, false, string.Empty, null, "missing");
        }

        try
        {
            FileInfo fileInfo = new(filePath);
            if (!fileInfo.Exists)
            {
                return new FileStatus(label, false, MaskUserPath(filePath), null, "missing");
            }

            string detail = showSize
                ? $"{FormatByteSize(fileInfo.Length)}, {FormatDate(fileInfo.LastWriteTime)}"
                : $"exists, {FormatDate(fileInfo.LastWriteTime)}";
            return new FileStatus(label, true, MaskUserPath(filePath), fileInfo.Length, detail);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new FileStatus(label, false, MaskUserPath(filePath), null, exception.GetType().Name);
        }
    }

    private static IReadOnlyList<Embed> BuildEmbeds(
        IReadOnlyList<DiagnosticCheckResult> checkResults,
        FolderStatus folderStatus,
        bool includeUpdateDownload,
        DateTimeOffset startedAt,
        DateTimeOffset finishedAt)
    {
        List<Embed> embeds = [];
        int passedCount = checkResults.Count(result => result.Status == DiagnosticCheckStatus.Passed);
        int failedCount = checkResults.Count(result => result.Status == DiagnosticCheckStatus.Failed);
        int timeoutCount = checkResults.Count(result => result.Status == DiagnosticCheckStatus.TimedOut);
        int skippedCount = checkResults.Count(result => result.Status == DiagnosticCheckStatus.Skipped);

        string versionLabel = GetCurrentVersionLabel();
        List<string> diagnosticLines = checkResults
            .Select(FormatDiagnosticLine)
            .ToList();
        foreach ((string description, int pageIndex, int pageCount) in ChunkLines(diagnosticLines))
        {
            EmbedBuilder builder = new()
            {
                Title = pageCount == 1 ? "VALOWATCH 自己診断" : $"VALOWATCH 自己診断 {pageIndex}/{pageCount}",
                Description = description,
                Color = failedCount == 0 && timeoutCount == 0
                    ? new Discord.Color(63, 185, 80)
                    : new Discord.Color(248, 81, 73),
                Timestamp = finishedAt
            };
            builder.AddField("結果", $"OK {passedCount} / NG {failedCount} / TIMEOUT {timeoutCount} / SKIP {skippedCount}", inline: false);
            builder.AddField("所要時間", $"{(finishedAt - startedAt).TotalSeconds:0.0}s", inline: true);
            builder.AddField("Version", versionLabel, inline: true);
            builder.AddField("更新DL診断", includeUpdateDownload ? "実行" : "未実行(download:trueで実行)", inline: true);
            embeds.Add(builder.Build());
        }

        embeds.Add(BuildFolderStatusEmbed(folderStatus));
        embeds.Add(BuildRootChildrenEmbed(folderStatus));
        return embeds;
    }

    private static Embed BuildFolderStatusEmbed(FolderStatus folderStatus)
    {
        EmbedBuilder builder = new()
        {
            Title = "VALOWATCH フォルダー状況",
            Color = new Discord.Color(88, 166, 255),
            Timestamp = DateTimeOffset.Now
        };
        builder.Description = TrimDescription(string.Join(
            Environment.NewLine,
            [
                $"Root: {folderStatus.WorkspaceRoot}",
                $"Data: {folderStatus.DataDirectory}",
                "",
                "重要ファイル",
                .. folderStatus.ImportantFiles.Select(FormatFileStatusLine),
                "",
                "主要フォルダー",
                .. folderStatus.KeyFolders.Select(FormatFolderSummaryLine)
            ]));
        builder.WithFooter("秘密情報の中身は表示しません。ユーザーパスは%USERPROFILE%にマスク済み");
        return builder.Build();
    }

    private static Embed BuildRootChildrenEmbed(FolderStatus folderStatus)
    {
        string description = folderStatus.RootChildren.Count == 0
            ? "Root直下の項目は見つかりませんでした。"
            : string.Join(Environment.NewLine, folderStatus.RootChildren.Select(FormatFolderSummaryLine));
        EmbedBuilder builder = new()
        {
            Title = "VALOWATCH Root直下",
            Description = TrimDescription(description),
            Color = new Discord.Color(88, 166, 255),
            Timestamp = DateTimeOffset.Now
        };
        builder.WithFooter($"容量集計は最大{DirectoryScanEntryLimit}件まで。フルパスの詳細列挙はしていません");
        return builder.Build();
    }

    private static string FormatDiagnosticLine(DiagnosticCheckResult result)
    {
        string statusText = result.Status switch
        {
            DiagnosticCheckStatus.Passed => "OK",
            DiagnosticCheckStatus.Failed => "NG",
            DiagnosticCheckStatus.TimedOut => "TIMEOUT",
            DiagnosticCheckStatus.Skipped => "SKIP",
            _ => "UNKNOWN"
        };
        string exitText = result.ExitCode is int exitCode ? $" exit={exitCode}" : string.Empty;
        string messageText = string.IsNullOrWhiteSpace(result.Message)
            ? string.Empty
            : $" - {result.Message}";
        return $"[{statusText}] {result.Label} ({result.Duration.TotalSeconds:0.0}s{exitText}){messageText}";
    }

    private static string FormatFileStatusLine(FileStatus fileStatus)
    {
        string statusText = fileStatus.Exists ? "OK" : "missing";
        return $"• {fileStatus.Label}: {statusText} ({fileStatus.Detail})";
    }

    private static string FormatFolderSummaryLine(FolderSummary summary)
    {
        if (!summary.Exists)
        {
            return $"• {summary.Label}: missing";
        }

        string truncatedText = summary.Truncated ? ", partial" : string.Empty;
        string newestText = summary.NewestWrite is DateTimeOffset newestWrite
            ? $", latest {FormatDate(newestWrite)}"
            : string.Empty;
        return $"• {summary.Label}: {FormatByteSize(summary.TotalBytes)}, files {summary.FileCount}, dirs {summary.DirectoryCount}{newestText}{truncatedText}";
    }

    private static IReadOnlyList<(string Description, int PageIndex, int PageCount)> ChunkLines(IReadOnlyList<string> lines)
    {
        int maximumLength = DiscordDescriptionLimit - DiscordDescriptionSafetyMargin;
        List<string> chunks = [];
        StringBuilder currentChunk = new();
        foreach (string line in lines)
        {
            string pendingLine = currentChunk.Length == 0 ? line : Environment.NewLine + line;
            if (currentChunk.Length > 0 && currentChunk.Length + pendingLine.Length > maximumLength)
            {
                chunks.Add(currentChunk.ToString());
                currentChunk.Clear();
                pendingLine = line;
            }

            currentChunk.Append(pendingLine);
        }

        if (currentChunk.Length > 0)
        {
            chunks.Add(currentChunk.ToString());
        }

        if (chunks.Count == 0)
        {
            chunks.Add("自己診断項目がありません。");
        }

        return chunks
            .Select((description, index) => (description, index + 1, chunks.Count))
            .ToArray();
    }

    private static string TrimDescription(string description)
    {
        int maximumLength = DiscordDescriptionLimit - DiscordDescriptionSafetyMargin;
        return description.Length <= maximumLength
            ? description
            : description[..maximumLength] + $"{Environment.NewLine}...省略";
    }

    private static string ResolveWorkspaceRoot(AppPaths appPaths)
    {
        string dataDirectory = Path.GetFullPath(appPaths.DataDirectory);
        DirectoryInfo? dataDirectoryInfo = new(dataDirectory);
        if (string.Equals(dataDirectoryInfo.Name, "data", StringComparison.OrdinalIgnoreCase) &&
            dataDirectoryInfo.Parent is not null)
        {
            return dataDirectoryInfo.Parent.FullName;
        }

        DirectoryInfo? currentDirectory = new(AppContext.BaseDirectory);
        while (currentDirectory is not null)
        {
            if (File.Exists(Path.Combine(currentDirectory.FullName, "VALOWATCH.slnx")) ||
                Directory.Exists(Path.Combine(currentDirectory.FullName, "installer")) ||
                Directory.Exists(Path.Combine(currentDirectory.FullName, "app")))
            {
                return currentDirectory.FullName;
            }

            currentDirectory = currentDirectory.Parent;
        }

        return dataDirectoryInfo.Parent?.FullName ?? AppContext.BaseDirectory;
    }

    private static string? ResolveAppExecutablePath(string workspaceRoot)
    {
        string? currentProcessPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(currentProcessPath) &&
            File.Exists(currentProcessPath) &&
            string.Equals(Path.GetFileName(currentProcessPath), "VALOWATCH.exe", StringComparison.OrdinalIgnoreCase))
        {
            return currentProcessPath;
        }

        return ResolveFirstExistingFile(
            Path.Combine(AppContext.BaseDirectory, "VALOWATCH.exe"),
            Path.Combine(workspaceRoot, "app", "VALOWATCH.exe"),
            Path.Combine(workspaceRoot, "data", "installed", "VALOWATCH", "app", "VALOWATCH.exe"),
            Path.Combine(workspaceRoot, "exe", "VALOWATCH.exe"));
    }

    private static string? ResolveFirstExistingFile(params string[] paths)
    {
        return paths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string directoryPath)
    {
        try
        {
            return Directory.EnumerateDirectories(directoryPath).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string directoryPath)
    {
        try
        {
            return Directory.EnumerateFiles(directoryPath).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }
    }

    private static bool IsReparsePoint(string directoryPath)
    {
        try
        {
            return (File.GetAttributes(directoryPath) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return true;
        }
    }

    private static DateTimeOffset? TryGetLastWriteTime(string path)
    {
        try
        {
            return File.GetLastWriteTime(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static DateTimeOffset? NewerOf(DateTimeOffset? current, DateTimeOffset? candidate)
    {
        if (candidate is null)
        {
            return current;
        }

        return current is null || candidate > current ? candidate : current;
    }

    private static string FormatByteSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = Math.Max(0, bytes);
        int unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{bytes} {units[unitIndex]}" : $"{size:0.##} {units[unitIndex]}";
    }

    private static string FormatDate(DateTimeOffset dateTime)
    {
        return dateTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    private static string MaskUserPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "(unknown)";
        }

        string maskedPath = Path.GetFullPath(path);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            maskedPath = maskedPath.Replace(userProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        string environmentUserProfile = Environment.GetEnvironmentVariable("USERPROFILE") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(environmentUserProfile))
        {
            maskedPath = maskedPath.Replace(environmentUserProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        return maskedPath;
    }

    private static string GetCurrentVersionLabel()
    {
        Assembly applicationAssembly = typeof(ValowatchSelfDiagnostics).Assembly;
        return applicationAssembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion?
            .Trim() ??
            applicationAssembly.GetName().Version?.ToString() ??
            "unknown";
    }

    private sealed record DiagnosticCheckSpec(
        string Label,
        string? ExecutablePath,
        string WorkingDirectory,
        IReadOnlyList<string> Arguments,
        TimeSpan? TimeoutOverride = null)
    {
        public TimeSpan Timeout { get; } = TimeoutOverride ?? DefaultDiagnosticTimeout;
    }

    private sealed record DiagnosticCheckResult(
        string Label,
        DiagnosticCheckStatus Status,
        int? ExitCode,
        TimeSpan Duration,
        string Message);

    private enum DiagnosticCheckStatus
    {
        Passed,
        Failed,
        TimedOut,
        Skipped
    }

    private sealed record FolderStatus(
        string WorkspaceRoot,
        string DataDirectory,
        IReadOnlyList<FileStatus> ImportantFiles,
        IReadOnlyList<FolderSummary> KeyFolders,
        IReadOnlyList<FolderSummary> RootChildren);

    private sealed record FileStatus(
        string Label,
        bool Exists,
        string Path,
        long? SizeBytes,
        string Detail);

    private sealed record FolderSummary(
        string Label,
        string Path,
        bool Exists,
        long TotalBytes,
        int FileCount,
        int DirectoryCount,
        DateTimeOffset? NewestWrite,
        bool Truncated,
        string Note);
}
