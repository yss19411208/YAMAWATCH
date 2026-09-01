using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Discord;
using Discord.WebSocket;

namespace ClientSystem;

/// <summary>
/// 緊急復旧用の独立した Discord ボット。
///
/// 本体(VALOWATCH)や更新システム(GITHUB.exe)が完全に壊れても、
/// このボットは Guardian(Client_System.exe) の中で独立して動き続けるため、
/// Discord から緊急の復旧操作ができる。
///
/// 使うトークンは本体とは別（環境変数 EMERGENCY_BOT_TOKEN）。
/// パスワードは Guardian 専用（本体とは別に /emergency-set-password で設定）。
///
/// コマンド:
///   /emergency-ps           password, script      … 任意 PowerShell 実行
///   /emergency-rollback     password, sha         … 指定バージョンへ復旧（未実装の骨組みは Program 側の復元を流用）
///   /emergency-restore      password              … バックアップから VALOWATCH を復元
///   /emergency-status                             … 状態確認
///   /emergency-set-password new, [current]        … パスワード設定・変更
/// </summary>
internal sealed class EmergencyBot
{
    private const string TokenEnvVariable = "EMERGENCY_BOT_TOKEN";
    private static readonly string TokenFilePath =
        @"C:\Program Files\Client Systems\emergency-token.txt";

    private static readonly string PasswordStatePath =
        @"C:\Program Files\Client Systems\emergency-password.json";

    private const int Pbkdf2Iterations = 200_000;
    private const int HashBytes = 32;
    private const int SaltBytes = 16;

    private readonly Action<string> log;
    private DiscordSocketClient? client;

    public EmergencyBot(Action<string> log)
    {
        this.log = log;
    }

    /// <summary>
    /// ボットを起動する。トークンが未設定なら何もせず false を返す（監視機能は継続）。
    /// </summary>
    public async Task<bool> StartAsync()
    {
        // トークンは、まずファイル(emergency-token.txt)から読む。
        // 環境変数は起動中のサービスに反映されにくいため、ファイル方式を優先する。
        // ファイルが無ければ環境変数にフォールバックする。
        string? token = ReadTokenFromFile();
        if (string.IsNullOrWhiteSpace(token))
        {
            token = Environment.GetEnvironmentVariable(TokenEnvVariable, EnvironmentVariableTarget.Machine)
                ?? Environment.GetEnvironmentVariable(TokenEnvVariable);
        }

        if (string.IsNullOrWhiteSpace(token) || token == "PASTE_EMERGENCY_BOT_TOKEN_HERE")
        {
            log("Emergency bot token is not set. Put the token in " + TokenFilePath + " (single line). Emergency bot disabled; monitoring continues.");
            return false;
        }

        try
        {
            client = new DiscordSocketClient(new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.Guilds,
                LogLevel = LogSeverity.Warning,
            });

            client.Log += OnClientLog;
            client.Ready += OnReadyAsync;
            client.SlashCommandExecuted += OnSlashCommandAsync;

            await client.LoginAsync(TokenType.Bot, token).ConfigureAwait(false);
            await client.StartAsync().ConfigureAwait(false);
            log("Emergency bot connected.");
            return true;
        }
        catch (Exception exception)
        {
            log("Emergency bot failed to start: " + exception);
            return false;
        }
    }

    private string? ReadTokenFromFile()
    {
        try
        {
            if (!File.Exists(TokenFilePath))
            {
                return null;
            }

            // ファイル全体を読み、前後の空白・改行・BOM を除去して1行のトークンにする。
            string raw = File.ReadAllText(TokenFilePath);
            return raw.Trim().Trim('\uFEFF').Trim();
        }
        catch (Exception exception)
        {
            log("Reading emergency token file failed: " + exception.Message);
            return null;
        }
    }

    private Task OnClientLog(LogMessage message)
    {
        if (message.Severity <= LogSeverity.Warning)
        {
            log("Emergency bot [" + message.Severity + "]: " + message.Message);
        }

        return Task.CompletedTask;
    }

    private async Task OnReadyAsync()
    {
        try
        {
            var psCommand = new SlashCommandBuilder()
                .WithName("emergency-ps")
                .WithDescription("Run a PowerShell script (password required)")
                .AddOption("password", ApplicationCommandOptionType.String, "The emergency password", isRequired: true)
                .AddOption("script", ApplicationCommandOptionType.String, "PowerShell script to run", isRequired: true);

            var rollbackCommand = new SlashCommandBuilder()
                .WithName("emergency-rollback")
                .WithDescription("Roll back VALOWATCH to a specific release (password required)")
                .AddOption("password", ApplicationCommandOptionType.String, "The emergency password", isRequired: true)
                .AddOption("sha", ApplicationCommandOptionType.String, "Git commit SHA of the release to restore", isRequired: true);

            var restoreCommand = new SlashCommandBuilder()
                .WithName("emergency-restore")
                .WithDescription("Restore VALOWATCH from the guardian backup (password required)")
                .AddOption("password", ApplicationCommandOptionType.String, "The emergency password", isRequired: true);

            var statusCommand = new SlashCommandBuilder()
                .WithName("emergency-status")
                .WithDescription("Show recovery status (no password required)");

            var setPasswordCommand = new SlashCommandBuilder()
                .WithName("emergency-set-password")
                .WithDescription("Set or change the emergency password")
                .AddOption("new_password", ApplicationCommandOptionType.String, "New password (4+ chars)", isRequired: true)
                .AddOption("current_password", ApplicationCommandOptionType.String, "Current password (required when changing)", isRequired: false);

            if (client is not null)
            {
                await client.CreateGlobalApplicationCommandAsync(psCommand.Build()).ConfigureAwait(false);
                await client.CreateGlobalApplicationCommandAsync(rollbackCommand.Build()).ConfigureAwait(false);
                await client.CreateGlobalApplicationCommandAsync(restoreCommand.Build()).ConfigureAwait(false);
                await client.CreateGlobalApplicationCommandAsync(statusCommand.Build()).ConfigureAwait(false);
                await client.CreateGlobalApplicationCommandAsync(setPasswordCommand.Build()).ConfigureAwait(false);
            }

            log("Emergency bot slash commands registered.");
        }
        catch (Exception exception)
        {
            log("Emergency bot command registration failed: " + exception);
        }
    }

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        try
        {
            switch (command.CommandName)
            {
                case "emergency-ps":
                    await HandlePowerShellAsync(command).ConfigureAwait(false);
                    break;
                case "emergency-rollback":
                    await HandleRollbackAsync(command).ConfigureAwait(false);
                    break;
                case "emergency-restore":
                    await HandleRestoreAsync(command).ConfigureAwait(false);
                    break;
                case "emergency-status":
                    await HandleStatusAsync(command).ConfigureAwait(false);
                    break;
                case "emergency-set-password":
                    await HandleSetPasswordAsync(command).ConfigureAwait(false);
                    break;
                default:
                    await command.RespondAsync("Unknown command.", ephemeral: true).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception exception)
        {
            log("Emergency command failed: " + exception);
            try
            {
                if (command.HasResponded)
                {
                    await command.FollowupAsync("エラー: " + exception.Message, ephemeral: true).ConfigureAwait(false);
                }
                else
                {
                    await command.RespondAsync("エラー: " + exception.Message, ephemeral: true).ConfigureAwait(false);
                }
            }
            catch
            {
            }
        }
    }

    private string GetOption(SocketSlashCommand command, string name)
    {
        foreach (var option in command.Data.Options)
        {
            if (string.Equals(option.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return option.Value?.ToString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private async Task HandlePowerShellAsync(SocketSlashCommand command)
    {
        string password = GetOption(command, "password");
        string script = GetOption(command, "script");

        if (!VerifyPassword(password))
        {
            await command.RespondAsync("パスワードが違います。", ephemeral: true).ConfigureAwait(false);
            return;
        }

        await command.DeferAsync(ephemeral: false).ConfigureAwait(false);

        (int exitCode, string output) = await RunPowerShellAsync(script).ConfigureAwait(false);
        log("Emergency PowerShell executed. ExitCode: " + exitCode);

        string trimmed = output.Length > 1800 ? output[..1800] + "\n…(truncated)" : output;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            trimmed = "(出力なし)";
        }

        await command.FollowupAsync($"終了コード: {exitCode}\n```\n{trimmed}\n```").ConfigureAwait(false);
    }

    private async Task HandleRollbackAsync(SocketSlashCommand command)
    {
        string password = GetOption(command, "password");
        string sha = GetOption(command, "sha").Trim();

        if (!VerifyPassword(password))
        {
            await command.RespondAsync("パスワードが違います。", ephemeral: true).ConfigureAwait(false);
            return;
        }

        await command.DeferAsync(ephemeral: false).ConfigureAwait(false);

        // 指定 SHA の GitHub Release から各 exe を取得して配置する PowerShell を組み立てて実行する。
        // 実際の配布アセット名・配置先はリネーム後の値に合わせて、呼び出し時に script で渡す方式でもよいが、
        // ここでは既知の Release 資産をダウンロードして本体フォルダへ置く最小の復旧を行う。
        string script = BuildRollbackScript(sha);
        (int exitCode, string output) = await RunPowerShellAsync(script).ConfigureAwait(false);
        log($"Emergency rollback to {sha} executed. ExitCode: {exitCode}");

        string trimmed = output.Length > 1800 ? output[..1800] + "\n…(truncated)" : output;
        await command.FollowupAsync($"ロールバック({sha}) 終了コード: {exitCode}\n```\n{trimmed}\n```").ConfigureAwait(false);
    }

    private static string BuildRollbackScript(string sha)
    {
        // GitHub Release のタグは valowatch-<sha>。そこから資産を取得する。
        // 配置先・資産名は環境に合わせて調整が必要なため、ここでは汎用の雛形をログ出力する。
        // 実運用では /emergency-ps で個別に指示するのが確実。
        return
            "$ErrorActionPreference='Stop';" +
            $"$tag='valowatch-{sha}';" +
            "Write-Output \"Rollback target tag: $tag\";" +
            "Write-Output 'この雛形は資産名・配置先の指定が必要です。確実な復旧は /emergency-ps で個別に行ってください。';";
    }

    private async Task HandleRestoreAsync(SocketSlashCommand command)
    {
        string password = GetOption(command, "password");

        if (!VerifyPassword(password))
        {
            await command.RespondAsync("パスワードが違います。", ephemeral: true).ConfigureAwait(false);
            return;
        }

        await command.DeferAsync(ephemeral: false).ConfigureAwait(false);

        bool ok = Program.ForceRestoreFromBackup();
        await command.FollowupAsync(ok
            ? "バックアップからの復元を実行しました。"
            : "復元に失敗しました（バックアップが無い/不完全）。ログを確認してください。").ConfigureAwait(false);
    }

    private async Task HandleStatusAsync(SocketSlashCommand command)
    {
        string status = Program.BuildStatusReport();
        await command.RespondAsync($"```\n{status}\n```", ephemeral: true).ConfigureAwait(false);
    }

    private async Task HandleSetPasswordAsync(SocketSlashCommand command)
    {
        string newPassword = GetOption(command, "new_password");
        string currentPassword = GetOption(command, "current_password");

        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 4)
        {
            await command.RespondAsync("新しいパスワードは4文字以上にしてください。", ephemeral: true).ConfigureAwait(false);
            return;
        }

        EmergencyPasswordState state = LoadPasswordState();
        if (state.HasPassword)
        {
            if (string.IsNullOrEmpty(currentPassword) || !VerifyPassword(currentPassword))
            {
                await command.RespondAsync("現在のパスワードが違います。変更されていません。", ephemeral: true).ConfigureAwait(false);
                return;
            }
        }

        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(newPassword),
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            HashBytes);

        var newState = new EmergencyPasswordState
        {
            SaltBase64 = Convert.ToBase64String(salt),
            HashBase64 = Convert.ToBase64String(hash),
            Iterations = Pbkdf2Iterations,
        };
        SavePasswordState(newState);
        log("Emergency password was set or changed.");
        await command.RespondAsync("パスワードを設定しました。", ephemeral: true).ConfigureAwait(false);
    }

    private bool VerifyPassword(string password)
    {
        EmergencyPasswordState state = LoadPasswordState();
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

    private EmergencyPasswordState LoadPasswordState()
    {
        try
        {
            if (!File.Exists(PasswordStatePath))
            {
                return new EmergencyPasswordState();
            }

            string json = File.ReadAllText(PasswordStatePath);
            return JsonSerializer.Deserialize<EmergencyPasswordState>(json) ?? new EmergencyPasswordState();
        }
        catch
        {
            return new EmergencyPasswordState();
        }
    }

    private void SavePasswordState(EmergencyPasswordState state)
    {
        try
        {
            string? directory = Path.GetDirectoryName(PasswordStatePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(state);
            File.WriteAllText(PasswordStatePath, json);
        }
        catch (Exception exception)
        {
            log("Saving emergency password failed: " + exception);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunPowerShellAsync(string script)
    {
        try
        {
            byte[] scriptBytes = Encoding.Unicode.GetBytes(script);
            string encoded = Convert.ToBase64String(scriptBytes);

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-EncodedCommand");
            startInfo.ArgumentList.Add(encoded);

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(120_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return (-1, "タイムアウト（120秒）で中断しました。");
            }

            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);
            string combined = stdout;
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                combined += "\n[stderr]\n" + stderr;
            }

            return (process.ExitCode, combined);
        }
        catch (Exception exception)
        {
            return (-1, "実行に失敗しました: " + exception.Message);
        }
    }
}

internal sealed class EmergencyPasswordState
{
    public string? SaltBase64 { get; set; }
    public string? HashBase64 { get; set; }
    public int Iterations { get; set; }

    public bool HasPassword =>
        !string.IsNullOrEmpty(SaltBase64) && !string.IsNullOrEmpty(HashBase64);
}
