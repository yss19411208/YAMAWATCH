using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace VALOWATCH.Installer;

internal static class Program
{
    private const string RegistryRunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RegistryValueName = "VALOWATCH";
    private const string EmbeddedExecutableResourceName = "VALOWATCH.exe";
    private const string EmbeddedGitHubResourceName = "GITHUB.exe";
    private const string EmbeddedStartAgentResourceName = "VALOWATCH_Start.exe";
    private const string StartAgentFileName = "VALOWATCH_Start.exe";
    private const string EmbeddedEnvResourceName = "InstallerEnv/.env";
    private const string StartupCommandFileName = "VALOWATCH.cmd";
    private const string KeepAliveScheduledTaskName = "VALOWATCH KeepAlive";
    private const string LogonScheduledTaskName = "VALOWATCH Logon";
    private const string StartAgentKeepAliveScheduledTaskName = "VALOWATCH StartAgent KeepAlive";
    private const string StartAgentLogonScheduledTaskName = "VALOWATCH StartAgent Logon";
    private const string PendingInstallerReportFileName = "installer-result.pending.log";
    private const int DiscordMessageMaximumLength = 1900;
    private static readonly TimeSpan ProcessRepairWaitTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DiscordReportTimeout = TimeSpan.FromSeconds(15);
    private const int KeepAliveIntervalMinutes = 5;
    private const int InstallerReportMaximumLogLines = 200;
    private static readonly object InstallerSessionLogLock = new();
    private static readonly List<string> InstallerSessionLogLines = [];
    private static readonly (string ResourceName, string FileName)[] NativeDependencyResources =
    [
        ("Native/libdave.dll", "libdave.dll"),
        ("Native/libsodium.dll", "libsodium.dll"),
        ("Native/opus.dll", "opus.dll")
    ];

    private readonly record struct InstallProgress(int Percent, string Message);
    private sealed record InstallerOptions(
        bool Silent,
        string? InstallDirectory,
        bool StartAfterInstall,
        bool RegisterStartup,
        bool StopAllValowatchProcesses,
        bool MarkUpdateCompleted,
        bool CleanReinstall);

    private sealed record SelfRepairResult(
        bool HasBlockingFailure,
        string Summary,
        IReadOnlyList<string> ReportLines);

    private sealed record DirectDiscordReportSettings(
        string BotToken,
        ulong TextChannelId);

    [STAThread]
    private static void Main(string[] args)
    {
        InstallerOptions options = ParseInstallerOptions(args);
        if (options.Silent)
        {
            Environment.ExitCode = RunSilentInstallation(options);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new SetupForm());
    }

    private static InstallerOptions ParseInstallerOptions(IReadOnlyList<string> args)
    {
        bool implicitCleanReinstall = string.Equals(
            Path.GetFileNameWithoutExtension(Environment.ProcessPath),
            "VALOWATCH_Reinstall",
            StringComparison.OrdinalIgnoreCase);
        bool silent = implicitCleanReinstall;
        bool startAfterInstall = true;
        bool registerStartup = true;
        bool stopAllValowatchProcesses = true;
        bool markUpdateCompleted = false;
        bool cleanReinstall = implicitCleanReinstall;
        string? installDirectory = null;

        for (int argumentIndex = 0; argumentIndex < args.Count; argumentIndex++)
        {
            string argument = args[argumentIndex];
            if (IsOption(argument, "--silent", "/silent", "/s", "--quiet", "/quiet"))
            {
                silent = true;
                continue;
            }

            if (IsOption(argument, "--no-start", "/no-start"))
            {
                startAfterInstall = false;
                continue;
            }

            if (IsOption(argument, "--no-startup", "/no-startup"))
            {
                registerStartup = false;
                continue;
            }

            if (IsOption(argument, "--only-target-process", "/only-target-process"))
            {
                stopAllValowatchProcesses = false;
                continue;
            }

            if (IsOption(argument, "--update", "/update"))
            {
                markUpdateCompleted = true;
                continue;
            }

            if (IsOption(argument, "--clean-reinstall", "/clean-reinstall"))
            {
                silent = true;
                cleanReinstall = true;
                continue;
            }

            const string installDirectoryPrefix = "--install-dir=";
            if (argument.StartsWith(installDirectoryPrefix, StringComparison.OrdinalIgnoreCase))
            {
                installDirectory = argument[installDirectoryPrefix.Length..];
                continue;
            }

            if (IsOption(argument, "--install-dir", "/install-dir") && argumentIndex + 1 < args.Count)
            {
                installDirectory = args[++argumentIndex];
            }
        }

        return new InstallerOptions(
            silent,
            installDirectory,
            startAfterInstall,
            registerStartup,
            stopAllValowatchProcesses,
            markUpdateCompleted,
            cleanReinstall);
    }

    private static bool IsOption(string argument, params string[] optionNames)
    {
        return optionNames.Any(optionName => string.Equals(argument, optionName, StringComparison.OrdinalIgnoreCase));
    }

    private static void WriteInstallerLog(string message, Exception? exception = null)
    {
        string exceptionText = exception is null ? string.Empty : $" Exception: {exception}";
        string logLine = $"{DateTimeOffset.Now:O} [Setup] {message}{exceptionText}";
        lock (InstallerSessionLogLock)
        {
            InstallerSessionLogLines.Add(logLine);
        }

        try
        {
            string logDirectory = Path.Combine(Path.GetTempPath(), "VALOWATCH");
            Directory.CreateDirectory(logDirectory);
            string logFilePath = Path.Combine(logDirectory, "VALOWATCH_Setup.log");
            File.AppendAllText(logFilePath, logLine + Environment.NewLine);
        }
        catch (Exception logException) when (logException is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class SetupForm : Form
    {
        private readonly TextBox installDirectoryTextBox = new();
        private readonly Button browseButton = new();
        private readonly Button installButton = new();
        private readonly Button cancelButton = new();
        private readonly ProgressBar progressBar = new();
        private readonly Label progressLabel = new();
        private readonly Label statusLabel = new();

        public SetupForm()
        {
            BuildInterface();
        }

        private void BuildInterface()
        {
            Text = "VALOWATCH Setup";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(780, 650);
            MinimumSize = new Size(780, 650);
            Font = new Font("Yu Gothic UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            TableLayoutPanel rootLayout = new()
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(24, 20, 24, 20),
                ColumnCount = 1,
                RowCount = 9
            };
            rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 204F));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18F));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58F));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));

            Label titleLabel = new()
            {
                Dock = DockStyle.Fill,
                Text = "VALOWATCH 暫定版セットアップ",
                Font = new Font(Font.FontFamily, 16F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            };

            TextBox requirementTextBox = new()
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = SystemColors.Control,
                Text =
                    "最初に確認してください\r\n" +
                    "1. この暫定版は外部オーバーレイ基盤を使いません。\r\n" +
                    "2. VALORANT を起動した後、設定 → グラフィック → 一般 を開きます。\r\n" +
                    "   画面モードで「ウィンドウフルスクリーン」を選んで「適用」を押してください。\r\n\r\n" +
                    "インストール先は C:\\ 直下や Program Files を避けて、ユーザーが書き込めるフォルダーを指定してください。",
                TabStop = false
            };

            Label installDirectoryLabel = new()
            {
                Dock = DockStyle.Fill,
                Text = "インストール先フォルダー",
                TextAlign = ContentAlignment.MiddleLeft
            };

            TableLayoutPanel installDirectoryLayout = new()
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1
            };
            installDirectoryLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            installDirectoryLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116F));

            installDirectoryTextBox.Dock = DockStyle.Fill;
            installDirectoryTextBox.Margin = new Padding(0, 4, 12, 4);
            installDirectoryTextBox.Text = GetDefaultInstallDirectory();

            browseButton.Text = "参照...";
            browseButton.Dock = DockStyle.Fill;
            browseButton.Margin = new Padding(0, 3, 0, 4);
            browseButton.Click += (_, _) => BrowseInstallDirectory();

            installDirectoryLayout.Controls.Add(installDirectoryTextBox, 0, 0);
            installDirectoryLayout.Controls.Add(browseButton, 1, 0);

            progressBar.Dock = DockStyle.Fill;
            progressBar.Minimum = 0;
            progressBar.Maximum = 100;

            TableLayoutPanel statusLayout = new()
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1
            };
            statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84F));

            progressLabel.AutoSize = false;
            progressLabel.Text = "0%";
            progressLabel.TextAlign = ContentAlignment.MiddleRight;
            progressLabel.Dock = DockStyle.Fill;

            statusLabel.AutoSize = false;
            statusLabel.Text = "インストールを開始できます。";
            statusLabel.Dock = DockStyle.Fill;
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;

            statusLayout.Controls.Add(statusLabel, 0, 0);
            statusLayout.Controls.Add(progressLabel, 1, 0);

            TableLayoutPanel buttonLayout = new()
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1
            };
            buttonLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            buttonLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128F));
            buttonLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128F));

            installButton.Text = "インストール";
            installButton.Dock = DockStyle.Fill;
            installButton.Margin = new Padding(8, 5, 4, 5);
            installButton.Click += InstallButton_Click;

            cancelButton.Text = "キャンセル";
            cancelButton.Dock = DockStyle.Fill;
            cancelButton.Margin = new Padding(4, 5, 0, 5);
            cancelButton.Click += (_, _) => Close();

            buttonLayout.Controls.Add(new Label(), 0, 0);
            buttonLayout.Controls.Add(installButton, 1, 0);
            buttonLayout.Controls.Add(cancelButton, 2, 0);

            rootLayout.Controls.Add(titleLabel, 0, 0);
            rootLayout.Controls.Add(requirementTextBox, 0, 1);
            rootLayout.Controls.Add(installDirectoryLabel, 0, 2);
            rootLayout.Controls.Add(installDirectoryLayout, 0, 3);
            rootLayout.Controls.Add(progressBar, 0, 5);
            rootLayout.Controls.Add(statusLayout, 0, 6);
            rootLayout.Controls.Add(buttonLayout, 0, 8);

            Controls.Add(rootLayout);
        }

        private void BrowseInstallDirectory()
        {
            using FolderBrowserDialog folderBrowserDialog = new()
            {
                Description = "VALOWATCH のインストール先を選んでください。",
                InitialDirectory = Directory.Exists(installDirectoryTextBox.Text)
                    ? installDirectoryTextBox.Text
                    : GetDefaultInstallDirectory(),
                UseDescriptionForTitle = true
            };

            if (folderBrowserDialog.ShowDialog(this) == DialogResult.OK)
            {
                installDirectoryTextBox.Text = folderBrowserDialog.SelectedPath;
            }
        }

        private async void InstallButton_Click(object? sender, EventArgs eventArgs)
        {
            string selectedInstallDirectory = installDirectoryTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(selectedInstallDirectory))
            {
                MessageBox.Show(
                    this,
                    "インストール先を指定してください。",
                    "VALOWATCH Setup",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            try
            {
                selectedInstallDirectory = NormalizeInstallDirectoryPath(selectedInstallDirectory);
                ValidateInstallDirectorySelection(selectedInstallDirectory);
                installDirectoryTextBox.Text = selectedInstallDirectory;
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException)
            {
                MessageBox.Show(
                    this,
                    exception.Message,
                    "VALOWATCH Setup",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            SetInstallingState(true);
            Progress<InstallProgress> progress = new(ReportProgress);

            try
            {
                await Task.Run(() => RunInstallation(
                    selectedInstallDirectory,
                    progress,
                    startAfterInstall: true,
                    registerStartup: true,
                    stopAllValowatchProcesses: true,
                    markUpdateCompleted: false,
                    cleanReinstall: false));
                ReportProgress(new InstallProgress(100, "インストールが完了しました。VALOWATCH を起動しています。"));
                MessageBox.Show(
                    this,
                    "インストールが完了しました。",
                    "VALOWATCH Setup",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                Close();
            }
            catch (Exception exception)
            {
                ReportProgress(new InstallProgress(progressBar.Value, "インストールに失敗しました。"));
                MessageBox.Show(
                    this,
                    $"VALOWATCH setup failed.\n\n{exception.Message}",
                    "VALOWATCH Setup",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                if (!IsDisposed)
                {
                    SetInstallingState(false);
                }
            }
        }

        private void SetInstallingState(bool isInstalling)
        {
            installDirectoryTextBox.Enabled = !isInstalling;
            browseButton.Enabled = !isInstalling;
            installButton.Enabled = !isInstalling;
            cancelButton.Enabled = !isInstalling;
        }

        private void ReportProgress(InstallProgress progress)
        {
            int safePercent = Math.Clamp(progress.Percent, 0, 100);
            progressBar.Value = safePercent;
            progressLabel.Text = $"{safePercent}%";
            statusLabel.Text = progress.Message;
        }
    }

    private static int RunSilentInstallation(InstallerOptions options)
    {
        string installDirectory = string.IsNullOrWhiteSpace(options.InstallDirectory)
            ? GetDefaultInstallDirectory()
            : options.InstallDirectory;
        InlineProgress progress = new(progressValue =>
            WriteInstallerLog($"{progressValue.Percent}% {progressValue.Message}"));

        try
        {
            if (options.CleanReinstall)
            {
                installDirectory = ResolveCleanReinstallInstallDirectory(installDirectory);
            }

            RunInstallation(
                installDirectory,
                progress,
                options.StartAfterInstall,
                options.RegisterStartup,
                options.StopAllValowatchProcesses,
                options.MarkUpdateCompleted,
                options.CleanReinstall);
            WriteInstallerLog("Silent installation completed.");
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException)
        {
            WriteInstallerLog("Silent installation failed.", exception);
            if (options.CleanReinstall)
            {
                TryWriteAndSendInstallerReport(
                    installDirectory,
                    succeeded: false,
                    options.StartAfterInstall,
                    options.RegisterStartup,
                    exception,
                    additionalReportLines: []);
            }

            return 1;
        }
    }

    private static void RunInstallation(
        string installDirectory,
        IProgress<InstallProgress> progress,
        bool startAfterInstall,
        bool registerStartup,
        bool stopAllValowatchProcesses,
        bool markUpdateCompleted,
        bool cleanReinstall)
    {
        progress.Report(new InstallProgress(2, "インストール先を準備しています。"));
        if (cleanReinstall)
        {
            installDirectory = ResolveCleanReinstallInstallDirectory(installDirectory);
            progress.Report(new InstallProgress(4, "標準フォルダーへ再配置します。"));
        }
        else
        {
            installDirectory = NormalizeInstallDirectoryPath(installDirectory);
        }

        ValidateInstallDirectorySelection(installDirectory);

        string installedExecutablePath = Path.Combine(installDirectory, "VALOWATCH.exe");
        string workspaceRoot = GetWorkspaceRootForInstallDirectory(installDirectory);
        string installedGitHubPath = Path.Combine(workspaceRoot, "GITHUB.exe");
        string installedStartAgentPath = Path.Combine(workspaceRoot, StartAgentFileName);
        bool replacesExistingInstallation = File.Exists(installedExecutablePath);
        SelfRepairResult selfRepairResult = new(false, "not run", []);

        progress.Report(new InstallProgress(8, "起動中の VALOWATCH をすべて停止しています。"));
        StopRunningInstalledApp(installedExecutablePath, stopAllValowatchProcesses);
        StopRunningUpdateProcesses();

        if (cleanReinstall)
        {
            progress.Report(new InstallProgress(10, "旧インストールを安全に削除しています。"));
            RemoveStartupRegistration();
            CleanInstalledAppDirectory(installDirectory);
        }

        progress.Report(new InstallProgress(14, "VALOWATCH 本体を展開しています。"));
        ExtractEmbeddedExecutable(installedExecutablePath, progress, 14, 68);

        progress.Report(new InstallProgress(69, "Discord 音声 DLL を展開しています。"));
        ExtractNativeDependencies(installDirectory);
        ExtractEmbeddedFile(EmbeddedGitHubResourceName, installedGitHubPath);
        ExtractEmbeddedFile(EmbeddedStartAgentResourceName, installedStartAgentPath);
        RemoveObsoleteCaptureTools(installDirectory);

        progress.Report(new InstallProgress(70, "Discord bot 設定を配置しています。"));
        EnsureEnvFiles(installDirectory);

        if (registerStartup)
        {
            progress.Report(new InstallProgress(92, "Windows 起動時の自動起動を登録しています。"));
            RegisterStartup(installedGitHubPath, installedStartAgentPath, installDirectory);
        }
        else
        {
            progress.Report(new InstallProgress(92, "Windows 起動時の自動起動登録をスキップしています。"));
        }

        if (markUpdateCompleted || replacesExistingInstallation || cleanReinstall)
        {
            WriteUpdateCompletedMarker(installDirectory);
        }

        if (startAfterInstall)
        {
            progress.Report(new InstallProgress(98, "VALOWATCH を起動しています。"));
            StartGitHubAgent(installedGitHubPath, installDirectory);
            StartStartAgent(installedStartAgentPath, installDirectory);
        }
        else
        {
            progress.Report(new InstallProgress(98, "VALOWATCH の起動をスキップしています。"));
        }

        selfRepairResult = RunSelfRepairChecks(
            installDirectory,
            installedExecutablePath,
            installedGitHubPath,
            installedStartAgentPath,
            registerStartup,
            startAfterInstall);
        if (selfRepairResult.HasBlockingFailure)
        {
            throw new InvalidOperationException($"VALOWATCH self repair failed: {selfRepairResult.Summary}");
        }

        if (cleanReinstall)
        {
            WriteInstallerLog("Clean reinstallation completed and self repair checks passed.");
            TryWriteAndSendInstallerReport(
                installDirectory,
                succeeded: true,
                startAfterInstall,
                registerStartup,
                exception: null,
                selfRepairResult.ReportLines);
        }
    }

    private static void WriteUpdateCompletedMarker(string installDirectory)
    {
        string dataDirectory = Path.Combine(GetWorkspaceRootForInstallDirectory(installDirectory), "data");
        Directory.CreateDirectory(dataDirectory);
        string markerPath = Path.Combine(dataDirectory, "update-completed.pending");
        File.WriteAllText(markerPath, DateTimeOffset.UtcNow.ToString("O"), Encoding.UTF8);
        WriteInstallerLog($"Update completion marker written: {markerPath}");
    }

    private static SelfRepairResult RunSelfRepairChecks(
        string installDirectory,
        string installedExecutablePath,
        string installedGitHubPath,
        string installedStartAgentPath,
        bool registerStartup,
        bool startAfterInstall)
    {
        List<string> reportLines = [];
        List<string> repairIssues = [];
        bool hasBlockingFailure = false;

        AddSelfRepairLine(reportLines, $"InstallDirectory={installDirectory}");
        AddFileCheck(reportLines, repairIssues, "VALOWATCH.exe", installedExecutablePath, required: true);
        AddFileCheck(reportLines, repairIssues, "GITHUB.exe", installedGitHubPath, required: true);
        AddFileCheck(reportLines, repairIssues, StartAgentFileName, installedStartAgentPath, required: true);
        foreach ((_, string nativeDependencyFileName) in NativeDependencyResources)
        {
            AddFileCheck(
                reportLines,
                repairIssues,
                nativeDependencyFileName,
                Path.Combine(installDirectory, nativeDependencyFileName),
                required: true);
        }

        string workspaceRoot = GetWorkspaceRootForInstallDirectory(installDirectory);
        string envPath = Path.Combine(workspaceRoot, "installer", ".env");
        AddFileCheck(reportLines, repairIssues, "installer/.env", envPath, required: false);

        if (registerStartup)
        {
            VerifyStartupRegistration(
                reportLines,
                repairIssues,
                installedGitHubPath,
                installedStartAgentPath,
                installDirectory);
        }
        else
        {
            AddSelfRepairLine(reportLines, "StartupRegistration=skipped");
        }

        if (startAfterInstall)
        {
            bool githubRunning = WaitForProcessFromPath(
                processName: "GITHUB",
                expectedExecutablePath: installedGitHubPath,
                timeout: ProcessRepairWaitTimeout);
            AddSelfRepairLine(reportLines, $"GitHubAgentRunningInitial={githubRunning}");
            bool startAgentRunning = WaitForProcessFromPath(
                processName: "VALOWATCH_Start",
                expectedExecutablePath: installedStartAgentPath,
                timeout: ProcessRepairWaitTimeout);
            AddSelfRepairLine(reportLines, $"StartAgentRunningInitial={startAgentRunning}");

            if (!githubRunning)
            {
                try
                {
                    StartGitHubAgent(installedGitHubPath, installDirectory);
                    AddSelfRepairLine(reportLines, "GitHubAgentRepairStart=attempted");
                }
                catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
                {
                    string startFailure = DescribeProcessStartFailure(exception);
                    repairIssues.Add($"GITHUB start failed: {startFailure}");
                    AddSelfRepairLine(reportLines, $"GitHubAgentRepairStart=failed:{startFailure}");
                    if (IsWindowsApplicationControlBlock(exception))
                    {
                        AddSelfRepairLine(reportLines, "WindowsApplicationControlBlocked=true;BlockedExecutable=GITHUB.exe");
                    }
                }

                githubRunning = WaitForProcessFromPath(
                    processName: "GITHUB",
                    expectedExecutablePath: installedGitHubPath,
                    timeout: ProcessRepairWaitTimeout);
                AddSelfRepairLine(reportLines, $"GitHubAgentRunningAfterRepair={githubRunning}");
            }

            if (!startAgentRunning)
            {
                try
                {
                    StartStartAgent(installedStartAgentPath, installDirectory);
                    AddSelfRepairLine(reportLines, "StartAgentRepairStart=attempted");
                }
                catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
                {
                    string startFailure = DescribeProcessStartFailure(exception);
                    repairIssues.Add($"Start agent start failed: {startFailure}");
                    AddSelfRepairLine(reportLines, $"StartAgentRepairStart=failed:{startFailure}");
                    if (IsWindowsApplicationControlBlock(exception))
                    {
                        AddSelfRepairLine(reportLines, "WindowsApplicationControlBlocked=true;BlockedExecutable=VALOWATCH_Start.exe");
                    }
                }

                startAgentRunning = WaitForProcessFromPath(
                    processName: "VALOWATCH_Start",
                    expectedExecutablePath: installedStartAgentPath,
                    timeout: ProcessRepairWaitTimeout);
                AddSelfRepairLine(reportLines, $"StartAgentRunningAfterRepair={startAgentRunning}");
            }

            if (!githubRunning)
            {
                bool fallbackRunning = TryStartInstalledAppFallback(
                    installedExecutablePath,
                    installDirectory,
                    reportLines,
                    repairIssues);
                AddSelfRepairLine(reportLines, $"VALOWATCHFallbackRunning={fallbackRunning}");
                hasBlockingFailure = !fallbackRunning;
            }
        }
        else
        {
            AddSelfRepairLine(reportLines, "ProcessStartCheck=skipped");
        }

        string summary = repairIssues.Count == 0
            ? "ready"
            : string.Join("; ", repairIssues.Take(8));
        reportLines.Insert(0, $"BlockingFailure={hasBlockingFailure}");
        reportLines.Insert(0, $"Summary={summary}");
        AddSelfRepairLine(reportLines, $"ResultSummary={summary}");
        return new SelfRepairResult(hasBlockingFailure, summary, reportLines);
    }

    private static void AddFileCheck(
        List<string> reportLines,
        List<string> repairIssues,
        string label,
        string filePath,
        bool required)
    {
        FileInfo fileInfo = new(filePath);
        bool exists = fileInfo.Exists && fileInfo.Length > 0;
        AddSelfRepairLine(reportLines, $"{label}=exists:{exists};bytes:{(exists ? fileInfo.Length : 0)};path:{filePath}");
        if (required && !exists)
        {
            repairIssues.Add($"{label} missing");
        }
    }

    private static void VerifyStartupRegistration(
        List<string> reportLines,
        List<string> repairIssues,
        string installedGitHubPath,
        string installedStartAgentPath,
        string installDirectory)
    {
        string expectedCommand = BuildGitHubAgentCommand(installedGitHubPath, installDirectory);
        try
        {
            using RegistryKey? registryKey = Registry.CurrentUser.OpenSubKey(RegistryRunPath, writable: false);
            string? registeredCommand = registryKey?.GetValue(RegistryValueName) as string;
            bool registryMatches = string.Equals(registeredCommand, expectedCommand, StringComparison.OrdinalIgnoreCase);
            AddSelfRepairLine(reportLines, $"StartupRegistryMatches={registryMatches}");
            if (!registryMatches)
            {
                repairIssues.Add("startup registry mismatch");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            repairIssues.Add($"startup registry unreadable: {exception.GetType().Name}");
            AddSelfRepairLine(reportLines, $"StartupRegistryMatches=failed:{exception.GetType().Name}");
        }

        try
        {
            string startupDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string startupCommandPath = Path.Combine(startupDirectory, StartupCommandFileName);
            string startupCommandText = File.Exists(startupCommandPath)
                ? File.ReadAllText(startupCommandPath, Encoding.UTF8)
                : string.Empty;
            bool startupCommandMatches = startupCommandText.Contains(installedGitHubPath, StringComparison.OrdinalIgnoreCase) &&
                startupCommandText.Contains(installedStartAgentPath, StringComparison.OrdinalIgnoreCase) &&
                startupCommandText.Contains(installDirectory, StringComparison.OrdinalIgnoreCase);
            AddSelfRepairLine(reportLines, $"StartupCommandMatches={startupCommandMatches}");
            if (!startupCommandMatches)
            {
                repairIssues.Add("startup command mismatch");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            repairIssues.Add($"startup command unreadable: {exception.GetType().Name}");
            AddSelfRepairLine(reportLines, $"StartupCommandMatches=failed:{exception.GetType().Name}");
        }

        bool keepAliveTaskExists = TryQueryScheduledTask(KeepAliveScheduledTaskName, out string keepAliveTaskDetail);
        AddSelfRepairLine(reportLines, $"KeepAliveTaskExists={keepAliveTaskExists};detail:{keepAliveTaskDetail}");
        bool startAgentKeepAliveTaskExists = TryQueryScheduledTask(StartAgentKeepAliveScheduledTaskName, out string startAgentKeepAliveTaskDetail);
        AddSelfRepairLine(reportLines, $"StartAgentKeepAliveTaskExists={startAgentKeepAliveTaskExists};detail:{startAgentKeepAliveTaskDetail}");
        AddSelfRepairLine(reportLines, "LogonScheduledTasks=skipped;reason:HKCU Run and Startup folder are used for logon startup.");
    }

    private static bool TryQueryScheduledTask(string taskName, out string detail)
    {
        detail = string.Empty;
        try
        {
            string taskSchedulerPath = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
            ProcessStartInfo processStartInfo = new()
            {
                FileName = taskSchedulerPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            processStartInfo.ArgumentList.Add("/Query");
            processStartInfo.ArgumentList.Add("/TN");
            processStartInfo.ArgumentList.Add(taskName);
            processStartInfo.ArgumentList.Add("/FO");
            processStartInfo.ArgumentList.Add("LIST");

            using Process taskSchedulerProcess = Process.Start(processStartInfo)
                ?? throw new InvalidOperationException("Windows Task Scheduler query could not be started.");
            Task<string> outputTask = taskSchedulerProcess.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = taskSchedulerProcess.StandardError.ReadToEndAsync();
            if (!taskSchedulerProcess.WaitForExit(10000))
            {
                taskSchedulerProcess.Kill(entireProcessTree: true);
                throw new TimeoutException("Windows Task Scheduler query timed out.");
            }

            string output = outputTask.GetAwaiter().GetResult().Trim();
            string error = errorTask.GetAwaiter().GetResult().Trim();
            detail = SanitizeInstallerReportText(taskSchedulerProcess.ExitCode == 0 ? output : error);
            detail = detail
                .Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal);
            if (detail.Length > 240)
            {
                detail = detail[..240];
            }

            return taskSchedulerProcess.ExitCode == 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            detail = exception.GetType().Name;
            return false;
        }
    }

    private static bool TryStartInstalledAppFallback(
        string installedExecutablePath,
        string installDirectory,
        List<string> reportLines,
        List<string> repairIssues)
    {
        try
        {
            ProcessStartInfo processStartInfo = new()
            {
                FileName = installedExecutablePath,
                UseShellExecute = true,
                WorkingDirectory = installDirectory,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(processStartInfo);
            AddSelfRepairLine(reportLines, "VALOWATCHFallbackStart=attempted");
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            string startFailure = DescribeProcessStartFailure(exception);
            repairIssues.Add($"VALOWATCH fallback start failed: {startFailure}");
            AddSelfRepairLine(reportLines, $"VALOWATCHFallbackStart=failed:{startFailure}");
            if (IsWindowsApplicationControlBlock(exception))
            {
                AddSelfRepairLine(reportLines, "WindowsApplicationControlBlocked=true;BlockedExecutable=VALOWATCH.exe");
            }

            return false;
        }

        bool appRunning = WaitForProcessFromPath(
            processName: "VALOWATCH",
            expectedExecutablePath: installedExecutablePath,
            timeout: ProcessRepairWaitTimeout);
        if (!appRunning)
        {
            repairIssues.Add("VALOWATCH fallback not running");
        }

        return appRunning;
    }

    private static bool WaitForProcessFromPath(string processName, string expectedExecutablePath, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow <= deadline)
        {
            if (IsProcessRunningFromPath(processName, expectedExecutablePath))
            {
                return true;
            }

            Task.Delay(500).GetAwaiter().GetResult();
        }

        return false;
    }

    private static bool IsProcessRunningFromPath(string processName, string expectedExecutablePath)
    {
        string normalizedExpectedPath = NormalizeExecutablePath(expectedExecutablePath);
        foreach (Process candidateProcess in Process.GetProcessesByName(processName))
        {
            using (candidateProcess)
            {
                if (IsProcessFromExecutablePath(candidateProcess, normalizedExpectedPath))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsProcessFromExecutablePath(Process candidateProcess, string normalizedExpectedPath)
    {
        try
        {
            string? processFileName = candidateProcess.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(processFileName))
            {
                return false;
            }

            return string.Equals(
                NormalizeExecutablePath(processFileName),
                normalizedExpectedPath,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void AddSelfRepairLine(List<string> reportLines, string line)
    {
        string sanitizedLine = SanitizeInstallerReportText(line);
        reportLines.Add(sanitizedLine);
        WriteInstallerLog($"Self repair: {sanitizedLine}");
    }

    private static string DescribeProcessStartFailure(Exception exception)
    {
        if (TryGetWin32Exception(exception, out System.ComponentModel.Win32Exception? win32Exception) &&
            win32Exception is not null)
        {
            string classification = IsWindowsApplicationControlBlock(win32Exception)
                ? "WindowsApplicationControlBlocked"
                : "Win32ProcessStartFailure";
            return $"{classification};NativeErrorCode={win32Exception.NativeErrorCode};Message={TrimForLog(win32Exception.Message, 240)}";
        }

        return $"{exception.GetType().Name};Message={TrimForLog(exception.Message, 240)}";
    }

    private static string DescribeInstallerFailure(Exception? exception)
    {
        if (exception is null)
        {
            return string.Empty;
        }

        if (TryGetWin32Exception(exception, out _))
        {
            return DescribeProcessStartFailure(exception);
        }

        return $"{exception.GetType().Name};Message={TrimForLog(exception.Message, 240)}";
    }

    private static bool IsWindowsApplicationControlBlock(Exception? exception)
    {
        return TryGetWin32Exception(exception, out System.ComponentModel.Win32Exception? win32Exception) &&
            win32Exception is not null &&
            IsWindowsApplicationControlBlockCode(win32Exception.NativeErrorCode);
    }

    private static bool IsWindowsApplicationControlBlockCode(int nativeErrorCode)
    {
        return nativeErrorCode is 4551 or 1260;
    }

    private static bool TryGetWin32Exception(
        Exception? exception,
        out System.ComponentModel.Win32Exception? win32Exception)
    {
        Exception? currentException = exception;
        while (currentException is not null)
        {
            if (currentException is System.ComponentModel.Win32Exception currentWin32Exception)
            {
                win32Exception = currentWin32Exception;
                return true;
            }

            currentException = currentException.InnerException;
        }

        win32Exception = null;
        return false;
    }

    private static void TryWriteAndSendInstallerReport(
        string installDirectory,
        bool succeeded,
        bool startAfterInstall,
        bool registerStartup,
        Exception? exception,
        IReadOnlyList<string> additionalReportLines)
    {
        try
        {
            string normalizedInstallDirectory = ResolveCleanReinstallReportDirectory(installDirectory);
            string workspaceRoot = GetWorkspaceRootForInstallDirectory(normalizedInstallDirectory);
            string logDirectory = Path.Combine(workspaceRoot, "data", "logs");
            Directory.CreateDirectory(logDirectory);

            string reportPath = Path.Combine(logDirectory, PendingInstallerReportFileName);
            string temporaryReportPath = reportPath + ".writing";
            string version = typeof(Program).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? "unknown";
            string[] sessionLogLines;
            lock (InstallerSessionLogLock)
            {
                sessionLogLines = InstallerSessionLogLines
                    .TakeLast(InstallerReportMaximumLogLines)
                    .Select(SanitizeInstallerReportText)
                    .ToArray();
            }

            List<string> reportLines =
            [
                "VALOWATCH installer result",
                $"TimestampUtc={DateTimeOffset.UtcNow:O}",
                $"Result={(succeeded ? "success" : "failure")}",
                $"Version={version}",
                $"OperatingSystem={Environment.OSVersion.VersionString}",
                $"ProcessArchitecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}",
                $"InstallDirectory={SanitizeInstallerReportText(normalizedInstallDirectory)}",
                $"StartAfterInstall={startAfterInstall}",
                $"StartupRegistered={registerStartup}",
                $"FailureType={exception?.GetType().Name ?? string.Empty}",
                $"WindowsApplicationControlBlocked={IsWindowsApplicationControlBlock(exception)}",
                $"FailureDetail={DescribeInstallerFailure(exception)}",
                string.Empty
            ];
            if (additionalReportLines.Count > 0)
            {
                reportLines.Add("Self repair:");
                reportLines.AddRange(additionalReportLines.Select(SanitizeInstallerReportText));
                reportLines.Add(string.Empty);
            }

            reportLines.Add("Session log:");
            reportLines.AddRange(sessionLogLines);

            File.WriteAllLines(temporaryReportPath, reportLines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryReportPath, reportPath, overwrite: true);
            WriteInstallerLog($"Pending Discord installer report written: {reportPath}");
            TrySendInstallerReportDirectly(normalizedInstallDirectory, reportLines, succeeded);
        }
        catch (Exception reportException) when (reportException is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException)
        {
            WriteInstallerLog("Pending Discord installer report could not be written.", reportException);
        }
    }

    private static void TrySendInstallerReportDirectly(
        string installDirectory,
        IReadOnlyList<string> reportLines,
        bool succeeded)
    {
        try
        {
            if (!TryLoadDirectDiscordReportSettings(
                installDirectory,
                out DirectDiscordReportSettings? reportSettings,
                out string settingsStatus))
            {
                WriteInstallerLog($"Direct Discord installer report skipped: {settingsStatus}");
                return;
            }

            DirectDiscordReportSettings activeReportSettings = reportSettings
                ?? throw new InvalidOperationException("Discord report settings were not loaded.");
            IReadOnlyList<string> messageChunks = BuildDiscordReportMessages(reportLines, succeeded);
            using HttpClient httpClient = new()
            {
                Timeout = DiscordReportTimeout
            };
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", activeReportSettings.BotToken);

            foreach (string messageChunk in messageChunks)
            {
                string jsonPayload = JsonSerializer.Serialize(new
                {
                    content = messageChunk,
                    allowed_mentions = new
                    {
                        parse = Array.Empty<string>()
                    }
                });
                using StringContent content = new(jsonPayload, Encoding.UTF8, "application/json");
                using HttpResponseMessage response = httpClient
                    .PostAsync($"https://discord.com/api/v10/channels/{activeReportSettings.TextChannelId}/messages", content)
                    .GetAwaiter()
                    .GetResult();
                if (!response.IsSuccessStatusCode)
                {
                    string responseText = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    throw new InvalidOperationException(
                        $"Discord returned {(int)response.StatusCode} {response.ReasonPhrase}. {TrimForLog(responseText, 400)}");
                }

                Task.Delay(350).GetAwaiter().GetResult();
            }

            WriteInstallerLog($"Direct Discord installer report sent. Chunks: {messageChunks.Count}.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException)
        {
            WriteInstallerLog(
                "Direct Discord installer report could not be sent; pending report remains for VALOWATCH.",
                exception);
        }
    }

    private static bool TryLoadDirectDiscordReportSettings(
        string installDirectory,
        out DirectDiscordReportSettings? reportSettings,
        out string status)
    {
        reportSettings = null;
        Dictionary<string, string> envValues = LoadInstallerEnvValues(installDirectory);
        if (envValues.Count == 0)
        {
            status = "installer .env was not found or empty";
            return false;
        }

        if (TryGetEnvValue(envValues, out string discordEnabledText, "DISCORD_BOT_ENABLED") &&
            IsFalseEnvValue(discordEnabledText))
        {
            status = "DISCORD_BOT_ENABLED is false";
            return false;
        }

        if (!TryGetEnvValue(envValues, out string botToken, "DISCORD_BOT_TOKEN", "DISCORD_TOKEN", "BOT_TOKEN") ||
            IsPlaceholderDiscordToken(botToken))
        {
            status = "Discord bot token is missing";
            return false;
        }

        if (!TryGetEnvValue(envValues, out string channelIdText, "DISCORD_TEXT_CHANNEL_ID", "DISCORD_STATUS_CHANNEL_ID", "TEXT_CHANNEL_ID", "STATUS_CHANNEL_ID") ||
            !ulong.TryParse(channelIdText, out ulong textChannelId) ||
            textChannelId == 0)
        {
            status = "Discord text channel id is missing";
            return false;
        }

        reportSettings = new DirectDiscordReportSettings(botToken, textChannelId);
        status = "ready";
        return true;
    }

    private static Dictionary<string, string> LoadInstallerEnvValues(string installDirectory)
    {
        Dictionary<string, string> mergedValues = new(StringComparer.OrdinalIgnoreCase);
        string workspaceRoot = GetWorkspaceRootForInstallDirectory(installDirectory);
        string installedEnvPath = Path.Combine(workspaceRoot, "installer", ".env");
        string sideBySideEnvPath = Path.Combine(AppContext.BaseDirectory, ".env");

        foreach (string? envText in new[]
        {
            ReadEmbeddedEnvText(),
            TryReadTextFile(sideBySideEnvPath),
            TryReadTextFile(installedEnvPath)
        })
        {
            if (string.IsNullOrWhiteSpace(envText))
            {
                continue;
            }

            (_, Dictionary<string, string> envAssignments) = ReadEnvAssignments(envText);
            foreach ((string key, string value) in envAssignments)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    mergedValues[key] = value;
                }
                else if (!mergedValues.ContainsKey(key))
                {
                    mergedValues[key] = value;
                }
            }
        }

        return mergedValues;
    }

    private static bool TryGetEnvValue(
        IReadOnlyDictionary<string, string> envValues,
        out string value,
        params string[] keys)
    {
        foreach (string key in keys)
        {
            if (envValues.TryGetValue(key, out string? rawValue) && !string.IsNullOrWhiteSpace(rawValue))
            {
                value = rawValue.Trim().Trim('"');
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool IsFalseEnvValue(string value)
    {
        return value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("off", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlaceholderDiscordToken(string value)
    {
        return string.IsNullOrWhiteSpace(value) ||
            value.Contains("PASTE_", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("TOKEN_HERE", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> BuildDiscordReportMessages(IReadOnlyList<string> reportLines, bool succeeded)
    {
        string title = succeeded
            ? "**VALOWATCH 再インストール完了**"
            : "**VALOWATCH 再インストール失敗**";
        List<string> messages = [];
        StringBuilder currentMessage = new(title);
        currentMessage.AppendLine();

        foreach (string reportLine in reportLines.Select(SanitizeInstallerReportText))
        {
            string line = reportLine.Length > 480
                ? reportLine[..480] + "..."
                : reportLine;
            if (currentMessage.Length + line.Length + Environment.NewLine.Length > DiscordMessageMaximumLength)
            {
                messages.Add(currentMessage.ToString().TrimEnd());
                currentMessage.Clear();
                currentMessage.AppendLine(title);
            }

            currentMessage.AppendLine(line);
        }

        if (currentMessage.Length > 0)
        {
            messages.Add(currentMessage.ToString().TrimEnd());
        }

        return messages;
    }

    private static string TrimForLog(string value, int maximumLength)
    {
        string sanitizedValue = SanitizeInstallerReportText(value);
        return sanitizedValue.Length <= maximumLength
            ? sanitizedValue
            : sanitizedValue[..maximumLength] + "...";
    }

    private static string SanitizeInstallerReportText(string value)
    {
        string sanitizedValue = value;
        string? userProfileEnvironmentValue = Environment.GetEnvironmentVariable("USERPROFILE");
        string[] userProfileDirectories =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            userProfileEnvironmentValue ?? string.Empty
        ];
        foreach (string userProfileDirectory in userProfileDirectories
            .Where(profileDirectory => !string.IsNullOrWhiteSpace(profileDirectory))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            sanitizedValue = sanitizedValue.Replace(userProfileDirectory, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        string temporaryDirectory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.IsNullOrWhiteSpace(temporaryDirectory))
        {
            sanitizedValue = sanitizedValue.Replace(temporaryDirectory, "%TEMP%", StringComparison.OrdinalIgnoreCase);
        }

        return sanitizedValue;
    }

    private static string GetDefaultInstallDirectory()
    {
        string workspaceRoot = GetValowatchWorkspaceRoot();
        if (IsSourceRepositoryRoot(workspaceRoot))
        {
            // Keep the runnable installation inside the allowed workspace without placing it beside source files.
            return Path.Combine(workspaceRoot, "data", "installed", "VALOWATCH", "app");
        }

        return Path.Combine(workspaceRoot, "app");
    }

    private static bool IsSourceRepositoryRoot(string workspaceRoot)
    {
        return File.Exists(Path.Combine(workspaceRoot, "VALOWATCH.slnx")) ||
            Directory.Exists(Path.Combine(workspaceRoot, "src"));
    }

    private static string GetWorkspaceRootForInstallDirectory(string installDirectory)
    {
        return Directory.GetParent(Path.GetFullPath(installDirectory))?.FullName
            ?? throw new InvalidOperationException("インストール先の親フォルダーを取得できません。");
    }

    private static string GetValowatchWorkspaceRoot()
    {
        if (TryFindWorkspaceRoot(AppContext.BaseDirectory, out string workspaceRoot))
        {
            return workspaceRoot;
        }

        string documentsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return string.IsNullOrWhiteSpace(documentsDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "VALOWATCH")
            : Path.Combine(documentsDirectory, "VALOWATCH");
    }

    private static bool TryFindWorkspaceRoot(string startDirectory, out string workspaceRoot)
    {
        DirectoryInfo? currentDirectory = new(startDirectory);
        while (currentDirectory is not null)
        {
            string installerDirectory = Path.Combine(currentDirectory.FullName, "installer");
            string sourceDirectory = Path.Combine(currentDirectory.FullName, "src");
            if ((currentDirectory.Name.Equals("VALOWATCH", StringComparison.OrdinalIgnoreCase) && Directory.Exists(installerDirectory)) ||
                File.Exists(Path.Combine(currentDirectory.FullName, "VALOWATCH.slnx")) ||
                (Directory.Exists(installerDirectory) && Directory.Exists(sourceDirectory)))
            {
                workspaceRoot = currentDirectory.FullName;
                return true;
            }

            currentDirectory = currentDirectory.Parent;
        }

        workspaceRoot = string.Empty;
        return false;
    }

    private static string NormalizeInstallDirectoryPath(string rawPath)
    {
        string expandedPath = Environment.ExpandEnvironmentVariables(rawPath.Trim());
        string fullPath = Path.GetFullPath(expandedPath);
        string pathRoot = Path.GetPathRoot(fullPath) ?? string.Empty;

        if (pathRoot.Length > 0 && string.Equals(fullPath, pathRoot, StringComparison.OrdinalIgnoreCase))
        {
            return pathRoot;
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void ValidateInstallDirectorySelection(string installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            throw new InvalidOperationException("インストール先を指定してください。");
        }

        if (IsDriveRoot(installDirectory))
        {
            throw new InvalidOperationException(
                "C:\\ などのドライブ直下にはインストールできません。\n\n" +
                $"おすすめの場所:\n{GetDefaultInstallDirectory()}\n\n" +
                "別の例:\nD:\\VALOWATCH\\app");
        }

        if (IsProtectedInstallDirectory(installDirectory))
        {
            throw new InvalidOperationException(
                "Windows フォルダーや Program Files にはインストールできません。\n\n" +
                $"おすすめの場所:\n{GetDefaultInstallDirectory()}");
        }

        EnsureWritableInstallDirectory(installDirectory);
    }

    private static bool IsDriveRoot(string installDirectory)
    {
        string fullPath = Path.GetFullPath(installDirectory);
        string pathRoot = Path.GetPathRoot(fullPath) ?? string.Empty;
        return pathRoot.Length > 0 && string.Equals(fullPath, pathRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProtectedInstallDirectory(string installDirectory)
    {
        string[] protectedDirectories =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.System)
        ];

        return protectedDirectories
            .Where(protectedDirectory => !string.IsNullOrWhiteSpace(protectedDirectory))
            .Any(protectedDirectory => IsSameOrChildDirectory(installDirectory, protectedDirectory));
    }

    private static bool IsSameOrChildDirectory(string candidateDirectory, string parentDirectory)
    {
        string normalizedCandidate = EnsureTrailingDirectorySeparator(Path.GetFullPath(candidateDirectory));
        string normalizedParent = EnsureTrailingDirectorySeparator(Path.GetFullPath(parentDirectory));

        return normalizedCandidate.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingDirectorySeparator(string directoryPath)
    {
        if (directoryPath.EndsWith(Path.DirectorySeparatorChar) || directoryPath.EndsWith(Path.AltDirectorySeparatorChar))
        {
            return directoryPath;
        }

        return $"{directoryPath}{Path.DirectorySeparatorChar}";
    }

    private static void EnsureWritableInstallDirectory(string installDirectory)
    {
        string? testFilePath = null;
        try
        {
            Directory.CreateDirectory(installDirectory);
            testFilePath = Path.Combine(installDirectory, $".valowatch-write-test-{Guid.NewGuid():N}.tmp");
            using FileStream testFileStream = new(
                testFilePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
            testFileStream.WriteByte(0);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new UnauthorizedAccessException(
                "このインストール先には書き込めません。\n\n" +
                $"選択された場所:\n{installDirectory}\n\n" +
                $"おすすめの場所:\n{GetDefaultInstallDirectory()}",
                exception);
        }
        catch (IOException exception)
        {
            throw new IOException(
                "このインストール先を使用できません。\n\n" +
                $"選択された場所:\n{installDirectory}\n\n" +
                $"おすすめの場所:\n{GetDefaultInstallDirectory()}",
                exception);
        }
        finally
        {
            if (testFilePath is not null && File.Exists(testFilePath))
            {
                try
                {
                    File.Delete(testFilePath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // The temporary write test file uses DeleteOnClose, so this is only a cleanup fallback.
                }
            }
        }
    }

    private static void ExtractEmbeddedExecutable(
        string installedExecutablePath,
        IProgress<InstallProgress> progress,
        int startPercent,
        int endPercent)
    {
        using Stream? resourceStream = typeof(Program).Assembly.GetManifestResourceStream(EmbeddedExecutableResourceName);
        if (resourceStream is null)
        {
            throw new InvalidOperationException("Embedded VALOWATCH.exe was not found.");
        }

        using FileStream executableStream = new(
            installedExecutablePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);
        CopyStreamWithProgress(
            resourceStream,
            executableStream,
            progress,
            startPercent,
            endPercent,
            "VALOWATCH 本体を展開しています。");
    }

    private static void ExtractNativeDependencies(string installDirectory)
    {
        foreach ((string resourceName, string fileName) in NativeDependencyResources)
        {
            string targetFilePath = Path.Combine(installDirectory, fileName);
            ExtractEmbeddedFile(resourceName, targetFilePath);
        }
    }

    private static void ExtractEmbeddedFile(string resourceName, string targetFilePath)
    {
        using Stream? resourceStream = typeof(Program).Assembly.GetManifestResourceStream(resourceName);
        if (resourceStream is null)
        {
            throw new InvalidOperationException($"Embedded file was not found: {resourceName}");
        }

        using FileStream targetStream = new(
            targetFilePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);
        resourceStream.CopyTo(targetStream);
    }

    private static void RemoveObsoleteCaptureTools(string installDirectory)
    {
        string normalizedInstallDirectory = Path.GetFullPath(installDirectory);
        string ffmpegDirectory = Path.GetFullPath(Path.Combine(normalizedInstallDirectory, "ffmpeg"));
        if (string.Equals(normalizedInstallDirectory, ffmpegDirectory, StringComparison.OrdinalIgnoreCase) ||
            !IsSameOrChildDirectory(ffmpegDirectory, normalizedInstallDirectory))
        {
            throw new InvalidOperationException("Obsolete FFmpeg cleanup path escaped the install directory.");
        }

        if (Directory.Exists(ffmpegDirectory))
        {
            try
            {
                Directory.Delete(ffmpegDirectory, recursive: true);
                WriteInstallerLog($"Removed obsolete capture tools: {ffmpegDirectory}");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Old locked files are unused by this build and must not block installation.
                WriteInstallerLog($"Could not remove obsolete capture tools: {ffmpegDirectory}. {exception}");
            }
        }
    }

    private static void EnsureEnvFiles(string installDirectory)
    {
        string workspaceRoot = GetWorkspaceRootForInstallDirectory(installDirectory);
        string configDirectory = Path.Combine(workspaceRoot, "config");
        string installerEnvDirectory = Path.Combine(workspaceRoot, "installer");

        Directory.CreateDirectory(configDirectory);
        Directory.CreateDirectory(installerEnvDirectory);

        string envPath = Path.Combine(installerEnvDirectory, ".env");
        string envExamplePath = Path.Combine(installerEnvDirectory, ".env.example");
        string installerSideEnvPath = Path.Combine(AppContext.BaseDirectory, ".env");
        string? installerSideEnvText = TryReadTextFile(installerSideEnvPath);
        string? embeddedEnvText = ReadEmbeddedEnvText();
        string? preferredEnvText = !string.IsNullOrWhiteSpace(installerSideEnvText)
            ? installerSideEnvText
            : embeddedEnvText;

        string[] defaultEnvLines =
        [
            "DISCORD_BOT_ENABLED=false",
            "DISCORD_BOT_TOKEN=PASTE_BOT_TOKEN_HERE",
            "DISCORD_GUILD_ID=0",
            "DISCORD_VOICE_CHANNEL_ID=0",
            "DISCORD_TEXT_CHANNEL_ID=0",
            "DISCORD_STREAM_MIC_AUDIO=true",
            "DISCORD_MIC_DEVICE_NAME=",
            "DISCORD_MIC_VOLUME=0.85",
            "DISCORD_MIC_NOISE_GATE=0",
            "DISCORD_STREAM_LINE_AUDIO=true",
            "DISCORD_LINE_PROCESS_NAMES=LINE,Line,line",
            "DISCORD_LINE_AUDIO_VOLUME=0.65",
            "VALOWATCH_UPDATE_CHECK_ENABLED=true",
            "VALOWATCH_UPDATE_REPOSITORY=yss19411208/YAMAWATCH",
            "VALOWATCH_UPDATE_CURRENT_VERSION=0.1.2",
            "VALOWATCH_UPDATE_BRANCH=main",
            "VALOWATCH_UPDATE_CURRENT_COMMIT=",
            "VALOWATCH_GITHUB_TOKEN=",
            "VALOWATCH_AUDIO_RELAY_ONLY=true"
        ];

        if (!File.Exists(envExamplePath))
        {
            File.WriteAllLines(envExamplePath, defaultEnvLines);
        }

        if (File.Exists(envPath))
        {
            if (!string.IsNullOrWhiteSpace(preferredEnvText))
            {
                MergeEnvFile(envPath, preferredEnvText);
            }
            else
            {
                EnsureEnvFileContainsMissingLines(envPath, defaultEnvLines);
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(preferredEnvText))
        {
            File.WriteAllText(envPath, preferredEnvText, Encoding.UTF8);
            return;
        }

        File.WriteAllLines(envPath, defaultEnvLines, Encoding.UTF8);
    }

    private static void MergeEnvFile(string targetEnvPath, string sourceEnvText)
    {
        (List<string> sourceKeys, Dictionary<string, string> sourceValues) = ReadEnvAssignments(sourceEnvText);
        if (sourceKeys.Count == 0)
        {
            return;
        }

        string[] existingLines = File.ReadAllLines(targetEnvPath, Encoding.UTF8);
        List<string> mergedLines = new(existingLines.Length + sourceKeys.Count + 1);
        HashSet<string> updatedKeys = new(StringComparer.OrdinalIgnoreCase);

        foreach (string existingLine in existingLines)
        {
            if (TryParseEnvAssignment(existingLine, out string existingKey, out string existingValue) &&
                sourceValues.TryGetValue(existingKey, out string? sourceValue))
            {
                string mergedValue = ShouldKeepExistingEnvValue(existingValue, sourceValue)
                    ? existingValue
                    : sourceValue ?? string.Empty;
                mergedLines.Add($"{existingKey}={mergedValue}");
                updatedKeys.Add(existingKey);
                continue;
            }

            mergedLines.Add(existingLine);
        }

        foreach (string sourceKey in sourceKeys)
        {
            if (updatedKeys.Contains(sourceKey))
            {
                continue;
            }

            mergedLines.Add($"{sourceKey}={sourceValues[sourceKey]}");
        }

        File.WriteAllLines(targetEnvPath, mergedLines, Encoding.UTF8);
    }

    private static bool ShouldKeepExistingEnvValue(string existingValue, string? sourceValue)
    {
        return string.IsNullOrWhiteSpace(sourceValue) &&
            !string.IsNullOrWhiteSpace(existingValue);
    }

    private static void EnsureEnvFileContainsMissingLines(string targetEnvPath, IEnumerable<string> defaultEnvLines)
    {
        string existingEnvText = File.ReadAllText(targetEnvPath, Encoding.UTF8);
        (_, Dictionary<string, string> existingValues) = ReadEnvAssignments(existingEnvText);

        List<string> missingLines = [];
        foreach (string defaultEnvLine in defaultEnvLines)
        {
            if (!TryParseEnvAssignment(defaultEnvLine, out string defaultKey, out _))
            {
                continue;
            }

            if (!existingValues.ContainsKey(defaultKey))
            {
                missingLines.Add(defaultEnvLine);
            }
        }

        if (missingLines.Count == 0)
        {
            return;
        }

        using StreamWriter writer = File.AppendText(targetEnvPath);
        writer.WriteLine();
        foreach (string missingLine in missingLines)
        {
            writer.WriteLine(missingLine);
        }
    }

    private static (List<string> Keys, Dictionary<string, string> Values) ReadEnvAssignments(string envText)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        List<string> keys = [];

        foreach (string rawLine in envText.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            if (!TryParseEnvAssignment(rawLine, out string key, out string value))
            {
                continue;
            }

            if (!values.ContainsKey(key))
            {
                keys.Add(key);
            }

            values[key] = value;
        }

        return (keys, values);
    }

    private static bool TryParseEnvAssignment(string rawLine, out string key, out string value)
    {
        string line = rawLine.Trim();
        if (line.Length == 0 || line.StartsWith('#'))
        {
            key = string.Empty;
            value = string.Empty;
            return false;
        }

        int separatorIndex = line.IndexOf('=');
        if (separatorIndex <= 0)
        {
            key = string.Empty;
            value = string.Empty;
            return false;
        }

        key = line[..separatorIndex].Trim();
        value = line[(separatorIndex + 1)..].Trim();
        return key.Length > 0;
    }

    private static string? TryReadTextFile(string filePath)
    {
        try
        {
            return File.Exists(filePath)
                ? File.ReadAllText(filePath, Encoding.UTF8)
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadEmbeddedEnvText()
    {
        using Stream? resourceStream = typeof(Program).Assembly.GetManifestResourceStream(EmbeddedEnvResourceName);
        if (resourceStream is null)
        {
            return null;
        }

        using StreamReader reader = new(resourceStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static void StopRunningInstalledApp(string installedExecutablePath, bool stopAllValowatchProcesses)
    {
        string normalizedInstalledExecutablePath = NormalizeExecutablePath(installedExecutablePath);
        foreach (Process candidateProcess in Process.GetProcessesByName("VALOWATCH"))
        {
            using (candidateProcess)
            {
                if (candidateProcess.Id == Environment.ProcessId)
                {
                    continue;
                }

                if (!stopAllValowatchProcesses &&
                    !IsInstalledAppProcess(candidateProcess, normalizedInstalledExecutablePath))
                {
                    continue;
                }

                if (!candidateProcess.CloseMainWindow())
                {
                    candidateProcess.Kill(entireProcessTree: true);
                    candidateProcess.WaitForExit(5000);
                    continue;
                }

                if (!candidateProcess.WaitForExit(5000))
                {
                    candidateProcess.Kill(entireProcessTree: true);
                    candidateProcess.WaitForExit(5000);
                }
            }
        }
    }

    private static bool IsInstalledAppProcess(Process candidateProcess, string normalizedInstalledExecutablePath)
    {
        try
        {
            string? processFileName = candidateProcess.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(processFileName))
            {
                return false;
            }

            return string.Equals(
                NormalizeExecutablePath(processFileName),
                normalizedInstalledExecutablePath,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string NormalizeExecutablePath(string executablePath)
    {
        return Path.GetFullPath(executablePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void StopRunningUpdateProcesses()
    {
        Process[] processes = Process.GetProcesses();
        try
        {
            foreach (Process process in processes)
            {
                if (process.Id == Environment.ProcessId)
                {
                    continue;
                }

                string processName;
                try
                {
                    processName = process.ProcessName;
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    continue;
                }

                bool isUpdateProcess = processName.Equals("VALOWATCH_Update", StringComparison.OrdinalIgnoreCase) ||
                    processName.Equals("GITHUB", StringComparison.OrdinalIgnoreCase) ||
                    processName.StartsWith("VALOWATCH_Setup_", StringComparison.OrdinalIgnoreCase);
                if (!isUpdateProcess)
                {
                    continue;
                }

                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    WriteInstallerLog($"Could not stop update process {process.Id}.", exception);
                }
            }
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static void RemoveStartupRegistration()
    {
        try
        {
            using RegistryKey? registryKey = Registry.CurrentUser.OpenSubKey(RegistryRunPath, writable: true);
            registryKey?.DeleteValue(RegistryValueName, throwOnMissingValue: false);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            WriteInstallerLog("Could not remove VALOWATCH startup registry value.", exception);
        }

        try
        {
            string startupDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            if (!string.IsNullOrWhiteSpace(startupDirectory))
            {
                string startupCommandPath = Path.Combine(startupDirectory, StartupCommandFileName);
                if (File.Exists(startupCommandPath))
                {
                    File.Delete(startupCommandPath);
                }
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            WriteInstallerLog("Could not remove VALOWATCH Startup command.", exception);
        }

        TryDeleteScheduledTask(KeepAliveScheduledTaskName);
        TryDeleteScheduledTask(LogonScheduledTaskName);
        TryDeleteScheduledTask(StartAgentKeepAliveScheduledTaskName);
        TryDeleteScheduledTask(StartAgentLogonScheduledTaskName);
    }

    private static void TryDeleteScheduledTask(string taskName)
    {
        try
        {
            string taskSchedulerPath = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
            ProcessStartInfo processStartInfo = new()
            {
                FileName = taskSchedulerPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            processStartInfo.ArgumentList.Add("/Delete");
            processStartInfo.ArgumentList.Add("/TN");
            processStartInfo.ArgumentList.Add(taskName);
            processStartInfo.ArgumentList.Add("/F");

            using Process taskSchedulerProcess = Process.Start(processStartInfo)
                ?? throw new InvalidOperationException("Windows Task Scheduler cleanup could not be started.");
            Task<string> outputTask = taskSchedulerProcess.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = taskSchedulerProcess.StandardError.ReadToEndAsync();
            if (!taskSchedulerProcess.WaitForExit(15000))
            {
                taskSchedulerProcess.Kill(entireProcessTree: true);
                throw new TimeoutException("Windows Task Scheduler cleanup timed out.");
            }

            Task.WaitAll([outputTask, errorTask], 2000);
            WriteInstallerLog(
                $"Scheduled task cleanup finished. Task: {taskName}. ExitCode: {taskSchedulerProcess.ExitCode}. " +
                $"Output: {outputTask.Result.Trim()} Error: {errorTask.Result.Trim()}");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            WriteInstallerLog($"Scheduled task cleanup failed or task did not exist. Task: {taskName}.", exception);
        }
    }

    private static void CleanInstalledAppDirectory(string installDirectory)
    {
        string normalizedInstallDirectory = ValidateCleanReinstallDirectory(installDirectory);
        if (Directory.Exists(normalizedInstallDirectory))
        {
            Directory.Delete(normalizedInstallDirectory, recursive: true);
        }

        Directory.CreateDirectory(normalizedInstallDirectory);
        WriteInstallerLog($"Clean reinstall removed old app directory: {normalizedInstallDirectory}");
    }

    private static string ResolveCleanReinstallInstallDirectory(string requestedInstallDirectory)
    {
        string requestedDirectory = requestedInstallDirectory.Trim();
        string standardInstallDirectory = ResolveStandardCleanReinstallDirectory();
        if (TryValidateCleanReinstallDirectory(
            requestedDirectory,
            out string validatedRequestedDirectory,
            out string requestedRejectReason))
        {
            if (string.Equals(validatedRequestedDirectory, standardInstallDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return validatedRequestedDirectory;
            }

            WriteInstallerLog(
                "Clean reinstall install directory differed from the standard folder and was redirected. " +
                $"Requested: {SanitizeInstallerReportText(validatedRequestedDirectory)}. " +
                $"Standard: {SanitizeInstallerReportText(standardInstallDirectory)}");
            return standardInstallDirectory;
        }

        WriteInstallerLog(
            "Clean reinstall install directory was redirected to the standard folder. " +
            $"Requested: {SanitizeInstallerReportText(requestedDirectory)}. " +
            $"Reason: {requestedRejectReason}. " +
            $"Standard: {SanitizeInstallerReportText(standardInstallDirectory)}");
        return standardInstallDirectory;
    }

    private static string ResolveCleanReinstallReportDirectory(string requestedInstallDirectory)
    {
        string requestedDirectory = requestedInstallDirectory.Trim();
        string standardInstallDirectory = ResolveStandardCleanReinstallDirectory();
        if (TryValidateCleanReinstallDirectory(
            requestedDirectory,
            out string validatedRequestedDirectory,
            out _))
        {
            return string.Equals(validatedRequestedDirectory, standardInstallDirectory, StringComparison.OrdinalIgnoreCase)
                ? validatedRequestedDirectory
                : standardInstallDirectory;
        }

        return standardInstallDirectory;
    }

    private static string ResolveStandardCleanReinstallDirectory()
    {
        string standardInstallDirectory = GetDefaultInstallDirectory();
        if (TryValidateCleanReinstallDirectory(
            standardInstallDirectory,
            out string validatedStandardDirectory,
            out string standardRejectReason))
        {
            return validatedStandardDirectory;
        }

        throw new InvalidOperationException(
            $"標準の再インストール先を使用できません: {standardInstallDirectory}. {standardRejectReason}");
    }

    private static string ValidateCleanReinstallDirectory(string installDirectory)
    {
        if (TryValidateCleanReinstallDirectory(
            installDirectory,
            out string normalizedInstallDirectory,
            out string rejectReason))
        {
            return normalizedInstallDirectory;
        }

        throw new InvalidOperationException(
            $"安全のため、このフォルダーはクリーン再インストールできません: {installDirectory}. {rejectReason}");
    }

    private static bool TryValidateCleanReinstallDirectory(
        string installDirectory,
        out string normalizedInstallDirectory,
        out string rejectReason)
    {
        normalizedInstallDirectory = string.Empty;
        rejectReason = string.Empty;
        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            rejectReason = "install directory is empty";
            return false;
        }

        try
        {
            normalizedInstallDirectory = Path.GetFullPath(installDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            rejectReason = $"install directory path is invalid: {exception.GetType().Name}";
            return false;
        }

        DirectoryInfo installDirectoryInfo = new(normalizedInstallDirectory);
        DirectoryInfo? workspaceDirectoryInfo = installDirectoryInfo.Parent;
        bool pathLooksLikeInstalledApp = installDirectoryInfo.Name.Equals("app", StringComparison.OrdinalIgnoreCase) &&
            workspaceDirectoryInfo?.Name.Equals("VALOWATCH", StringComparison.OrdinalIgnoreCase) == true;
        bool sourceRepositoryDetected = workspaceDirectoryInfo is not null &&
            (File.Exists(Path.Combine(workspaceDirectoryInfo.FullName, "VALOWATCH.slnx")) ||
                Directory.Exists(Path.Combine(workspaceDirectoryInfo.FullName, "src")));
        if (!pathLooksLikeInstalledApp)
        {
            rejectReason = "install directory is not a VALOWATCH app folder";
            return false;
        }

        if (sourceRepositoryDetected)
        {
            rejectReason = "install directory points at the source repository";
            return false;
        }

        return true;
    }

    private static void RegisterStartup(
        string installedGitHubPath,
        string installedStartAgentPath,
        string installDirectory)
    {
        using RegistryKey registryKey = Registry.CurrentUser.CreateSubKey(RegistryRunPath, true)
            ?? throw new InvalidOperationException("Windows startup registry key could not be opened.");

        string agentCommand = BuildGitHubAgentCommand(installedGitHubPath, installDirectory);
        registryKey.SetValue(RegistryValueName, agentCommand);
        WriteStartupCommand(installedGitHubPath, installedStartAgentPath, installDirectory);
        TryRegisterKeepAliveTask(installedGitHubPath, installDirectory);
        TryRegisterStartAgentKeepAliveTask(installedStartAgentPath, installDirectory);
    }

    private static void TryRegisterKeepAliveTask(string installedGitHubPath, string installDirectory)
    {
        try
        {
            string taskSchedulerPath = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
            ProcessStartInfo processStartInfo = new()
            {
                FileName = taskSchedulerPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            processStartInfo.ArgumentList.Add("/Create");
            processStartInfo.ArgumentList.Add("/TN");
            processStartInfo.ArgumentList.Add(KeepAliveScheduledTaskName);
            processStartInfo.ArgumentList.Add("/TR");
            processStartInfo.ArgumentList.Add(BuildGitHubAgentCommand(installedGitHubPath, installDirectory));
            processStartInfo.ArgumentList.Add("/SC");
            processStartInfo.ArgumentList.Add("MINUTE");
            processStartInfo.ArgumentList.Add("/MO");
            processStartInfo.ArgumentList.Add(KeepAliveIntervalMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture));
            processStartInfo.ArgumentList.Add("/RL");
            processStartInfo.ArgumentList.Add("LIMITED");
            processStartInfo.ArgumentList.Add("/F");

            using Process taskSchedulerProcess = Process.Start(processStartInfo)
                ?? throw new InvalidOperationException("Windows Task Scheduler could not be started.");
            Task<string> outputTask = taskSchedulerProcess.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = taskSchedulerProcess.StandardError.ReadToEndAsync();
            if (!taskSchedulerProcess.WaitForExit(15000))
            {
                taskSchedulerProcess.Kill(entireProcessTree: true);
                throw new TimeoutException("Windows Task Scheduler registration timed out.");
            }

            string output = outputTask.GetAwaiter().GetResult().Trim();
            string error = errorTask.GetAwaiter().GetResult().Trim();
            if (taskSchedulerProcess.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Windows Task Scheduler registration failed with exit code {taskSchedulerProcess.ExitCode}. {error}");
            }

            WriteInstallerLog(
                $"Keepalive task registered. Name: {KeepAliveScheduledTaskName}. " +
                $"IntervalMinutes: {KeepAliveIntervalMinutes}. Output: {output}");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            WriteInstallerLog(
                "Keepalive task registration failed. Registry and Startup-folder launch remain enabled.",
                exception);
        }
    }

    private static void TryRegisterLogonTask(string installedGitHubPath, string installDirectory)
    {
        try
        {
            string taskSchedulerPath = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
            ProcessStartInfo processStartInfo = new()
            {
                FileName = taskSchedulerPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            processStartInfo.ArgumentList.Add("/Create");
            processStartInfo.ArgumentList.Add("/TN");
            processStartInfo.ArgumentList.Add(LogonScheduledTaskName);
            processStartInfo.ArgumentList.Add("/TR");
            processStartInfo.ArgumentList.Add(BuildGitHubAgentCommand(installedGitHubPath, installDirectory));
            processStartInfo.ArgumentList.Add("/SC");
            processStartInfo.ArgumentList.Add("ONLOGON");
            processStartInfo.ArgumentList.Add("/DELAY");
            processStartInfo.ArgumentList.Add("0000:30");
            processStartInfo.ArgumentList.Add("/RL");
            processStartInfo.ArgumentList.Add("LIMITED");
            processStartInfo.ArgumentList.Add("/F");

            using Process taskSchedulerProcess = Process.Start(processStartInfo)
                ?? throw new InvalidOperationException("Windows Task Scheduler could not be started.");
            Task<string> outputTask = taskSchedulerProcess.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = taskSchedulerProcess.StandardError.ReadToEndAsync();
            if (!taskSchedulerProcess.WaitForExit(15000))
            {
                taskSchedulerProcess.Kill(entireProcessTree: true);
                throw new TimeoutException("Windows Task Scheduler registration timed out.");
            }

            string output = outputTask.GetAwaiter().GetResult().Trim();
            string error = errorTask.GetAwaiter().GetResult().Trim();
            if (taskSchedulerProcess.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Windows Task Scheduler registration failed with exit code {taskSchedulerProcess.ExitCode}. {error}");
            }

            WriteInstallerLog(
                $"Logon task registered. Name: {LogonScheduledTaskName}. Delay: 30 seconds. Output: {output}");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            WriteInstallerLog(
                "Logon task registration failed. Registry, Startup-folder, and KeepAlive launch remain enabled.",
                exception);
        }
    }

    private static void TryRegisterStartAgentKeepAliveTask(string installedStartAgentPath, string installDirectory)
    {
        TryRegisterScheduledTask(
            StartAgentKeepAliveScheduledTaskName,
            BuildStartAgentCommand(installedStartAgentPath, installDirectory),
            processStartInfo =>
            {
                processStartInfo.ArgumentList.Add("/SC");
                processStartInfo.ArgumentList.Add("MINUTE");
                processStartInfo.ArgumentList.Add("/MO");
                processStartInfo.ArgumentList.Add(KeepAliveIntervalMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture));
            },
            "Start agent keepalive task");
    }

    private static void TryRegisterStartAgentLogonTask(string installedStartAgentPath, string installDirectory)
    {
        TryRegisterScheduledTask(
            StartAgentLogonScheduledTaskName,
            BuildStartAgentCommand(installedStartAgentPath, installDirectory),
            processStartInfo =>
            {
                processStartInfo.ArgumentList.Add("/SC");
                processStartInfo.ArgumentList.Add("ONLOGON");
                processStartInfo.ArgumentList.Add("/DELAY");
                processStartInfo.ArgumentList.Add("0000:20");
            },
            "Start agent logon task");
    }

    private static void TryRegisterScheduledTask(
        string taskName,
        string taskCommand,
        Action<ProcessStartInfo> addScheduleArguments,
        string label)
    {
        try
        {
            string taskSchedulerPath = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
            ProcessStartInfo processStartInfo = new()
            {
                FileName = taskSchedulerPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            processStartInfo.ArgumentList.Add("/Create");
            processStartInfo.ArgumentList.Add("/TN");
            processStartInfo.ArgumentList.Add(taskName);
            processStartInfo.ArgumentList.Add("/TR");
            processStartInfo.ArgumentList.Add(taskCommand);
            addScheduleArguments(processStartInfo);
            processStartInfo.ArgumentList.Add("/RL");
            processStartInfo.ArgumentList.Add("LIMITED");
            processStartInfo.ArgumentList.Add("/F");

            using Process taskSchedulerProcess = Process.Start(processStartInfo)
                ?? throw new InvalidOperationException("Windows Task Scheduler could not be started.");
            Task<string> outputTask = taskSchedulerProcess.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = taskSchedulerProcess.StandardError.ReadToEndAsync();
            if (!taskSchedulerProcess.WaitForExit(15000))
            {
                taskSchedulerProcess.Kill(entireProcessTree: true);
                throw new TimeoutException("Windows Task Scheduler registration timed out.");
            }

            string output = outputTask.GetAwaiter().GetResult().Trim();
            string error = errorTask.GetAwaiter().GetResult().Trim();
            if (taskSchedulerProcess.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Windows Task Scheduler registration failed with exit code {taskSchedulerProcess.ExitCode}. {error}");
            }

            WriteInstallerLog($"{label} registered. Name: {taskName}. Output: {output}");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            WriteInstallerLog($"{label} registration failed.", exception);
        }
    }

    private static void WriteStartupCommand(
        string installedGitHubPath,
        string installedStartAgentPath,
        string installDirectory)
    {
        string startupDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        if (string.IsNullOrWhiteSpace(startupDirectory))
        {
            return;
        }

        Directory.CreateDirectory(startupDirectory);
        string commandPath = Path.Combine(startupDirectory, StartupCommandFileName);
        string[] commandLines =
        [
            "@echo off",
            $"start \"\" \"{installedGitHubPath}\" --watch --install-dir \"{installDirectory}\"",
            $"start \"\" \"{installedStartAgentPath}\" --install-dir \"{installDirectory}\""
        ];
        File.WriteAllLines(commandPath, commandLines);
    }

    private static string BuildGitHubAgentCommand(string installedGitHubPath, string installDirectory)
    {
        return $"\"{installedGitHubPath}\" --watch --install-dir \"{installDirectory}\"";
    }

    private static string BuildStartAgentCommand(string installedStartAgentPath, string installDirectory)
    {
        return $"\"{installedStartAgentPath}\" --install-dir \"{installDirectory}\"";
    }

    private static void StartGitHubAgent(string installedGitHubPath, string installDirectory)
    {
        ProcessStartInfo processStartInfo = new()
        {
            FileName = installedGitHubPath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installedGitHubPath),
            WindowStyle = ProcessWindowStyle.Hidden
        };
        processStartInfo.ArgumentList.Add("--watch");
        processStartInfo.ArgumentList.Add("--install-dir");
        processStartInfo.ArgumentList.Add(installDirectory);

        Process.Start(processStartInfo);
    }

    private static void StartStartAgent(string installedStartAgentPath, string installDirectory)
    {
        ProcessStartInfo processStartInfo = new()
        {
            FileName = installedStartAgentPath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installedStartAgentPath),
            WindowStyle = ProcessWindowStyle.Hidden
        };
        processStartInfo.ArgumentList.Add("--install-dir");
        processStartInfo.ArgumentList.Add(installDirectory);

        Process.Start(processStartInfo);
    }

    private static void CopyStreamWithProgress(
        Stream sourceStream,
        Stream targetStream,
        IProgress<InstallProgress> progress,
        int startPercent,
        int endPercent,
        string message,
        long? knownTotalBytes = null)
    {
        byte[] buffer = new byte[1024 * 1024];
        long totalBytes = knownTotalBytes ?? TryGetStreamLength(sourceStream);
        long copiedBytes = 0;
        int lastReportedPercent = startPercent;

        int readByteCount;
        while ((readByteCount = sourceStream.Read(buffer, 0, buffer.Length)) > 0)
        {
            targetStream.Write(buffer, 0, readByteCount);
            copiedBytes += readByteCount;

            if (totalBytes <= 0)
            {
                continue;
            }

            int percent = startPercent + (int)((endPercent - startPercent) * copiedBytes / totalBytes);
            percent = Math.Clamp(percent, startPercent, endPercent);
            if (percent == lastReportedPercent)
            {
                continue;
            }

            lastReportedPercent = percent;
            progress.Report(new InstallProgress(percent, $"{message} {FormatByteCount(copiedBytes)} / {FormatByteCount(totalBytes)}"));
        }

        progress.Report(new InstallProgress(endPercent, message));
    }

    private static long TryGetStreamLength(Stream stream)
    {
        try
        {
            return stream.CanSeek ? stream.Length : 0;
        }
        catch (NotSupportedException)
        {
            return 0;
        }
    }

    private static string FormatByteCount(long byteCount)
    {
        const long kibibyte = 1024;
        const long mebibyte = kibibyte * 1024;

        if (byteCount >= mebibyte)
        {
            return $"{byteCount / (double)mebibyte:0.0} MB";
        }

        if (byteCount >= kibibyte)
        {
            return $"{byteCount / (double)kibibyte:0.0} KB";
        }

        return $"{byteCount} B";
    }

    private sealed class InlineProgress : IProgress<InstallProgress>
    {
        private readonly Action<InstallProgress> reportAction;

        public InlineProgress(Action<InstallProgress> reportAction)
        {
            this.reportAction = reportAction;
        }

        public void Report(InstallProgress value)
        {
            reportAction(value);
        }
    }
}
