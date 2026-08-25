using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VALOWATCH;

/// <summary>
/// Discord から任意の PowerShell を実行する機能。危険度が高いため、
/// 次の多重の安全策で守る。
///
///   1. 管理者（Administrator / ManageGuild）だけがパスワード設定・実行できる
///   2. パスワードは平文で保存せず、PBKDF2 でハッシュ化して保存する
///   3. パスワード未設定なら実行は一切できない（既定は無効）
///   4. 総当たり対策として、連続失敗が続くと一定時間ロックする
///   5. 実行・失敗・ロックはすべてログに記録する
///
/// 呼び出し側（DiscordBotVoiceRelay）で、コマンド応答をエフェメラル（本人のみ）に
/// することで、出力が他人に見えないようにする。
/// </summary>
internal sealed class PowerShellCommandController
{
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ProgressUpdateInterval = TimeSpan.FromSeconds(2);
    private const int Pbkdf2Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int MaxOutputCharacters = 1800;
    // 出力を分割送信するときの最大メッセージ数。極端に長い出力での連投暴走を防ぐ。
    private const int MaxOutputChunks = 10;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object gate = new();
    private readonly string statePath;
    private readonly Action<string, Exception?> writeLog;

    private int failedAttempts;
    private DateTimeOffset? lockedUntilUtc;

    // いま実行中の PowerShell プロセス。/valowatch-ps stop で外から止められるよう保持する。
    private readonly object runLock = new();
    private Process? runningProcess;

    public PowerShellCommandController(AppPaths appPaths, Action<string, Exception?> writeLog)
    {
        statePath = Path.Combine(appPaths.ConfigDirectory, "powershell-command.json");
        this.writeLog = writeLog;
    }

    public bool IsPasswordConfigured()
    {
        PowerShellCommandState state = LoadState();
        return state.HasPassword;
    }

    /// <summary>
    /// パスワードを設定（または変更）する。既に設定済みの場合は、正しい
    /// 現在のパスワードを渡さないと変更できない。
    /// </summary>
    public PowerShellPasswordResult SetPassword(string? currentPassword, string newPassword)
    {
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 4)
        {
            return new PowerShellPasswordResult(false, "新しいパスワードは4文字以上にしてください。");
        }

        lock (gate)
        {
            PowerShellCommandState state = LoadState();
            if (state.HasPassword)
            {
                if (string.IsNullOrEmpty(currentPassword) || !VerifyAgainst(state, currentPassword))
                {
                    writeLog("PowerShell command password change rejected: current password mismatch.", null);
                    return new PowerShellPasswordResult(false, "現在のパスワードが違います。パスワードは変更されていません。");
                }
            }

            byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
            byte[] hash = Derive(newPassword, salt);
            var updated = new PowerShellCommandState(
                Convert.ToBase64String(salt),
                Convert.ToBase64String(hash),
                Pbkdf2Iterations);
            SaveState(updated);

            failedAttempts = 0;
            lockedUntilUtc = null;
            writeLog("PowerShell command password was set or changed by an administrator.", null);
            return new PowerShellPasswordResult(true, "パスワードを設定しました。");
        }
    }

    /// <summary>
    /// パスワードを照合し、一致すれば PowerShell を実行する。
    /// </summary>
    public async Task<PowerShellExecutionResult> ExecuteAsync(
        string password,
        string script,
        Func<string, Task>? onProgress = null)
    {
        lock (gate)
        {
            PowerShellCommandState state = LoadState();
            if (!state.HasPassword)
            {
                return PowerShellExecutionResult.Rejected(
                    "パスワードが未設定です。先に管理者が /valowatch-ps set-password で設定してください。");
            }

            if (lockedUntilUtc is { } lockedUntil && DateTimeOffset.UtcNow < lockedUntil)
            {
                TimeSpan remaining = lockedUntil - DateTimeOffset.UtcNow;
                writeLog("PowerShell command execution blocked: locked out.", null);
                return PowerShellExecutionResult.Rejected(
                    $"連続で失敗したためロック中です。あと約{Math.Ceiling(remaining.TotalMinutes)}分お待ちください。");
            }

            if (!VerifyAgainst(state, password))
            {
                failedAttempts++;
                writeLog($"PowerShell command password mismatch. Failed attempts: {failedAttempts}.", null);
                if (failedAttempts >= MaxFailedAttempts)
                {
                    lockedUntilUtc = DateTimeOffset.UtcNow + LockoutDuration;
                    failedAttempts = 0;
                    writeLog("PowerShell command locked out due to repeated failures.", null);
                    return PowerShellExecutionResult.Rejected(
                        $"パスワードを{MaxFailedAttempts}回間違えたため、{LockoutDuration.TotalMinutes:0}分間ロックしました。");
                }

                return PowerShellExecutionResult.Rejected(
                    $"パスワードが違います。（あと{MaxFailedAttempts - failedAttempts}回でロック）");
            }

            failedAttempts = 0;
        }

        if (string.IsNullOrWhiteSpace(script))
        {
            return PowerShellExecutionResult.Rejected("実行するコマンドが空です。");
        }

        writeLog($"PowerShell command execution started. Length: {script.Length} chars.", null);
        return await RunPowerShellAsync(script, onProgress).ConfigureAwait(false);
    }

    private async Task<PowerShellExecutionResult> RunPowerShellAsync(
        string script,
        Func<string, Task>? onProgress)
    {
        // 進捗バー（CLIXML）が stderr に漏れて文字化けするのを防ぐため、
        // スクリプトの先頭で進捗表示を抑制する。
        // あわせて、日本語Windowsでは既定の出力が CP932 になり UTF-8 で読むと
        // 文字化けするため、出力エンコーディングを UTF-8 に統一する。
        string wrappedScript =
            "$ProgressPreference = 'SilentlyContinue'\r\n" +
            "$OutputEncoding = [System.Text.Encoding]::UTF8\r\n" +
            "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\r\n" +
            "[Console]::InputEncoding = [System.Text.Encoding]::UTF8\r\n" +
            script;

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        // -EncodedCommand を使うと、引用符やパイプを含むスクリプトでも安全に渡せる。
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(wrappedScript)));

        using var process = new Process { StartInfo = startInfo };
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();
        var progressLock = new object();
        bool progressDirty = false;

        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                lock (progressLock)
                {
                    outputBuilder.AppendLine(args.Data);
                    progressDirty = true;
                }
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                lock (progressLock)
                {
                    errorBuilder.AppendLine(args.Data);
                    progressDirty = true;
                }
            }
        };

        try
        {
            if (!process.Start())
            {
                return PowerShellExecutionResult.Rejected("PowerShell プロセスを開始できませんでした。");
            }

            lock (runLock)
            {
                runningProcess = process;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // 出力が溜まったら、一定間隔で途中経過を通知する（リアルタイム更新）。
            Task? progressLoop = null;
            if (onProgress is not null)
            {
                progressLoop = Task.Run(async () =>
                {
                    while (!process.HasExited)
                    {
                        await Task.Delay(ProgressUpdateInterval).ConfigureAwait(false);
                        string? snapshot = null;
                        lock (progressLock)
                        {
                            if (progressDirty)
                            {
                                snapshot = BuildCombined(outputBuilder, errorBuilder);
                                progressDirty = false;
                            }
                        }

                        if (snapshot is not null)
                        {
                            try
                            {
                                await onProgress(snapshot).ConfigureAwait(false);
                            }
                            catch (Exception progressException)
                            {
                                writeLog("PowerShell progress update failed.", progressException);
                            }
                        }
                    }
                });
            }

            // 自動タイムアウトなし。/valowatch-ps stop で止めるまで動き続ける。
            await process.WaitForExitAsync().ConfigureAwait(false);

            if (progressLoop is not null)
            {
                await progressLoop.ConfigureAwait(false);
            }

            bool wasStopped;
            lock (runLock)
            {
                wasStopped = runningProcess is null;
                runningProcess = null;
            }

            if (wasStopped)
            {
                writeLog("PowerShell command was stopped by user.", null);
                return PowerShellExecutionResult.Completed(
                    -1,
                    outputBuilder.ToString(),
                    errorBuilder.ToString() + "\n(ユーザーの /valowatch-ps stop により停止しました)");
            }

            writeLog($"PowerShell command finished. ExitCode: {process.ExitCode}.", null);
            return PowerShellExecutionResult.Completed(
                process.ExitCode,
                outputBuilder.ToString(),
                errorBuilder.ToString());
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            lock (runLock)
            {
                runningProcess = null;
            }

            writeLog("PowerShell command execution failed.", exception);
            return PowerShellExecutionResult.Rejected($"実行に失敗しました: {exception.Message}");
        }
    }

    /// <summary>
    /// 実行中の PowerShell を止める。パスワード照合を通った管理者だけが呼べる。
    /// </summary>
    public PowerShellStopResult Stop(string password)
    {
        lock (gate)
        {
            PowerShellCommandState state = LoadState();
            if (!state.HasPassword)
            {
                return new PowerShellStopResult(false, "パスワードが未設定です。");
            }

            if (lockedUntilUtc is { } lockedUntil && DateTimeOffset.UtcNow < lockedUntil)
            {
                return new PowerShellStopResult(false, "ロック中です。しばらくお待ちください。");
            }

            if (!VerifyAgainst(state, password))
            {
                failedAttempts++;
                if (failedAttempts >= MaxFailedAttempts)
                {
                    lockedUntilUtc = DateTimeOffset.UtcNow + LockoutDuration;
                    failedAttempts = 0;
                    return new PowerShellStopResult(false, $"パスワードを{MaxFailedAttempts}回間違えたためロックしました。");
                }

                return new PowerShellStopResult(false, "パスワードが違います。");
            }

            failedAttempts = 0;
        }

        Process? target;
        lock (runLock)
        {
            target = runningProcess;
            // ここで null にしておくと、実行側は「stop で止められた」と判定できる。
            runningProcess = null;
        }

        if (target is null)
        {
            return new PowerShellStopResult(false, "いま実行中の PowerShell はありません。");
        }

        TryKill(target);
        writeLog("PowerShell command stop requested by user.", null);
        return new PowerShellStopResult(true, "実行中の PowerShell を停止しました。");
    }

    private static string BuildCombined(StringBuilder outputBuilder, StringBuilder errorBuilder)
    {
        string combined = outputBuilder.ToString();
        string errors = errorBuilder.ToString();
        if (!string.IsNullOrWhiteSpace(errors))
        {
            combined += "\n[stderr]\n" + errors;
        }

        return combined;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // 既に終了している等は無視する
        }
    }

    public static string FormatProgressForDiscord(string combinedOutput)
    {
        string combined = combinedOutput.Trim();
        if (combined.Length == 0)
        {
            combined = "(まだ出力はありません)";
        }

        bool truncated = combined.Length > MaxOutputCharacters;
        if (truncated)
        {
            // 途中経過は「新しい方」を優先して末尾を見せる。
            combined = "…(前略)\n" + combined[^MaxOutputCharacters..];
        }

        return $"⏳ 実行中…\n```\n{combined}\n```";
    }

    public static string FormatForDiscord(PowerShellExecutionResult result)
    {
        if (!result.Executed)
        {
            return result.Message;
        }

        string combined = result.StandardOutput;
        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            combined += "\n[stderr]\n" + result.StandardError;
        }

        combined = combined.Trim();
        if (combined.Length == 0)
        {
            combined = "(出力なし)";
        }

        bool truncated = combined.Length > MaxOutputCharacters;
        if (truncated)
        {
            combined = combined[..MaxOutputCharacters] + "\n…(以下省略)";
        }

        return $"終了コード: {result.ExitCode}\n```\n{combined}\n```";
    }

    /// <summary>
    /// 実行結果を、Discordのメッセージ上限に収まる複数のチャンクに分割して返す。
    /// 1メッセージ目には終了コードを付け、各チャンクはコードブロックで囲む。
    /// 出力が極端に長い場合に備え、最大チャンク数で打ち切る。
    /// </summary>
    public static IReadOnlyList<string> FormatForDiscordChunks(PowerShellExecutionResult result)
    {
        if (!result.Executed)
        {
            return new[] { result.Message };
        }

        string combined = result.StandardOutput;
        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            combined += "\n[stderr]\n" + result.StandardError;
        }

        combined = combined.Replace("\r\n", "\n").TrimEnd();
        if (combined.Length == 0)
        {
            return new[] { $"終了コード: {result.ExitCode}\n```\n(出力なし)\n```" };
        }

        // コードブロックの装飾（```\n … \n```）ぶんを引いた実データ用の幅。
        const int chunkBodyLimit = 1850;
        var pieces = new List<string>();
        int index = 0;
        while (index < combined.Length && pieces.Count < MaxOutputChunks)
        {
            int take = Math.Min(chunkBodyLimit, combined.Length - index);

            // できるだけ改行で区切る（行の途中で切らない）。
            if (index + take < combined.Length)
            {
                int lastNewline = combined.LastIndexOf('\n', index + take - 1, take);
                if (lastNewline > index)
                {
                    take = lastNewline - index + 1;
                }
            }

            pieces.Add(combined.Substring(index, take).TrimEnd('\n'));
            index += take;
        }

        bool truncatedByLimit = index < combined.Length;
        var messages = new List<string>();
        for (int i = 0; i < pieces.Count; i++)
        {
            string header = i == 0
                ? $"終了コード: {result.ExitCode}（全{pieces.Count}通）\n"
                : $"（{i + 1}/{pieces.Count}）\n";
            messages.Add($"{header}```\n{pieces[i]}\n```");
        }

        if (truncatedByLimit && messages.Count > 0)
        {
            messages[^1] += $"\n…(出力が長すぎるため{MaxOutputChunks}通で打ち切りました)";
        }

        return messages;
    }

    private bool VerifyAgainst(PowerShellCommandState state, string password)
    {
        if (!state.HasPassword)
        {
            return false;
        }

        try
        {
            byte[] salt = Convert.FromBase64String(state.SaltBase64!);
            byte[] expected = Convert.FromBase64String(state.HashBase64!);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password),
                salt,
                state.Iterations <= 0 ? Pbkdf2Iterations : state.Iterations,
                HashAlgorithmName.SHA256,
                expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] Derive(string password, byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            HashBytes);
    }

    private PowerShellCommandState LoadState()
    {
        try
        {
            if (!File.Exists(statePath))
            {
                return PowerShellCommandState.Empty;
            }

            string json = File.ReadAllText(statePath);
            return JsonSerializer.Deserialize<PowerShellCommandState>(json, JsonOptions)
                ?? PowerShellCommandState.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return PowerShellCommandState.Empty;
        }
    }

    private void SaveState(PowerShellCommandState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(statePath) ?? AppContext.BaseDirectory);
        string tempPath = $"{statePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        string json = JsonSerializer.Serialize(state, JsonOptions);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, statePath, overwrite: true);
    }
}

internal sealed record PowerShellCommandState(
    string? SaltBase64,
    string? HashBase64,
    int Iterations)
{
    public static PowerShellCommandState Empty { get; } = new(null, null, 0);

    public bool HasPassword =>
        !string.IsNullOrEmpty(SaltBase64) && !string.IsNullOrEmpty(HashBase64);
}

internal sealed record PowerShellPasswordResult(bool Success, string Message);

internal sealed record PowerShellStopResult(bool Stopped, string Message);

internal sealed record PowerShellExecutionResult(
    bool Executed,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    string Message)
{
    public static PowerShellExecutionResult Rejected(string message)
    {
        return new PowerShellExecutionResult(false, 0, string.Empty, string.Empty, message);
    }

    public static PowerShellExecutionResult Completed(int exitCode, string standardOutput, string standardError)
    {
        return new PowerShellExecutionResult(true, exitCode, standardOutput, standardError, string.Empty);
    }
}
