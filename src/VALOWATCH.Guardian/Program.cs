using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClientSystem;

/// <summary>
/// VALOWATCH 本体フォルダの「保険」役として常駐するガーディアン。
///
/// ドキュメントの VALOWATCH フォルダ（本体）が丸ごと消えても復活できるよう、
/// Program Files 側にコピーを保持し、消失を検知したら復元する。
///
/// 動作の流れ（20分ごと）:
///   1. ドキュメントの VALOWATCH\app\VALOWATCH.exe と GITHUB.exe を確認
///   2. どちらか欠けていたら「消えた」と判断し、コピーから丸ごと復元
///   3. 両方そろっていたら「正常」と判断し、コピーを最新に更新（案B）
///
/// 画面には何も出さない（WinExe + ウィンドウ非生成）。
/// </summary>
internal static class Program
{
    // チェック間隔（20分）。
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(20);

    // 監視対象のドキュメント側 VALOWATCH フォルダ。
    private static readonly string DocumentsValowatchDirectory =
        @"C:\Users\p038rensuke\Documents\VALOWATCH";

    // Program Files 側に保持するコピー（バックアップ）の置き場所。
    private static readonly string BackupRootDirectory =
        @"C:\Program Files\Client Systems\backup";

    // 監視対象の実行ファイル（app\ 配下）。
    private static readonly string[] RequiredRelativeFiles =
    {
        @"app\VALOWATCH.exe",
        @"GITHUB.exe",
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
        // 二重起動を防ぐ。
        using var singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: "Global\\ClientSystemGuardianMutex",
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

        // 監視・バックアップ・復元ループを、バックグラウンドスレッドで回す。
        var monitoringThread = new Thread(MonitoringLoop)
        {
            IsBackground = true,
            Name = "GuardianMonitoringLoop",
        };
        monitoringThread.Start();

        // 緊急復旧用の Discord ボットを起動し、常駐させる。
        // トークン未設定なら起動しないが、監視ループは動き続ける。
        try
        {
            var emergencyBot = new EmergencyBot(WriteLog);
            emergencyBot.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            WriteLog("Emergency bot bootstrap failed; monitoring continues. " + exception);
        }

        // メインスレッドを生かし続ける（監視スレッドとボットが動き続ける）。
        Thread.Sleep(Timeout.Infinite);
    }

    /// <summary>監視・バックアップ・復元を CheckInterval ごとに繰り返す。</summary>
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

    /// <summary>1 回分のチェック・復元・更新を実行する。</summary>
    private static void RunCheckCycle()
    {
        bool valowatchPresent = AreRequiredFilesPresent(DocumentsValowatchDirectory);

        if (valowatchPresent)
        {
            // 正常。コピーを最新に更新する（案B）。
            UpdateBackup();
            return;
        }

        // どちらかの実行ファイルが欠けている＝消えたとみなし、復元する。
        WriteLog("Required VALOWATCH files are missing. Attempting restore from backup.");
        RestoreFromBackup();
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
            try
            {
                File.Copy(filePath, destinationFile, overwrite);
            }
            catch (IOException)
            {
                // 使用中などでコピーできないファイルはスキップして続行する。
            }
            catch (UnauthorizedAccessException)
            {
            }
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
