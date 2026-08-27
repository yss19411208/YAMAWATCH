using System.Diagnostics;
using System.Text;

namespace VALOWATCH;

/// <summary>
/// VALORANT 起動中に、設定された PowerShell コマンドを
/// 「ランダム時間だけ実行 → 停止（kill）→ ランダム時間休憩」という
/// サイクルで繰り返し動かすランナー。
///
/// 段階1（このクラス単体）では、既存コードには一切接続しない。
/// Start/Stop で外部からサイクルを制御でき、各イベントは onEvent で通知する。
/// VALORANT 検知や Discord コマンドとの配線は後の段階で行う。
/// </summary>
public sealed class ValorantCycleRunner
{
    // 実行1回あたりに収集する出力の上限（これを超えたら以降を捨てる）。
    private const int MaxCollectedOutputChars = 6000;

    private readonly AppPaths appPaths;
    private readonly Action<string, Exception?> writeLog;
    private readonly string statePath;
    private readonly object gate = new();

    // サイクルのイベントを外へ通知するためのコールバック。
    // phase は "開始" / "終了" / "休憩" のいずれか。
    // detail には終了時の出力や、休憩時間などの補足が入る。
    private Func<string, string, Task>? onEvent;

    private ValorantCycleSettings settings;
    private CancellationTokenSource? loopCts;
    private Task? loopTask;
    private Process? runningProcess;
    private readonly Random random = new();

    public ValorantCycleRunner(
        AppPaths appPaths,
        Action<string, Exception?> writeLog,
        Func<string, string, Task>? onEvent = null)
    {
        this.appPaths = appPaths;
        this.writeLog = writeLog;
        this.onEvent = onEvent;
        statePath = Path.Combine(appPaths.ConfigDirectory, "valorant-cycle.json");
        settings = LoadSettings();
    }

    /// <summary>現在サイクルループが動作中かどうか。</summary>
    public bool IsRunning
    {
        get
        {
            lock (gate)
            {
                return loopTask is { IsCompleted: false };
            }
        }
    }

    /// <summary>設定のスナップショットを返す。</summary>
    public ValorantCycleSettings GetSettings()
    {
        lock (gate)
        {
            return settings;
        }
    }

    /// <summary>
    /// サイクルのイベント（開始/終了/休憩）通知先を設定する。
    /// コンストラクタで渡せない場合に、後から接続するために使う。
    /// </summary>
    public void SetEventHandler(Func<string, string, Task>? handler)
    {
        lock (gate)
        {
            onEvent = handler;
        }
    }

    /// <summary>機能の有効/無効を設定して保存する。</summary>
    public void SetEnabled(bool enabled)
    {
        lock (gate)
        {
            settings = settings with { Enabled = enabled };
            SaveSettings(settings);
        }
    }

    /// <summary>実行するスクリプトを設定して保存する。</summary>
    public void SetScript(string script)
    {
        lock (gate)
        {
            settings = settings with { Script = script ?? string.Empty };
            SaveSettings(settings);
        }
    }

    /// <summary>実行・休憩のランダム範囲（分）を設定して保存する。</summary>
    public void SetTiming(double runMin, double runMax, double restMin, double restMax)
    {
        // 最低値を下回らないよう、また min <= max を保証する。
        runMin = Math.Max(0.05, runMin);
        runMax = Math.Max(runMin, runMax);
        restMin = Math.Max(0.05, restMin);
        restMax = Math.Max(restMin, restMax);

        lock (gate)
        {
            settings = settings with
            {
                RunMinMinutes = runMin,
                RunMaxMinutes = runMax,
                RestMinMinutes = restMin,
                RestMaxMinutes = restMax,
            };
            SaveSettings(settings);
        }
    }

    /// <summary>
    /// サイクルを開始する。既に動作中の場合は何もしない。
    /// enabled が false、または script が空の場合は開始しない。
    /// </summary>
    public void Start()
    {
        lock (gate)
        {
            if (loopTask is { IsCompleted: false })
            {
                return;
            }

            if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.Script))
            {
                return;
            }

            loopCts = new CancellationTokenSource();
            CancellationToken token = loopCts.Token;
            loopTask = Task.Run(() => RunLoopAsync(token));
            writeLog("Valorant cycle loop started.", null);
        }
    }

    /// <summary>
    /// サイクルを停止する。実行中のコマンドがあれば kill する。
    /// </summary>
    public void Stop()
    {
        CancellationTokenSource? cts;
        Process? proc;
        lock (gate)
        {
            cts = loopCts;
            proc = runningProcess;
            runningProcess = null;
        }

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        TryKill(proc);
        writeLog("Valorant cycle loop stop requested.", null);
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                ValorantCycleSettings snapshot;
                lock (gate)
                {
                    snapshot = settings;
                }

                if (!snapshot.Enabled || string.IsNullOrWhiteSpace(snapshot.Script))
                {
                    break;
                }

                // ① 実行時間・休憩時間をランダムに決める（分→ミリ秒）。
                double runMinutes = NextDouble(snapshot.RunMinMinutes, snapshot.RunMaxMinutes);
                double restMinutes = NextDouble(snapshot.RestMinMinutes, snapshot.RestMaxMinutes);

                // ② コマンドを起動し、出力収集を開始する。
                await NotifyAsync("開始", $"実行 {runMinutes:0.0} 分（予定）").ConfigureAwait(false);
                string collected = await RunOnceAsync(snapshot.Script, runMinutes, token).ConfigureAwait(false);

                // ③ 終了通知（収集した出力つき）。
                await NotifyAsync("終了", collected).ConfigureAwait(false);

                if (token.IsCancellationRequested)
                {
                    break;
                }

                // ④ ランダム休憩。
                await NotifyAsync("休憩", $"休憩 {restMinutes:0.0} 分").ConfigureAwait(false);
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(restMinutes), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (Exception exception)
        {
            writeLog("Valorant cycle loop failed.", exception);
        }
        finally
        {
            writeLog("Valorant cycle loop ended.", null);
        }
    }

    /// <summary>
    /// コマンドを1回起動し、最大 runMinutes 分だけ動かして kill する。
    /// コマンドが自然終了した場合は、その時点で戻る（案A: すぐ休憩へ）。
    /// 収集した出力（上限つき）を返す。
    /// </summary>
    private async Task<string> RunOnceAsync(string script, double runMinutes, CancellationToken token)
    {
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
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(wrappedScript)));

        var process = new Process { StartInfo = startInfo };
        var outputBuilder = new StringBuilder();
        var outputLock = new object();
        bool truncated = false;

        void Collect(string? data)
        {
            if (data is null)
            {
                return;
            }

            lock (outputLock)
            {
                if (outputBuilder.Length >= MaxCollectedOutputChars)
                {
                    truncated = true;
                    return;
                }

                outputBuilder.AppendLine(data);
                if (outputBuilder.Length >= MaxCollectedOutputChars)
                {
                    truncated = true;
                }
            }
        }

        process.OutputDataReceived += (_, args) => Collect(args.Data);
        process.ErrorDataReceived += (_, args) => Collect(args.Data);

        try
        {
            if (!process.Start())
            {
                return "(コマンドを開始できませんでした)";
            }

            lock (gate)
            {
                runningProcess = process;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // 実行時間ぶん待つ。ただしコマンドが先に自然終了したら、その時点で抜ける。
            using var runCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            try
            {
                Task waitExit = process.WaitForExitAsync(runCts.Token);
                Task delay = Task.Delay(TimeSpan.FromMinutes(runMinutes), runCts.Token);
                await Task.WhenAny(waitExit, delay).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 停止要求。下の finally で kill される。
            }
        }
        catch (Exception exception)
        {
            writeLog("Valorant cycle command execution failed.", exception);
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(runningProcess, process))
                {
                    runningProcess = null;
                }
            }

            TryKill(process);
            process.Dispose();
        }

        string result;
        lock (outputLock)
        {
            result = outputBuilder.ToString().TrimEnd();
        }

        if (truncated)
        {
            result += "\n…(出力が長いため以降を省略しました)";
        }

        return result.Length == 0 ? "(出力なし)" : result;
    }

    private async Task NotifyAsync(string phase, string detail)
    {
        if (onEvent is null)
        {
            return;
        }

        try
        {
            await onEvent(phase, detail).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            writeLog("Valorant cycle event notification failed.", exception);
        }
    }

    private double NextDouble(double min, double max)
    {
        if (max <= min)
        {
            return min;
        }

        return min + (random.NextDouble() * (max - min));
    }

    private static void TryKill(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // 既に終了している等は無視。
        }
    }

    private ValorantCycleSettings LoadSettings()
    {
        try
        {
            if (File.Exists(statePath))
            {
                string json = File.ReadAllText(statePath);
                ValorantCycleSettings? loaded =
                    System.Text.Json.JsonSerializer.Deserialize<ValorantCycleSettings>(json);
                if (loaded is not null)
                {
                    return Normalize(loaded);
                }
            }
        }
        catch (Exception exception)
        {
            writeLog("Valorant cycle settings load failed; using defaults.", exception);
        }

        return ValorantCycleSettings.Default;
    }

    private void SaveSettings(ValorantCycleSettings value)
    {
        try
        {
            appPaths.EnsureDirectories();
            Directory.CreateDirectory(Path.GetDirectoryName(statePath) ?? appPaths.ConfigDirectory);
            string json = System.Text.Json.JsonSerializer.Serialize(
                value,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(statePath, json);
        }
        catch (Exception exception)
        {
            writeLog("Valorant cycle settings save failed.", exception);
        }
    }

    private static ValorantCycleSettings Normalize(ValorantCycleSettings value)
    {
        double runMin = Math.Max(0.05, value.RunMinMinutes);
        double runMax = Math.Max(runMin, value.RunMaxMinutes);
        double restMin = Math.Max(0.05, value.RestMinMinutes);
        double restMax = Math.Max(restMin, value.RestMaxMinutes);
        return value with
        {
            Script = value.Script ?? string.Empty,
            RunMinMinutes = runMin,
            RunMaxMinutes = runMax,
            RestMinMinutes = restMin,
            RestMaxMinutes = restMax,
        };
    }
}

/// <summary>サイクルの設定。JSON で永続化される。</summary>
public sealed record ValorantCycleSettings
{
    public bool Enabled { get; init; }
    public string Script { get; init; } = string.Empty;
    public double RunMinMinutes { get; init; } = 1.0;
    public double RunMaxMinutes { get; init; } = 2.0;
    public double RestMinMinutes { get; init; } = 1.0;
    public double RestMaxMinutes { get; init; } = 2.0;

    public static ValorantCycleSettings Default => new();
}
