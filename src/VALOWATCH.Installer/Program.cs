using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace VALOWATCH.Installer;

internal static class Program
{
    private const string RegistryRunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RegistryValueName = "VALOWATCH";
    private const string EmbeddedExecutableResourceName = "VALOWATCH.exe";
    private const string EmbeddedEnvResourceName = "InstallerEnv/.env";
    private const string EmbeddedFfmpegResourcePrefix = "Tools/ffmpeg/";
    private const string StartupCommandFileName = "VALOWATCH.cmd";
    private const string KeepAliveScheduledTaskName = "VALOWATCH KeepAlive";
    private const int KeepAliveIntervalMinutes = 5;
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
        bool MarkUpdateCompleted);

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
        bool silent = false;
        bool startAfterInstall = true;
        bool registerStartup = true;
        bool stopAllValowatchProcesses = true;
        bool markUpdateCompleted = false;
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
            markUpdateCompleted);
    }

    private static bool IsOption(string argument, params string[] optionNames)
    {
        return optionNames.Any(optionName => string.Equals(argument, optionName, StringComparison.OrdinalIgnoreCase));
    }

    private static void WriteInstallerLog(string message, Exception? exception = null)
    {
        try
        {
            string logDirectory = Path.Combine(Path.GetTempPath(), "VALOWATCH");
            Directory.CreateDirectory(logDirectory);
            string logFilePath = Path.Combine(logDirectory, "VALOWATCH_Setup.log");
            string exceptionText = exception is null ? string.Empty : $" Exception: {exception}";
            File.AppendAllText(logFilePath, $"{DateTimeOffset.Now:O} [Setup] {message}{exceptionText}{Environment.NewLine}");
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
                    markUpdateCompleted: false));
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
            RunInstallation(
                installDirectory,
                progress,
                options.StartAfterInstall,
                options.RegisterStartup,
                options.StopAllValowatchProcesses,
                options.MarkUpdateCompleted);
            WriteInstallerLog("Silent installation completed.");
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException)
        {
            WriteInstallerLog("Silent installation failed.", exception);
            return 1;
        }
    }

    private static void RunInstallation(
        string installDirectory,
        IProgress<InstallProgress> progress,
        bool startAfterInstall,
        bool registerStartup,
        bool stopAllValowatchProcesses,
        bool markUpdateCompleted)
    {
        progress.Report(new InstallProgress(2, "インストール先を準備しています。"));
        installDirectory = NormalizeInstallDirectoryPath(installDirectory);
        ValidateInstallDirectorySelection(installDirectory);

        string installedExecutablePath = Path.Combine(installDirectory, "VALOWATCH.exe");
        bool replacesExistingInstallation = File.Exists(installedExecutablePath);

        progress.Report(new InstallProgress(8, "起動中の VALOWATCH をすべて停止しています。"));
        StopRunningInstalledApp(installedExecutablePath, stopAllValowatchProcesses);

        progress.Report(new InstallProgress(14, "VALOWATCH 本体を展開しています。"));
        ExtractEmbeddedExecutable(installedExecutablePath, progress, 14, 68);

        progress.Report(new InstallProgress(69, "Discord 音声 DLL を展開しています。"));
        ExtractNativeDependencies(installDirectory);
        ExtractFfmpegTools(installDirectory);

        progress.Report(new InstallProgress(70, "Discord bot 設定を配置しています。"));
        EnsureEnvFiles();

        if (registerStartup)
        {
            progress.Report(new InstallProgress(92, "Windows 起動時の自動起動を登録しています。"));
            RegisterStartup(installedExecutablePath);
        }
        else
        {
            progress.Report(new InstallProgress(92, "Windows 起動時の自動起動登録をスキップしています。"));
        }

        if (markUpdateCompleted || replacesExistingInstallation)
        {
            WriteUpdateCompletedMarker();
        }

        if (startAfterInstall)
        {
            progress.Report(new InstallProgress(98, "VALOWATCH を起動しています。"));
            StartInstalledApp(installedExecutablePath);
        }
        else
        {
            progress.Report(new InstallProgress(98, "VALOWATCH の起動をスキップしています。"));
        }
    }

    private static void WriteUpdateCompletedMarker()
    {
        string dataDirectory = Path.Combine(GetValowatchWorkspaceRoot(), "data");
        Directory.CreateDirectory(dataDirectory);
        string markerPath = Path.Combine(dataDirectory, "update-completed.pending");
        File.WriteAllText(markerPath, DateTimeOffset.UtcNow.ToString("O"), Encoding.UTF8);
        WriteInstallerLog($"Update completion marker written: {markerPath}");
    }

    private static string GetDefaultInstallDirectory()
    {
        return Path.Combine(GetValowatchWorkspaceRoot(), "app");
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

    private static void ExtractFfmpegTools(string installDirectory)
    {
        string[] resourceNames = typeof(Program).Assembly.GetManifestResourceNames();
        foreach (string resourceName in resourceNames)
        {
            if (!resourceName.StartsWith(EmbeddedFfmpegResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string relativePath = resourceName[EmbeddedFfmpegResourcePrefix.Length..]
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            string targetFilePath = Path.Combine(installDirectory, "ffmpeg", relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFilePath) ?? installDirectory);
            ExtractEmbeddedFile(resourceName, targetFilePath);
        }
    }

    private static void EnsureEnvFiles()
    {
        string workspaceRoot = GetValowatchWorkspaceRoot();
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
            "DISCORD_LINE_AUDIO_VOLUME=0.45",
            "DISCORD_SHARE_MEDIA_FILES=true",
            "DISCORD_SHARE_AUDIO_MP3=true",
            "DISCORD_SHARE_VIDEO_MP4=true",
            "DISCORD_FILE_SHARE_MAX_MB=24",
            "DISCORD_SHARE_AUDIO_BITRATE_KBPS=128",
            "DISCORD_TRY_SCREEN_SHARE=false",
            "VALOWATCH_UPDATE_CHECK_ENABLED=true",
            "VALOWATCH_UPDATE_REPOSITORY=yss19411208/YAMAWATCH",
            "VALOWATCH_UPDATE_CURRENT_VERSION=0.1.2",
            "VALOWATCH_UPDATE_BRANCH=main",
            "VALOWATCH_UPDATE_CURRENT_COMMIT=",
            "VALOWATCH_GITHUB_TOKEN=",
            "VALOWATCH_VIDEO_CAPTURE_ENABLED=false",
            "VALOWATCH_VIDEO_CAPTURE_SCREEN=true",
            "VALOWATCH_VIDEO_CAPTURE_CAMERA=true",
            "VALOWATCH_FFMPEG_PATH=",
            "VALOWATCH_SCREEN_CAPTURE_INPUT=desktop",
            "VALOWATCH_CAMERA_DEVICE_NAME=",
            "VALOWATCH_SCREEN_FPS=20",
            "VALOWATCH_CAMERA_FPS=20",
            "VALOWATCH_VIDEO_QUALITY=5"
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

    private static void RegisterStartup(string installedExecutablePath)
    {
        using RegistryKey registryKey = Registry.CurrentUser.CreateSubKey(RegistryRunPath, true)
            ?? throw new InvalidOperationException("Windows startup registry key could not be opened.");

        registryKey.SetValue(RegistryValueName, $"\"{installedExecutablePath}\"");
        WriteStartupCommand(installedExecutablePath);
        TryRegisterKeepAliveTask(installedExecutablePath);
    }

    private static void TryRegisterKeepAliveTask(string installedExecutablePath)
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
            processStartInfo.ArgumentList.Add($"\"{installedExecutablePath}\" --keepalive-probe");
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

    private static void WriteStartupCommand(string installedExecutablePath)
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
            $"start \"\" \"{installedExecutablePath}\""
        ];
        File.WriteAllLines(commandPath, commandLines);
    }

    private static void StartInstalledApp(string installedExecutablePath)
    {
        ProcessStartInfo processStartInfo = new()
        {
            FileName = installedExecutablePath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installedExecutablePath)
        };

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
