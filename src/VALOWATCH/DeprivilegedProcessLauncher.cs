using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace VALOWATCH;

/// <summary>
/// 管理者権限（高整合性レベル）のプロセスから、通常権限（中整合性レベル）の
/// 子プロセスを起動するためのヘルパー。
///
/// VALOWATCH 本体が管理者権限で常駐していると、画面キャプチャ（BitBlt / DXGI）が
/// 権限境界で弾かれて真っ黒になる。そこで、キャプチャ処理だけを「通常権限の子プロセス」
/// で実行することで、本体は管理者のまま、キャプチャは成功させる。
///
/// 実現方法：デスクトップで動いている explorer.exe のトークンを複製し、
/// CreateProcessWithTokenW でそのトークンを使って子プロセスを起動する。
/// explorer.exe は必ず通常権限（中整合性）で動いているため、そのトークンで
/// 起動した子プロセスも通常権限になる。
///
/// 段階1（このクラス単体）では、既存コードには一切接続しない。
/// </summary>
internal static class DeprivilegedProcessLauncher
{
    /// <summary>
    /// 通常権限で子プロセスを起動できるか（＝現在管理者権限で、explorer が見つかるか）。
    /// 通常権限で動いている場合は、そもそも降格不要なので false を返す。
    /// </summary>
    public static bool IsDeprivilegationPossible()
    {
        try
        {
            if (!IsCurrentProcessElevated())
            {
                // 既に通常権限。降格の必要なし。
                return false;
            }

            return TryGetExplorerProcessId(out _);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 指定した実行ファイルを、通常権限（explorer と同じ整合性レベル）で起動する。
    /// 成功したら起動したプロセスを返す。失敗したら例外。
    /// </summary>
    /// <param name="fileName">実行ファイルのフルパス。</param>
    /// <param name="arguments">コマンドライン引数。</param>
    /// <param name="workingDirectory">作業ディレクトリ（null なら既定）。</param>
    public static Process StartAsNormalUser(
        string fileName,
        string arguments,
        string? workingDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("実行ファイルのパスが空です。", nameof(fileName));
        }

        if (!TryGetExplorerProcessId(out uint explorerPid))
        {
            throw new InvalidOperationException("explorer.exe が見つからないため、通常権限での起動ができません。");
        }

        IntPtr explorerHandle = OpenProcess(PROCESS_QUERY_INFORMATION, false, explorerPid);
        if (explorerHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "explorer.exe のプロセスを開けませんでした。");
        }

        IntPtr explorerToken = IntPtr.Zero;
        IntPtr duplicatedToken = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(
                    explorerHandle,
                    TOKEN_DUPLICATE | TOKEN_ASSIGN_PRIMARY | TOKEN_QUERY | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID,
                    out explorerToken))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "explorer のトークンを開けませんでした。");
            }

            if (!DuplicateTokenEx(
                    explorerToken,
                    TOKEN_ALL_ACCESS,
                    IntPtr.Zero,
                    SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation,
                    TOKEN_TYPE.TokenPrimary,
                    out duplicatedToken))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "トークンの複製に失敗しました。");
            }

            var startupInfo = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = "winsta0\\default",
            };

            string commandLine = BuildCommandLine(fileName, arguments);
            var commandLineBuffer = new StringBuilder(commandLine);

            bool created = CreateProcessWithTokenW(
                duplicatedToken,
                LOGON_WITH_PROFILE,
                fileName,
                commandLineBuffer,
                CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW,
                IntPtr.Zero,
                workingDirectory,
                ref startupInfo,
                out PROCESS_INFORMATION processInformation);

            if (!created)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "通常権限での子プロセス起動に失敗しました。");
            }

            try
            {
                return Process.GetProcessById((int)processInformation.dwProcessId);
            }
            finally
            {
                if (processInformation.hProcess != IntPtr.Zero)
                {
                    CloseHandle(processInformation.hProcess);
                }

                if (processInformation.hThread != IntPtr.Zero)
                {
                    CloseHandle(processInformation.hThread);
                }
            }
        }
        finally
        {
            if (duplicatedToken != IntPtr.Zero)
            {
                CloseHandle(duplicatedToken);
            }

            if (explorerToken != IntPtr.Zero)
            {
                CloseHandle(explorerToken);
            }

            CloseHandle(explorerHandle);
        }
    }

    private static string BuildCommandLine(string fileName, string arguments)
    {
        string quotedFile = "\"" + fileName + "\"";
        if (string.IsNullOrEmpty(arguments))
        {
            return quotedFile;
        }

        return quotedFile + " " + arguments;
    }

    private static bool TryGetExplorerProcessId(out uint processId)
    {
        processId = 0;
        Process[] explorers = Process.GetProcessesByName("explorer");
        try
        {
            if (explorers.Length == 0)
            {
                return false;
            }

            // 最初に見つかった explorer を使う。
            processId = (uint)explorers[0].Id;
            return true;
        }
        finally
        {
            foreach (Process explorer in explorers)
            {
                explorer.Dispose();
            }
        }
    }

    private static bool IsCurrentProcessElevated()
    {
        IntPtr tokenHandle = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out tokenHandle))
            {
                return false;
            }

            var elevation = new TOKEN_ELEVATION();
            int elevationSize = Marshal.SizeOf<TOKEN_ELEVATION>();
            IntPtr elevationPtr = Marshal.AllocHGlobal(elevationSize);
            try
            {
                Marshal.StructureToPtr(elevation, elevationPtr, false);
                if (!GetTokenInformation(
                        tokenHandle,
                        TOKEN_INFORMATION_CLASS.TokenElevation,
                        elevationPtr,
                        (uint)elevationSize,
                        out _))
                {
                    return false;
                }

                elevation = Marshal.PtrToStructure<TOKEN_ELEVATION>(elevationPtr);
                return elevation.TokenIsElevated != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(elevationPtr);
            }
        }
        finally
        {
            if (tokenHandle != IntPtr.Zero)
            {
                CloseHandle(tokenHandle);
            }
        }
    }

    // ==== P/Invoke 定義 ====

    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint TOKEN_ADJUST_DEFAULT = 0x0080;
    private const uint TOKEN_ADJUST_SESSIONID = 0x0100;
    private const uint TOKEN_ALL_ACCESS = 0xF01FF;
    private const uint LOGON_WITH_PROFILE = 0x00000001;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW = 0x08000000;

    private enum SECURITY_IMPERSONATION_LEVEL
    {
        SecurityAnonymous,
        SecurityIdentification,
        SecurityImpersonation,
        SecurityDelegation,
    }

    private enum TOKEN_TYPE
    {
        TokenPrimary = 1,
        TokenImpersonation,
    }

    private enum TOKEN_INFORMATION_CLASS
    {
        TokenElevation = 20,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_ELEVATION
    {
        public uint TokenIsElevated;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr existingToken,
        uint desiredAccess,
        IntPtr tokenAttributes,
        SECURITY_IMPERSONATION_LEVEL impersonationLevel,
        TOKEN_TYPE tokenType,
        out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        TOKEN_INFORMATION_CLASS tokenInformationClass,
        IntPtr tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(
        IntPtr token,
        uint logonFlags,
        string? applicationName,
        StringBuilder commandLine,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);
}
