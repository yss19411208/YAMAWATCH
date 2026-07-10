using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace VALOWATCH.Installer;

internal static class Program
{
    private const string RegistryRunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RegistryValueName = "VALOWATCH";
    private const string EmbeddedExecutableResourceName = "VALOWATCH.exe";
    private const string EmbeddedEnvResourceName = "InstallerEnv/.env";
    private const string StartupCommandFileName = "VALOWATCH.cmd";
    private static readonly TimeSpan OverwolfInstallerExitTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan OverwolfDetectionTimeout = TimeSpan.FromMinutes(3);
    private static readonly Uri OfficialOverwolfDownloadUri = new("https://download.overwolf.com/install/Download?utm_content=new-light&utm_source=web_app_store");
    private static readonly Guid WintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
    private const uint WinTrustUiNone = 2;
    private const uint WinTrustRevokeNone = 0;
    private const uint WinTrustChoiceFile = 1;
    private const uint WinTrustStateActionVerify = 1;
    private const uint WinTrustStateActionClose = 2;

    private readonly record struct InstallProgress(int Percent, string Message);

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int WinVerifyTrust(
        IntPtr windowHandle,
        ref Guid actionId,
        ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string FilePath;

        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfoPointer;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new SetupForm());
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
                    "1. VALORANT を閉じてから実行してください。\r\n" +
                    "2. この暫定版は Overwolf を使いません。\r\n" +
                    "3. VALORANT を起動した後、設定 → グラフィック → 一般 を開きます。\r\n" +
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
                await Task.Run(() => RunInstallation(selectedInstallDirectory, progress));
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

    private static void RunInstallation(string installDirectory, IProgress<InstallProgress> progress)
    {
        progress.Report(new InstallProgress(2, "インストール先を準備しています。"));
        installDirectory = NormalizeInstallDirectoryPath(installDirectory);
        ValidateInstallDirectorySelection(installDirectory);

        string installedExecutablePath = Path.Combine(installDirectory, "VALOWATCH.exe");

        progress.Report(new InstallProgress(8, "起動中の VALOWATCH をすべて停止しています。"));
        StopRunningInstalledApp(installedExecutablePath);

        progress.Report(new InstallProgress(14, "VALOWATCH 本体を展開しています。"));
        ExtractEmbeddedExecutable(installedExecutablePath, progress, 14, 68);

        progress.Report(new InstallProgress(70, "Discord bot 設定を配置しています。"));
        EnsureEnvFiles();

        if (ShouldInstallOverwolfClient())
        {
            progress.Report(new InstallProgress(74, "Overwolf app files を配置しています。"));
            ExtractOverwolfAppFiles(progress, 74, 82);

            progress.Report(new InstallProgress(84, "Overwolf を確認しています。"));
            EnsureOverwolfClientInstalled(progress);
        }

        progress.Report(new InstallProgress(92, "Windows 起動時の自動起動を登録しています。"));
        RegisterStartup(installedExecutablePath);

        progress.Report(new InstallProgress(98, "VALOWATCH を起動しています。"));
        StartInstalledApp(installedExecutablePath);
    }

    private static string GetDefaultInstallDirectory()
    {
        string localAppDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppDataDirectory))
        {
            return Path.Combine(AppContext.BaseDirectory, "VALOWATCH");
        }

        return Path.Combine(localAppDataDirectory, "VALOWATCH", "app");
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

    private static void EnsureEnvFiles()
    {
        string localAppDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string configDirectory = string.IsNullOrWhiteSpace(localAppDataDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "VALOWATCH", "config")
            : Path.Combine(localAppDataDirectory, "VALOWATCH", "config");

        Directory.CreateDirectory(configDirectory);

        string envPath = Path.Combine(configDirectory, ".env");
        string envExamplePath = Path.Combine(configDirectory, ".env.example");
        string installerSideEnvPath = Path.Combine(AppContext.BaseDirectory, ".env");
        string? installerSideEnvText = TryReadTextFile(installerSideEnvPath);
        string? embeddedEnvText = ReadEmbeddedEnvText();

        string[] defaultEnvLines =
        [
            "DISCORD_BOT_ENABLED=false",
            "DISCORD_BOT_TOKEN=PASTE_BOT_TOKEN_HERE",
            "DISCORD_GUILD_ID=0",
            "DISCORD_VOICE_CHANNEL_ID=0",
            "DISCORD_STREAM_PC_AUDIO=true",
            "DISCORD_TRY_SCREEN_SHARE=false",
            "VALOWATCH_INSTALL_OVERWOLF=true",
            "VALOWATCH_UPDATE_CHECK_ENABLED=true",
            "VALOWATCH_UPDATE_REPOSITORY=yss19411208/YAMAWATCH",
            "VALOWATCH_UPDATE_CURRENT_VERSION=0.1.0",
            "VALOWATCH_UPDATE_BRANCH=main",
            "VALOWATCH_UPDATE_CURRENT_COMMIT=",
            "VALOWATCH_GITHUB_TOKEN="
        ];

        if (!File.Exists(envExamplePath))
        {
            File.WriteAllLines(envExamplePath, defaultEnvLines);
        }

        if (File.Exists(envPath))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(installerSideEnvText))
        {
            File.WriteAllText(envPath, installerSideEnvText, Encoding.UTF8);
            return;
        }

        if (!string.IsNullOrWhiteSpace(embeddedEnvText))
        {
            File.WriteAllText(envPath, embeddedEnvText, Encoding.UTF8);
            return;
        }

        File.WriteAllLines(envPath, defaultEnvLines);
    }

    private static bool ShouldInstallOverwolfClient()
    {
        string installerSideEnvPath = Path.Combine(AppContext.BaseDirectory, ".env");
        string? configuredValue = ReadInstallerSideEnvValue(installerSideEnvPath, "VALOWATCH_INSTALL_OVERWOLF")
            ?? ReadEmbeddedEnvValue("VALOWATCH_INSTALL_OVERWOLF");
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return true;
        }

        string normalizedValue = configuredValue.Trim();
        if (bool.TryParse(normalizedValue, out bool parsedValue))
        {
            return parsedValue;
        }

        return !normalizedValue.Equals("0", StringComparison.OrdinalIgnoreCase)
            && !normalizedValue.Equals("no", StringComparison.OrdinalIgnoreCase)
            && !normalizedValue.Equals("off", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadInstallerSideEnvValue(string envPath, string key)
    {
        string? envText = TryReadTextFile(envPath);
        if (string.IsNullOrWhiteSpace(envText))
        {
            return null;
        }

        return ReadEnvValue(envText.Split(["\r\n", "\n"], StringSplitOptions.None), key);
    }

    private static string? ReadEmbeddedEnvValue(string key)
    {
        string? embeddedEnvText = ReadEmbeddedEnvText();
        if (string.IsNullOrWhiteSpace(embeddedEnvText))
        {
            return null;
        }

        return ReadEnvValue(embeddedEnvText.Split(["\r\n", "\n"], StringSplitOptions.None), key);
    }

    private static string? ReadEnvValue(IEnumerable<string> envLines, string key)
    {
        foreach (string rawLine in envLines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            int separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            string candidateKey = line[..separatorIndex].Trim();
            if (!candidateKey.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return line[(separatorIndex + 1)..].Trim();
        }

        return null;
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

    private static void ExtractOverwolfAppFiles(IProgress<InstallProgress> progress, int startPercent, int endPercent)
    {
        string localAppDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string overwolfAppDirectory = string.IsNullOrWhiteSpace(localAppDataDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "VALOWATCH", "overwolf", "VALOWATCH")
            : Path.Combine(localAppDataDirectory, "VALOWATCH", "overwolf", "VALOWATCH");

        Directory.CreateDirectory(overwolfAppDirectory);

        string[] overwolfResourceNames = typeof(Program).Assembly.GetManifestResourceNames()
            .Where(resourceName => resourceName.StartsWith("OverwolfApp/", StringComparison.Ordinal))
            .ToArray();

        for (int resourceIndex = 0; resourceIndex < overwolfResourceNames.Length; resourceIndex++)
        {
            const string resourcePrefix = "OverwolfApp/";
            string resourceName = overwolfResourceNames[resourceIndex];

            string relativeResourcePath = resourceName[resourcePrefix.Length..].Replace('/', Path.DirectorySeparatorChar);
            string targetPath = Path.Combine(overwolfAppDirectory, relativeResourcePath);
            string? targetDirectory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            using Stream? resourceStream = typeof(Program).Assembly.GetManifestResourceStream(resourceName);
            if (resourceStream is null)
            {
                continue;
            }

            using FileStream targetStream = new(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
            resourceStream.CopyTo(targetStream);

            int percent = CalculateStepPercent(startPercent, endPercent, resourceIndex + 1, overwolfResourceNames.Length);
            progress.Report(new InstallProgress(percent, "Overwolf app files を配置しています。"));
        }
    }

    private static void EnsureOverwolfClientInstalled(IProgress<InstallProgress> progress)
    {
        if (IsOverwolfInstalled())
        {
            progress.Report(new InstallProgress(90, "Overwolf は既にインストールされています。"));
            return;
        }

        try
        {
            DeleteStaleOverwolfInstallerDownload();
            string installerPath = DownloadOfficialOverwolfInstaller(progress, 84, 92);
            ValidateDownloadedInstaller(installerPath);
            bool installerExited = RunOfficialOverwolfInstaller(installerPath);
            if (!installerExited)
            {
                MessageBox.Show(
                    "The official Overwolf installer is still running.\n\nFinish that installer window to complete Overwolf installation.",
                    "VALOWATCH Setup",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            WaitForOverwolfInstallation();
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or TaskCanceledException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(
                $"Overwolf could not be downloaded or started.\n\nPlease download it manually:\n{OfficialOverwolfDownloadUri}\n\n{exception.Message}",
                "VALOWATCH Setup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private static bool IsOverwolfInstalled()
    {
        string localAppDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programFilesDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86Directory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        string[] candidatePaths =
        [
            Path.Combine(localAppDataDirectory, "Overwolf", "Overwolf.exe"),
            Path.Combine(localAppDataDirectory, "Overwolf", "OverwolfLauncher.exe"),
            Path.Combine(programFilesDirectory, "Overwolf", "Overwolf.exe"),
            Path.Combine(programFilesDirectory, "Overwolf", "OverwolfLauncher.exe"),
            Path.Combine(programFilesX86Directory, "Overwolf", "Overwolf.exe"),
            Path.Combine(programFilesX86Directory, "Overwolf", "OverwolfLauncher.exe")
        ];

        if (candidatePaths.Any(File.Exists))
        {
            return true;
        }

        return RegistryContainsOverwolf(Registry.CurrentUser)
            || RegistryContainsOverwolf(Registry.LocalMachine);
    }

    private static bool RegistryContainsOverwolf(RegistryKey registryRoot)
    {
        string[] uninstallRegistryPaths =
        [
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
            @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        ];

        foreach (string uninstallRegistryPath in uninstallRegistryPaths)
        {
            using RegistryKey? uninstallKey = registryRoot.OpenSubKey(uninstallRegistryPath, false);
            if (uninstallKey is null)
            {
                continue;
            }

            foreach (string subKeyName in uninstallKey.GetSubKeyNames())
            {
                using RegistryKey? appKey = uninstallKey.OpenSubKey(subKeyName, false);
                string? displayName = appKey?.GetValue("DisplayName") as string;
                if (displayName is not null && displayName.Contains("Overwolf", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void DeleteStaleOverwolfInstallerDownload()
    {
        string setupDirectory = Path.Combine(Path.GetTempPath(), "VALOWATCH");
        string staleInstallerPath = Path.Combine(setupDirectory, "OverwolfInstaller.exe");
        string staleDecompressedPath = $"{staleInstallerPath}.decompressed";

        DeleteFileIfExists(staleInstallerPath);
        DeleteFileIfExists(staleDecompressedPath);
    }

    private static void DeleteFileIfExists(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        try
        {
            File.Delete(filePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The previous broken download is harmless if it cannot be deleted.
        }
    }

    private static string DownloadOfficialOverwolfInstaller(
        IProgress<InstallProgress> progress,
        int startPercent,
        int endPercent)
    {
        string setupDirectory = Path.Combine(Path.GetTempPath(), "VALOWATCH");
        Directory.CreateDirectory(setupDirectory);

        string installerFileName = $"OverwolfInstaller-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.exe";
        string installerPath = Path.Combine(setupDirectory, installerFileName);

        using HttpClientHandler httpClientHandler = new()
        {
            AutomaticDecompression = DecompressionMethods.GZip
                | DecompressionMethods.Deflate
                | DecompressionMethods.Brotli
        };

        using HttpClient httpClient = new(httpClientHandler)
        {
            Timeout = TimeSpan.FromMinutes(2)
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("VALOWATCH-Setup/1.0");

        using (HttpResponseMessage response = httpClient
                   .GetAsync(OfficialOverwolfDownloadUri, HttpCompletionOption.ResponseHeadersRead)
                   .GetAwaiter()
                   .GetResult())
        {
            response.EnsureSuccessStatusCode();

            using Stream downloadStream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            using FileStream installerStream = new(installerPath, FileMode.Create, FileAccess.Write, FileShare.None);
            CopyStreamWithProgress(
                downloadStream,
                installerStream,
                progress,
                startPercent,
                endPercent,
                "Overwolf installer をダウンロードしています。",
                response.Content.Headers.ContentLength);
        }

        DecompressGzipFileInPlaceIfNeeded(installerPath);

        return installerPath;
    }

    private static void DecompressGzipFileInPlaceIfNeeded(string installerPath)
    {
        using (FileStream sniffStream = new(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (sniffStream.Length < 2)
            {
                return;
            }

            int firstByte = sniffStream.ReadByte();
            int secondByte = sniffStream.ReadByte();
            if (firstByte != 0x1F || secondByte != 0x8B)
            {
                return;
            }
        }

        string decompressedPath = $"{installerPath}.decompressed";
        using (FileStream compressedStream = new(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (GZipStream gzipStream = new(compressedStream, CompressionMode.Decompress))
        using (FileStream decompressedStream = new(decompressedPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            gzipStream.CopyTo(decompressedStream);
        }

        File.Copy(decompressedPath, installerPath, overwrite: true);
        File.Delete(decompressedPath);
    }

    private static void ValidateDownloadedInstaller(string installerPath)
    {
        FileInfo installerFile = new(installerPath);
        if (!installerFile.Exists || installerFile.Length < 64 * 1024)
        {
            throw new InvalidDataException("The downloaded Overwolf installer is too small to be a valid Windows installer.");
        }

        using FileStream installerStream = new(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        Span<byte> dosHeaderBytes = stackalloc byte[64];
        installerStream.ReadExactly(dosHeaderBytes);
        if (dosHeaderBytes[0] != 'M' || dosHeaderBytes[1] != 'Z')
        {
            throw new InvalidDataException("The downloaded Overwolf installer is not a Windows PE executable.");
        }

        int peHeaderOffset = BitConverter.ToInt32(dosHeaderBytes[0x3C..0x40]);
        if (peHeaderOffset < 64 || peHeaderOffset > installerFile.Length - 24)
        {
            throw new InvalidDataException("The downloaded Overwolf installer has an invalid PE header offset.");
        }

        installerStream.Position = peHeaderOffset;

        Span<byte> peHeaderBytes = stackalloc byte[24];
        installerStream.ReadExactly(peHeaderBytes);
        if (peHeaderBytes[0] != 'P' || peHeaderBytes[1] != 'E' || peHeaderBytes[2] != 0 || peHeaderBytes[3] != 0)
        {
            throw new InvalidDataException("The downloaded Overwolf installer does not contain a valid PE signature.");
        }

        ushort machineType = BitConverter.ToUInt16(peHeaderBytes[4..6]);
        if (machineType is not (0x014C or 0x8664 or 0xAA64))
        {
            throw new InvalidDataException($"The downloaded Overwolf installer targets an unsupported CPU type: 0x{machineType:X4}.");
        }

        if (!VerifyAuthenticodeSignature(installerPath))
        {
            throw new InvalidDataException("The downloaded Overwolf installer does not have a valid Authenticode signature.");
        }
    }

    private static bool RunOfficialOverwolfInstaller(string installerPath)
    {
        ProcessStartInfo processStartInfo = new()
        {
            FileName = installerPath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? AppContext.BaseDirectory
        };

        using Process installerProcess = StartProcessWithRetry(processStartInfo)
            ?? throw new InvalidOperationException("The official Overwolf installer process could not be started.");

        return installerProcess.WaitForExit((int)OverwolfInstallerExitTimeout.TotalMilliseconds);
    }

    private static Process? StartProcessWithRetry(ProcessStartInfo processStartInfo)
    {
        const int maxAttemptCount = 5;

        for (int attemptNumber = 1; attemptNumber <= maxAttemptCount; attemptNumber++)
        {
            try
            {
                return Process.Start(processStartInfo);
            }
            catch (System.ComponentModel.Win32Exception) when (attemptNumber < maxAttemptCount)
            {
                Thread.Sleep(1000);
            }
        }

        return Process.Start(processStartInfo);
    }

    private static void WaitForOverwolfInstallation()
    {
        DateTime deadlineUtc = DateTime.UtcNow.Add(OverwolfDetectionTimeout);
        while (DateTime.UtcNow < deadlineUtc)
        {
            if (IsOverwolfInstalled())
            {
                return;
            }

            Thread.Sleep(2000);
        }

        MessageBox.Show(
            "The official Overwolf installer finished, but VALOWATCH could not confirm that Overwolf is installed.\n\nIf Overwolf is open, you can continue. If not, run the Overwolf installer again.",
            "VALOWATCH Setup",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private static bool VerifyAuthenticodeSignature(string filePath)
    {
        WinTrustFileInfo fileInfo = new()
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = filePath,
            FileHandle = IntPtr.Zero,
            KnownSubject = IntPtr.Zero
        };

        IntPtr fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);

            WinTrustData trustData = new()
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                PolicyCallbackData = IntPtr.Zero,
                SipClientData = IntPtr.Zero,
                UiChoice = WinTrustUiNone,
                RevocationChecks = WinTrustRevokeNone,
                UnionChoice = WinTrustChoiceFile,
                FileInfoPointer = fileInfoPointer,
                StateAction = WinTrustStateActionVerify,
                StateData = IntPtr.Zero,
                UrlReference = IntPtr.Zero,
                ProvFlags = 0,
                UiContext = 0,
                SignatureSettings = IntPtr.Zero
            };

            Guid actionId = WintrustActionGenericVerifyV2;
            int result = WinVerifyTrust(IntPtr.Zero, ref actionId, ref trustData);

            trustData.StateAction = WinTrustStateActionClose;
            _ = WinVerifyTrust(IntPtr.Zero, ref actionId, ref trustData);

            return result == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
            Marshal.FreeHGlobal(fileInfoPointer);
        }
    }

    private static void StopRunningInstalledApp(string installedExecutablePath)
    {
        foreach (Process candidateProcess in Process.GetProcessesByName("VALOWATCH"))
        {
            using (candidateProcess)
            {
                if (candidateProcess.Id == Environment.ProcessId)
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

    private static void RegisterStartup(string installedExecutablePath)
    {
        using RegistryKey registryKey = Registry.CurrentUser.CreateSubKey(RegistryRunPath, true)
            ?? throw new InvalidOperationException("Windows startup registry key could not be opened.");

        registryKey.SetValue(RegistryValueName, $"\"{installedExecutablePath}\"");
        WriteStartupCommand(installedExecutablePath);
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

    private static int CalculateStepPercent(int startPercent, int endPercent, int completedCount, int totalCount)
    {
        if (totalCount <= 0)
        {
            return endPercent;
        }

        return Math.Clamp(
            startPercent + (endPercent - startPercent) * completedCount / totalCount,
            startPercent,
            endPercent);
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
}
