using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace VALOWATCH;

public sealed class MainForm : Form
{
    private const int StratsHotKeyId = 9101;
    private const string NotConnectedText = "Not connected";
    private const string DetectedText = "VALORANT detected";
    private const string NotDetectedText = "VALORANT not detected";
    private static readonly TimeSpan DiscordRetryInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan GitUpdateCheckInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ValorantStopGracePeriod = TimeSpan.FromSeconds(20);

    private readonly AppPaths appPaths;
    private readonly AppStateStore appStateStore;
    private readonly LoopbackRecorder loopbackRecorder;
    private readonly DiscordMediaSharer discordMediaSharer;
    private readonly VideoCaptureSession videoCaptureSession;
    private readonly DiscordBotVoiceRelay discordBotVoiceRelay;
    private readonly GitUpdateChecker gitUpdateChecker;
    private readonly GitAutoUpdater gitAutoUpdater;
    private readonly StartupService startupService;
    private readonly bool disableDiscordAutomation;
    private readonly System.Windows.Forms.Timer processTimer = new();
    private readonly System.Windows.Forms.Timer recordingTimer = new();
    private readonly System.Windows.Forms.Timer stratsToggleDelayTimer = new();
    private readonly List<TeammateControls> teammateControls = [];

    private AppState appState;
    private RecordingHistoryEntry? activeRecordingEntry;
    private Label valorantStatusLabel = null!;
    private Label recordingStatusLabel = null!;
    private Label discordStatusLabel = null!;
    private Label stratsStatusLabel = null!;
    private Label recordingElapsedLabel = null!;
    private Button startRecordingButton = null!;
    private Button stopRecordingButton = null!;
    private Button shareLatestButton = null!;
    private Button openRecordingsButton = null!;
    private Button openSharedMediaButton = null!;
    private Button openStratsButton = null!;
    private CheckBox topMostCheckBox = null!;
    private CheckBox startupCheckBox = null!;
    private ListView historyListView = null!;
    private NotifyIcon trayIcon = null!;
    private StratsOverlayForm? stratsOverlayForm;
    private bool suppressStartupToggle;
    private bool hotKeyRegistered;
    private bool lastValorantDetected;
    private bool discordTransitionInProgress;
    private bool hidOnInitialShow;
    private bool stratsTogglePending;
    private bool stratsToggleInProgress;
    private bool stratsPreloadInProgress;
    private bool gitUpdateCheckInProgress;
    private bool automaticRecordingStarted;
    private bool automaticVideoCaptureStarted;
    private bool automaticUploadInProgress;
    private DateTimeOffset? valorantMissingSinceUtc;
    private DateTimeOffset nextDiscordRetryAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset nextGitUpdateCheckAtUtc = DateTimeOffset.MinValue;

    public MainForm(
        AppPaths appPaths,
        AppStateStore appStateStore,
        LoopbackRecorder loopbackRecorder,
        DiscordMediaSharer discordMediaSharer,
        VideoCaptureSession videoCaptureSession,
        DiscordBotVoiceRelay discordBotVoiceRelay,
        GitUpdateChecker gitUpdateChecker,
        GitAutoUpdater gitAutoUpdater,
        StartupService startupService,
        bool disableDiscordAutomation)
    {
        this.appPaths = appPaths;
        this.appStateStore = appStateStore;
        this.loopbackRecorder = loopbackRecorder;
        this.discordMediaSharer = discordMediaSharer;
        this.videoCaptureSession = videoCaptureSession;
        this.discordBotVoiceRelay = discordBotVoiceRelay;
        this.gitUpdateChecker = gitUpdateChecker;
        this.gitAutoUpdater = gitAutoUpdater;
        this.startupService = startupService;
        this.disableDiscordAutomation = disableDiscordAutomation;
        appState = appStateStore.Load();

        BuildInterface();
        LoadTeammateInputs();
        RefreshHistoryList();
        RefreshStatusLabels();

        processTimer.Interval = 2000;
        processTimer.Tick += (_, _) => RefreshValorantStatus();
        processTimer.Start();

        recordingTimer.Interval = 1000;
        recordingTimer.Tick += (_, _) => RefreshRecordingTimer();
        recordingTimer.Start();

        stratsToggleDelayTimer.Interval = 30;
        stratsToggleDelayTimer.Tick += (_, _) => RunPendingStratsToggleAfterHotKeyRelease();
    }

    protected override void OnFormClosing(FormClosingEventArgs eventArgs)
    {
        UnregisterStratsHotKey();
        processTimer.Stop();
        recordingTimer.Stop();
        stratsToggleDelayTimer.Stop();
        trayIcon.Visible = false;
        DisposeStratsOverlay();
        if (loopbackRecorder.IsRecording)
        {
            automaticRecordingStarted = false;
            StopRecordingCore(showUserMessage: false);
        }

        if (videoCaptureSession.IsRecording)
        {
            automaticVideoCaptureStarted = false;
            try
            {
                videoCaptureSession.StopAsync().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                WriteAppLog("Video", "Video capture stop during shutdown failed.", exception);
            }
        }

        loopbackRecorder.Dispose();
        videoCaptureSession.Dispose();
        discordBotVoiceRelay.Dispose();
        base.OnFormClosing(eventArgs);
    }

    protected override void OnShown(EventArgs eventArgs)
    {
        base.OnShown(eventArgs);

        if (hidOnInitialShow)
        {
            return;
        }

        hidOnInitialShow = true;
        Hide();
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        RegisterStratsHotKey();
    }

    protected override void OnHandleDestroyed(EventArgs eventArgs)
    {
        UnregisterStratsHotKey();
        base.OnHandleDestroyed(eventArgs);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == NativeMethods.WmHotKey && message.WParam.ToInt32() == StratsHotKeyId)
        {
            ScheduleStratsOverlayToggleAfterHotKeyRelease();
            return;
        }

        base.WndProc(ref message);
    }

    private void BuildInterface()
    {
        Text = "VALOWATCH";
        Size = new Size(1, 1);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        BackColor = Color.Black;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10F);
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        Opacity = 1;
        TopMost = false;
        ShowInTaskbar = false;

        InitializeHeadlessControls();

        trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "VALOWATCH",
            Visible = true,
            ContextMenuStrip = BuildTrayMenu()
        };
        trayIcon.DoubleClick += (_, _) => ToggleStratsOverlayWhenValorantRunning();
    }

    private void InitializeHeadlessControls()
    {
        valorantStatusLabel = CreateStatusPill(NotDetectedText, Color.FromArgb(110, 118, 136));
        stratsStatusLabel = CreateStatusPill("Alt + T ready", Color.FromArgb(110, 118, 136));
        discordStatusLabel = CreateStatusPill(discordBotVoiceRelay.StatusText, Color.FromArgb(110, 118, 136));
        recordingStatusLabel = CreateStatusPill("Recording idle", Color.FromArgb(110, 118, 136));
        recordingElapsedLabel = new Label { Text = "No active recording" };

        startRecordingButton = new Button { Enabled = true };
        stopRecordingButton = new Button { Enabled = false };
        shareLatestButton = new Button { Enabled = true };
        openRecordingsButton = new Button();
        openSharedMediaButton = new Button();
        openStratsButton = new Button();
        topMostCheckBox = new CheckBox { Checked = true };
        startupCheckBox = new CheckBox { Checked = SafeIsStartupEnabled() };
        historyListView = new ListView();

        teammateControls.Clear();
        for (int teammateIndex = 0; teammateIndex < appState.Teammates.Count; teammateIndex++)
        {
            teammateControls.Add(new TeammateControls(new TextBox(), new Label()));
        }
    }

    private Control BuildHeaderPanel()
    {
        Panel headerPanel = new()
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(23, 26, 34),
            Padding = new Padding(14)
        };

        Label titleLabel = new()
        {
            Text = "VALOWATCH",
            Dock = DockStyle.Left,
            AutoSize = false,
            Width = 220,
            Font = new Font("Segoe UI Semibold", 24F, FontStyle.Bold),
            ForeColor = Color.FromArgb(255, 76, 86),
            TextAlign = ContentAlignment.MiddleLeft
        };
        headerPanel.Controls.Add(titleLabel);

        FlowLayoutPanel statusPanel = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(10, 8, 0, 0)
        };

        valorantStatusLabel = CreateStatusPill(NotDetectedText, Color.FromArgb(110, 118, 136));
        stratsStatusLabel = CreateStatusPill("Alt + T ready", Color.FromArgb(110, 118, 136));
        discordStatusLabel = CreateStatusPill(discordBotVoiceRelay.StatusText, Color.FromArgb(110, 118, 136));
        recordingStatusLabel = CreateStatusPill("Recording idle", Color.FromArgb(110, 118, 136));
        statusPanel.Controls.Add(valorantStatusLabel);
        statusPanel.Controls.Add(stratsStatusLabel);
        statusPanel.Controls.Add(discordStatusLabel);
        statusPanel.Controls.Add(recordingStatusLabel);
        headerPanel.Controls.Add(statusPanel);

        return headerPanel;
    }

    private Control BuildActionPanel()
    {
        TableLayoutPanel actionLayout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 2,
            BackColor = Color.FromArgb(16, 18, 24),
            Padding = new Padding(0, 12, 0, 12)
        };
        actionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        actionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        actionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        actionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        actionLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        actionLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));

        startRecordingButton = CreateCommandButton("Start recording");
        startRecordingButton.Click += (_, _) => StartRecording();

        stopRecordingButton = CreateCommandButton("Stop recording");
        stopRecordingButton.Enabled = false;
        stopRecordingButton.Click += (_, _) => StopRecording();

        shareLatestButton = CreateCommandButton("Share latest");
        shareLatestButton.Click += async (_, _) => await ShareLatestRecordingAsync().ConfigureAwait(true);

        openRecordingsButton = CreateCommandButton("Open recordings");
        openRecordingsButton.Click += (_, _) => OpenFolder(appPaths.RecordingsDirectory);

        openSharedMediaButton = CreateSecondaryButton("Open shared media");
        openSharedMediaButton.Click += (_, _) => OpenFolder(appPaths.SharedMediaDirectory);

        openStratsButton = CreateSecondaryButton("Open strats overlay");
        openStratsButton.Click += (_, _) => ToggleStratsOverlayWhenValorantRunning();

        topMostCheckBox = CreateCheckBox("Top most overlay", true);
        topMostCheckBox.CheckedChanged += (_, _) => TopMost = topMostCheckBox.Checked;

        startupCheckBox = CreateCheckBox("Start with Windows", SafeIsStartupEnabled());
        startupCheckBox.CheckedChanged += (_, _) => ToggleStartup();

        actionLayout.Controls.Add(startRecordingButton, 0, 0);
        actionLayout.Controls.Add(stopRecordingButton, 1, 0);
        actionLayout.Controls.Add(shareLatestButton, 2, 0);
        actionLayout.Controls.Add(openRecordingsButton, 3, 0);
        actionLayout.Controls.Add(openSharedMediaButton, 0, 1);
        actionLayout.Controls.Add(openStratsButton, 1, 1);
        actionLayout.Controls.Add(topMostCheckBox, 2, 1);
        actionLayout.Controls.Add(startupCheckBox, 3, 1);

        return actionLayout;
    }

    private Control BuildTeammatePanel()
    {
        Panel panel = new()
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(23, 26, 34),
            Padding = new Padding(12)
        };

        Label titleLabel = new()
        {
            Text = "Teammate Overlay",
            Dock = DockStyle.Top,
            Height = 32,
            Font = new Font("Segoe UI Semibold", 13F, FontStyle.Bold),
            ForeColor = Color.White
        };
        panel.Controls.Add(titleLabel);

        TableLayoutPanel teammateLayout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 6,
            BackColor = Color.FromArgb(23, 26, 34),
            Padding = new Padding(0, 8, 0, 0)
        };
        teammateLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        teammateLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        teammateLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        teammateLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        teammateLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        for (int rowIndex = 1; rowIndex <= 5; rowIndex++)
        {
            teammateLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
        }

        teammateLayout.Controls.Add(CreateHeaderLabel("Slot"), 0, 0);
        teammateLayout.Controls.Add(CreateHeaderLabel("Riot ID"), 1, 0);
        teammateLayout.Controls.Add(CreateHeaderLabel("States"), 2, 0);
        teammateLayout.Controls.Add(CreateHeaderLabel("Action"), 3, 0);

        for (int slotIndex = 0; slotIndex < 5; slotIndex++)
        {
            int displayNumber = slotIndex + 1;
            Label slotLabel = new()
            {
                Text = $"Mate {displayNumber}",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(190, 199, 214)
            };

            TextBox riotIdTextBox = new()
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(30, 34, 45),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(4)
            };

            Label stateLabel = new()
            {
                Text = TeammateSlot.DefaultStateText,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(255, 205, 112),
                Margin = new Padding(4)
            };

            Button openProfileButton = CreateSecondaryButton("Open profile");
            openProfileButton.Tag = slotIndex;
            openProfileButton.Click += (_, _) => OpenTrackerProfile(slotIndex);

            teammateControls.Add(new TeammateControls(riotIdTextBox, stateLabel));

            teammateLayout.Controls.Add(slotLabel, 0, displayNumber);
            teammateLayout.Controls.Add(riotIdTextBox, 1, displayNumber);
            teammateLayout.Controls.Add(stateLabel, 2, displayNumber);
            teammateLayout.Controls.Add(openProfileButton, 3, displayNumber);
        }

        panel.Controls.Add(teammateLayout);
        return panel;
    }

    private Control BuildHistoryPanel()
    {
        Panel historyPanel = new()
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(23, 26, 34),
            Padding = new Padding(12)
        };

        recordingElapsedLabel = new Label
        {
            Text = "No active recording",
            Dock = DockStyle.Top,
            Height = 28,
            ForeColor = Color.FromArgb(190, 199, 214)
        };
        historyPanel.Controls.Add(recordingElapsedLabel);

        historyListView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            BackColor = Color.FromArgb(16, 18, 24),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.None
        };
        historyListView.Columns.Add("Start", 160);
        historyListView.Columns.Add("Duration", 100);
        historyListView.Columns.Add("File", 360);
        historyListView.Columns.Add("Upload", 160);
        historyPanel.Controls.Add(historyListView);

        return historyPanel;
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        ContextMenuStrip trayMenu = new();
        trayMenu.Items.Add("Strats overlay (Alt+T)", null, (_, _) => ToggleStratsOverlayWhenValorantRunning());
        trayMenu.Items.Add("Exit", null, (_, _) => Close());
        return trayMenu;
    }

    private static Label CreateStatusPill(string text, Color backgroundColor)
    {
        return new Label
        {
            Text = text,
            BackColor = backgroundColor,
            ForeColor = Color.White,
            AutoSize = false,
            Width = 170,
            Height = 34,
            Margin = new Padding(0, 0, 10, 0),
            TextAlign = ContentAlignment.MiddleCenter
        };
    }

    private static Button CreateCommandButton(string text)
    {
        return new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(255, 76, 86),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(4)
        };
    }

    private static Button CreateSecondaryButton(string text)
    {
        return new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(39, 45, 59),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(4)
        };
    }

    private static CheckBox CreateCheckBox(string text, bool isChecked)
    {
        return new CheckBox
        {
            Text = text,
            Checked = isChecked,
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(210, 217, 229),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(8, 4, 4, 4)
        };
    }

    private static Label CreateHeaderLabel(string text)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(143, 153, 171),
            Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold)
        };
    }

    private void LoadTeammateInputs()
    {
        for (int teammateIndex = 0; teammateIndex < teammateControls.Count; teammateIndex++)
        {
            TeammateSlot teammateSlot = appState.Teammates[teammateIndex];
            teammateControls[teammateIndex].RiotIdTextBox.Text = teammateSlot.RiotId;
            teammateControls[teammateIndex].StateLabel.Text = string.IsNullOrWhiteSpace(teammateSlot.StateText)
                ? TeammateSlot.DefaultStateText
                : teammateSlot.StateText;
        }
    }

    private void SaveTeammates()
    {
        for (int teammateIndex = 0; teammateIndex < teammateControls.Count; teammateIndex++)
        {
            TeammateControls controls = teammateControls[teammateIndex];
            TeammateSlot teammateSlot = appState.Teammates[teammateIndex];
            teammateSlot.RiotId = controls.RiotIdTextBox.Text.Trim();
            teammateSlot.StateText = TeammateSlot.DefaultStateText;
            controls.StateLabel.Text = TeammateSlot.DefaultStateText;
        }

        appStateStore.Save(appState);
        ShowInfo("味方情報を保存しました。");
    }

    private void StartRecording()
    {
        StartRecordingCore(showUserMessage: true);
    }

    private RecordingHistoryEntry? StartRecordingCore(bool showUserMessage, string? timestampText = null)
    {
        if (loopbackRecorder.IsRecording)
        {
            return null;
        }

        string safeTimestampText = string.IsNullOrWhiteSpace(timestampText)
            ? DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)
            : timestampText;
        string recordingFilePath = Path.Combine(appPaths.RecordingsDirectory, $"VALOWATCH_{safeTimestampText}.wav");

        try
        {
            loopbackRecorder.Start(recordingFilePath);
            activeRecordingEntry = RecordingHistoryEntry.Start(recordingFilePath, appState.Teammates);
            startRecordingButton.Enabled = false;
            stopRecordingButton.Enabled = true;
            RefreshStatusLabels();
            RefreshRecordingTimer();
            WriteAppLog("Recording", $"Recording started: {recordingFilePath}");
            return activeRecordingEntry;
        }
        catch (Exception exception)
        {
            if (showUserMessage)
            {
                ShowError($"録音を開始できませんでした。{exception.Message}");
            }
            else
            {
                WriteAppLog("Recording", "Automatic recording start failed.", exception);
            }

            return null;
        }
    }

    private void StopRecording()
    {
        automaticRecordingStarted = false;
        StopRecordingCore(showUserMessage: true);
    }

    private RecordingHistoryEntry? StopRecordingCore(bool showUserMessage)
    {
        if (!loopbackRecorder.IsRecording)
        {
            return null;
        }

        RecordingHistoryEntry? finishedEntry = null;
        try
        {
            loopbackRecorder.Stop();
            if (activeRecordingEntry is not null)
            {
                activeRecordingEntry.Finish();
                finishedEntry = activeRecordingEntry;
                appState.History.Insert(0, activeRecordingEntry);
                activeRecordingEntry = null;
                appStateStore.Save(appState);
                RefreshHistoryList();
                WriteAppLog("Recording", $"Recording stopped: {finishedEntry.FilePath}");
            }
        }
        catch (Exception exception)
        {
            if (showUserMessage)
            {
                ShowError($"録音を停止できませんでした。{exception.Message}");
            }
            else
            {
                WriteAppLog("Recording", "Automatic recording stop failed.", exception);
            }
        }
        finally
        {
            startRecordingButton.Enabled = true;
            stopRecordingButton.Enabled = false;
            RefreshStatusLabels();
            RefreshRecordingTimer();
        }

        return finishedEntry;
    }

    private async Task ShareLatestRecordingAsync()
    {
        RecordingHistoryEntry? latestEntry = appState.History.FirstOrDefault();
        if (latestEntry is null)
        {
            ShowInfo("共有できる録音履歴がありません。");
            return;
        }

        shareLatestButton.Enabled = false;
        try
        {
            await ShareRecordingToDiscordAsync(latestEntry, showUserMessage: true).ConfigureAwait(true);
        }
        finally
        {
            shareLatestButton.Enabled = true;
            RefreshStatusLabels();
        }
    }

    private void OpenTrackerProfile(int teammateIndex)
    {
        string riotId = teammateControls[teammateIndex].RiotIdTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(riotId))
        {
            ShowInfo("Riot IDを入力してください。例: Player#JP1");
            return;
        }

        Uri profileUri = TrackerProfileUrlBuilder.BuildProfileUri(riotId);
        ProcessStartInfo processStartInfo = new()
        {
            FileName = profileUri.ToString(),
            UseShellExecute = true
        };

        try
        {
            Process.Start(processStartInfo);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ShowError($"ブラウザを開けませんでした。{exception.Message}");
        }
    }

    private void RefreshValorantStatus()
    {
        bool rawValorantDetected = ValorantProcessMonitor.IsValorantRunning();
        bool valorantDetected = GetDebouncedValorantDetected(rawValorantDetected);
        valorantStatusLabel.Text = rawValorantDetected || valorantDetected ? DetectedText : NotDetectedText;
        valorantStatusLabel.BackColor = rawValorantDetected || valorantDetected
            ? Color.FromArgb(39, 145, 104)
            : Color.FromArgb(110, 118, 136);

        if (!valorantDetected && stratsOverlayForm is not null)
        {
            if (stratsOverlayForm.IsOverlayVisible)
            {
                stratsOverlayForm.HideOverlayKeepingPage();
            }

            stratsStatusLabel.Text = "Launch VALORANT first";
            stratsStatusLabel.BackColor = Color.FromArgb(110, 118, 136);
        }

        if (valorantDetected != lastValorantDetected)
        {
            lastValorantDetected = valorantDetected;
            nextDiscordRetryAtUtc = valorantDetected
                ? DateTimeOffset.UtcNow.Add(DiscordRetryInterval)
                : DateTimeOffset.MinValue;
            if (valorantDetected)
            {
                nextGitUpdateCheckAtUtc = DateTimeOffset.MinValue;
                PreloadStratsOverlayIfNeeded();
                StartGitUpdateCheckIfNeeded(force: true);
            }

            HandleRecordingAutomationStateChange(valorantDetected);
            _ = HandleValorantStateChangeAsync(valorantDetected);
        }
        else if (ShouldRetryDiscordStart(valorantDetected))
        {
            nextDiscordRetryAtUtc = DateTimeOffset.UtcNow.Add(DiscordRetryInterval);
            _ = HandleValorantStateChangeAsync(valorantDetected);
        }

        if (valorantDetected)
        {
            PreloadStratsOverlayIfNeeded();
        }

        StartGitUpdateCheckIfNeeded(force: false);
        RefreshDiscordStatusLabel();
    }

    private void HandleRecordingAutomationStateChange(bool valorantDetected)
    {
        if (valorantDetected)
        {
            StartAutomaticRecordingIfNeeded();
            return;
        }

        _ = StopAutomaticRecordingAndUploadAsync();
    }

    private void StartAutomaticRecordingIfNeeded()
    {
        string timestampText = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

        if (!automaticRecordingStarted && !loopbackRecorder.IsRecording)
        {
            RecordingHistoryEntry? recordingEntry = StartRecordingCore(showUserMessage: false, timestampText);
            if (recordingEntry is not null)
            {
                automaticRecordingStarted = true;
                WriteAppLog("Recording", "Automatic audio recording started for VALORANT session.");
            }
        }

        if (automaticVideoCaptureStarted || videoCaptureSession.IsRecording)
        {
            return;
        }

        try
        {
            IReadOnlyList<VideoCaptureResult> videoCaptureResults = videoCaptureSession.Start(timestampText);
            foreach (string startupWarning in videoCaptureSession.LastStartupWarnings)
            {
                WriteAppLog("Video", startupWarning);
            }

            if (videoCaptureResults.Count > 0)
            {
                automaticVideoCaptureStarted = true;
                WriteAppLog(
                    "Video",
                    $"Automatic video capture started for VALORANT session. Files: {string.Join(", ", videoCaptureResults.Select(result => result.FilePath))}");
            }
        }
        catch (Exception exception)
        {
            WriteAppLog("Video", "Automatic video capture start failed.", exception);
        }
    }

    private async Task StopAutomaticRecordingAndUploadAsync()
    {
        if (!automaticRecordingStarted && !automaticVideoCaptureStarted)
        {
            return;
        }

        RecordingHistoryEntry? finishedEntry = null;
        if (automaticRecordingStarted)
        {
            automaticRecordingStarted = false;
            finishedEntry = StopRecordingCore(showUserMessage: false);
        }

        IReadOnlyList<VideoCaptureResult> finishedVideoCaptures = [];
        if (automaticVideoCaptureStarted)
        {
            automaticVideoCaptureStarted = false;
            try
            {
                finishedVideoCaptures = await videoCaptureSession.StopAsync().ConfigureAwait(true);
                WriteAppLog(
                    "Video",
                    $"Automatic video capture stopped. Files: {string.Join(", ", finishedVideoCaptures.Select(result => result.FilePath))}");
            }
            catch (Exception exception)
            {
                WriteAppLog("Video", "Automatic video capture stop failed.", exception);
            }
        }

        if (finishedEntry is null && finishedVideoCaptures.Count == 0)
        {
            return;
        }

        if (automaticUploadInProgress)
        {
            WriteAppLog("Share", "Automatic media share skipped because another share/upload is already running.");
            return;
        }

        automaticUploadInProgress = true;
        try
        {
            if (finishedEntry is not null)
            {
                await ShareRecordingToDiscordAsync(finishedEntry).ConfigureAwait(true);
            }

            foreach (VideoCaptureResult finishedVideoCapture in finishedVideoCaptures)
            {
                await ShareVideoCaptureToDiscordAsync(finishedVideoCapture).ConfigureAwait(true);
            }
        }
        finally
        {
            automaticUploadInProgress = false;
        }
    }

    private async Task ShareRecordingToDiscordAsync(RecordingHistoryEntry recordingEntry, bool showUserMessage = false)
    {
        try
        {
            DiscordMediaShareResult shareResult = await discordMediaSharer
                .ShareAudioRecordingAsync(recordingEntry, CancellationToken.None)
                .ConfigureAwait(true);
            recordingEntry.UploadStatus = shareResult.Sent ? "Discord shared" : "Share skipped";
            appStateStore.Save(appState);
            RefreshHistoryList();
            WriteAppLog(
                "DiscordShare",
                $"{shareResult.StatusText} Attempted: {shareResult.Attempted}. Sent: {shareResult.Sent}. File: {shareResult.SharedFilePath ?? "(none)"}");
            if (showUserMessage)
            {
                if (shareResult.Sent)
                {
                    ShowInfo("Discordへ共有しました。");
                }
                else
                {
                    ShowInfo(shareResult.StatusText);
                }
            }
        }
        catch (Exception exception)
        {
            recordingEntry.UploadStatus = "Share failed";
            appStateStore.Save(appState);
            RefreshHistoryList();
            WriteAppLog("DiscordShare", "Automatic audio MP3 share failed.", exception);
            if (showUserMessage)
            {
                ShowError(exception.Message);
            }
        }
    }

    private async Task ShareVideoCaptureToDiscordAsync(VideoCaptureResult videoCaptureResult)
    {
        try
        {
            DiscordMediaShareResult shareResult = await discordMediaSharer
                .ShareVideoCaptureAsync(videoCaptureResult, CancellationToken.None)
                .ConfigureAwait(true);
            WriteAppLog(
                "DiscordShare",
                $"{shareResult.StatusText} Kind: {videoCaptureResult.Kind}. Attempted: {shareResult.Attempted}. Sent: {shareResult.Sent}. File: {shareResult.SharedFilePath ?? "(none)"}");
        }
        catch (Exception exception)
        {
            WriteAppLog(
                "DiscordShare",
                $"Automatic video MP4 share failed. Kind: {videoCaptureResult.Kind}.",
                exception);
        }
    }

    private bool GetDebouncedValorantDetected(bool rawValorantDetected)
    {
        if (rawValorantDetected)
        {
            valorantMissingSinceUtc = null;
            return true;
        }

        if (!lastValorantDetected)
        {
            valorantMissingSinceUtc = null;
            return false;
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        valorantMissingSinceUtc ??= nowUtc;
        return nowUtc - valorantMissingSinceUtc.Value < ValorantStopGracePeriod;
    }

    private void RefreshStatusLabels()
    {
        RefreshValorantStatus();

        bool isRecording = loopbackRecorder.IsRecording;
        recordingStatusLabel.Text = isRecording ? "Recording" : "Recording idle";
        recordingStatusLabel.BackColor = isRecording
            ? Color.FromArgb(255, 76, 86)
            : Color.FromArgb(110, 118, 136);

        RefreshDiscordStatusLabel();
    }

    private void RefreshRecordingTimer()
    {
        if (loopbackRecorder.StartedAt is null)
        {
            recordingElapsedLabel.Text = "No active recording";
            return;
        }

        TimeSpan elapsed = DateTimeOffset.Now - loopbackRecorder.StartedAt.Value;
        recordingElapsedLabel.Text = $"Recording: {elapsed:hh\\:mm\\:ss}";
    }

    private void RefreshHistoryList()
    {
        historyListView.Items.Clear();

        foreach (RecordingHistoryEntry historyEntry in appState.History.Take(50))
        {
            string durationText = historyEntry.EndedAt is null
                ? "Active"
                : (historyEntry.EndedAt.Value - historyEntry.StartedAt).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

            string fileName = string.IsNullOrWhiteSpace(historyEntry.FilePath)
                ? "(missing)"
                : Path.GetFileName(historyEntry.FilePath);

            ListViewItem listViewItem = new(historyEntry.StartedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            listViewItem.SubItems.Add(durationText);
            listViewItem.SubItems.Add(fileName);
            listViewItem.SubItems.Add(historyEntry.UploadStatus);
            historyListView.Items.Add(listViewItem);
        }
    }

    private void ToggleStartup()
    {
        if (suppressStartupToggle)
        {
            return;
        }

        try
        {
            startupService.SetEnabled(startupCheckBox.Checked);
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            suppressStartupToggle = true;
            startupCheckBox.Checked = SafeIsStartupEnabled();
            suppressStartupToggle = false;
            ShowError($"スタートアップ設定を変更できませんでした。{exception.Message}");
        }
    }

    private void OpenFolder(string folderPath)
    {
        try
        {
            Directory.CreateDirectory(folderPath);
            ProcessStartInfo processStartInfo = new()
            {
                FileName = folderPath,
                UseShellExecute = true
            };
            Process.Start(processStartInfo);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ShowError($"フォルダーを開けませんでした。{exception.Message}");
        }
    }

    private void WriteUpdateLog(string message, Exception? exception = null)
    {
        WriteAppLog("Update", message, exception);
    }

    private void WriteUserMessageLog(string severity, string message)
    {
        WriteAppLog("UI", $"{severity}: {message}");
    }

    private void WriteAppLog(string category, string message, Exception? exception = null)
    {
        try
        {
            string logFilePath = Path.Combine(appPaths.DataDirectory, "logs", "valowatch.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath) ?? appPaths.DataDirectory);
            string exceptionText = exception is null ? string.Empty : $" Exception: {exception}";
            File.AppendAllText(logFilePath, $"{DateTimeOffset.Now:O} [{category}] {message}{exceptionText}{Environment.NewLine}");
        }
        catch (Exception logException) when (logException is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ShowInfo(string message)
    {
        WriteUserMessageLog("Info", message);
    }

    private void ShowError(string message)
    {
        WriteUserMessageLog("Error", message);
    }

    private bool SafeIsStartupEnabled()
    {
        try
        {
            return startupService.IsEnabled();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private void RegisterStratsHotKey()
    {
        if (hotKeyRegistered || Handle == IntPtr.Zero)
        {
            return;
        }

        hotKeyRegistered = NativeMethods.RegisterHotKey(
            Handle,
            StratsHotKeyId,
            NativeMethods.ModAlt,
            NativeMethods.VirtualKeyT);

        if (hotKeyRegistered)
        {
            WriteAppLog("Overlay", "Alt + T hotkey registered.");
        }
        else
        {
            WriteAppLog("Overlay", $"Alt + T hotkey registration failed. Win32Error: {Marshal.GetLastWin32Error()}.");
        }

        if (stratsStatusLabel is null)
        {
            return;
        }

        stratsStatusLabel.Text = hotKeyRegistered ? "Alt + T ready" : "Alt + T unavailable";
        stratsStatusLabel.BackColor = hotKeyRegistered
            ? Color.FromArgb(39, 145, 104)
            : Color.FromArgb(153, 99, 53);
    }

    private void UnregisterStratsHotKey()
    {
        if (!hotKeyRegistered || Handle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.UnregisterHotKey(Handle, StratsHotKeyId);
        hotKeyRegistered = false;
    }

    private void ToggleStratsOverlayWhenValorantRunning()
    {
        _ = ToggleStratsOverlayWhenValorantRunningAsync();
    }

    private void PreloadStratsOverlayIfNeeded()
    {
        if (stratsPreloadInProgress || stratsOverlayForm is not null)
        {
            return;
        }

        stratsPreloadInProgress = true;
        _ = PreloadStratsOverlayAsync();
    }

    private async Task PreloadStratsOverlayAsync()
    {
        try
        {
            stratsOverlayForm = new StratsOverlayForm();
            Rectangle targetBounds = GetValorantTargetBounds();

            stratsStatusLabel.Text = "Strats preloading";
            stratsStatusLabel.BackColor = Color.FromArgb(79, 118, 214);
            await stratsOverlayForm.PreloadAsync(targetBounds).ConfigureAwait(true);

            if (!ValorantProcessMonitor.IsValorantRunning())
            {
                DisposeStratsOverlay();
                stratsStatusLabel.Text = "Launch VALORANT first";
                stratsStatusLabel.BackColor = Color.FromArgb(110, 118, 136);
                return;
            }

            if (!stratsOverlayForm.IsOverlayVisible)
            {
                stratsStatusLabel.Text = "Strats ready";
                stratsStatusLabel.BackColor = Color.FromArgb(39, 145, 104);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            stratsStatusLabel.Text = "Strats preload failed";
            stratsStatusLabel.BackColor = Color.FromArgb(153, 99, 53);
        }
        finally
        {
            stratsPreloadInProgress = false;
        }
    }

    private async Task ToggleStratsOverlayWhenValorantRunningAsync()
    {
        if (stratsToggleInProgress)
        {
            return;
        }

        if (!ValorantProcessMonitor.IsValorantRunning())
        {
            stratsStatusLabel.Text = "Launch VALORANT first";
            stratsStatusLabel.BackColor = Color.FromArgb(110, 118, 136);
            return;
        }

        stratsToggleInProgress = true;
        try
        {
            if (stratsOverlayForm is null || stratsOverlayForm.IsDisposed)
            {
                stratsOverlayForm = new StratsOverlayForm();
            }

            if (stratsOverlayForm.IsOverlayVisible)
            {
                stratsOverlayForm.HideOverlayKeepingPage();
                stratsStatusLabel.Text = "Strats hidden";
                stratsStatusLabel.BackColor = Color.FromArgb(110, 118, 136);
                return;
            }

            Rectangle targetBounds = GetValorantTargetBounds();

            stratsStatusLabel.Text = stratsPreloadInProgress ? "Strats finishing" : "Strats loading";
            stratsStatusLabel.BackColor = Color.FromArgb(79, 118, 214);
            await stratsOverlayForm.BringOverlayToFrontAsync(targetBounds).ConfigureAwait(true);
            stratsStatusLabel.Text = "Strats visible";
            stratsStatusLabel.BackColor = Color.FromArgb(79, 118, 214);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            stratsStatusLabel.Text = "Strats failed";
            stratsStatusLabel.BackColor = Color.FromArgb(153, 99, 53);
            ShowError($"strats overlay を表示できませんでした。{exception.Message}");
        }
        finally
        {
            stratsToggleInProgress = false;
        }
    }

    private static Rectangle GetValorantTargetBounds()
    {
        return ValorantProcessMonitor.TryGetValorantWindowBounds(out Rectangle valorantBounds)
            ? valorantBounds
            : Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
    }

    private void ScheduleStratsOverlayToggleAfterHotKeyRelease()
    {
        stratsTogglePending = true;
        stratsToggleDelayTimer.Stop();
        stratsToggleDelayTimer.Start();
    }

    private void RunPendingStratsToggleAfterHotKeyRelease()
    {
        if (!stratsTogglePending)
        {
            stratsToggleDelayTimer.Stop();
            return;
        }

        bool altKeyDown = NativeMethods.IsKeyDown(NativeMethods.VirtualKeyMenu);
        bool tKeyDown = NativeMethods.IsKeyDown((int)NativeMethods.VirtualKeyT);
        if (altKeyDown || tKeyDown)
        {
            return;
        }

        stratsTogglePending = false;
        stratsToggleDelayTimer.Stop();
        ToggleStratsOverlayWhenValorantRunning();
    }

    private async Task HandleValorantStateChangeAsync(bool valorantDetected)
    {
        if (discordTransitionInProgress)
        {
            return;
        }

        discordTransitionInProgress = true;

        try
        {
            if (valorantDetected)
            {
                if (disableDiscordAutomation)
                {
                    RefreshDiscordStatusLabel();
                    return;
                }

                await discordBotVoiceRelay.StartForValorantAsync().ConfigureAwait(true);
            }
            else
            {
                await discordBotVoiceRelay.StopAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            discordTransitionInProgress = false;
            RefreshDiscordStatusLabel();
        }
    }

    private bool ShouldRetryDiscordStart(bool valorantDetected)
    {
        if (!valorantDetected || disableDiscordAutomation || discordTransitionInProgress)
        {
            return false;
        }

        if (discordBotVoiceRelay.IsRunning || !discordBotVoiceRelay.HasConfig)
        {
            return false;
        }

        return DateTimeOffset.UtcNow >= nextDiscordRetryAtUtc;
    }

    private void StartGitUpdateCheckIfNeeded(bool force)
    {
        if (gitUpdateCheckInProgress)
        {
            return;
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (!force && nowUtc < nextGitUpdateCheckAtUtc)
        {
            return;
        }

        _ = RunGitUpdateCheckAsync();
    }

    private async Task RunGitUpdateCheckAsync()
    {
        if (gitUpdateCheckInProgress)
        {
            return;
        }

        gitUpdateCheckInProgress = true;
        try
        {
            GitUpdateCheckResult updateResult = await gitUpdateChecker
                .CheckLatestReleaseAsync(CancellationToken.None)
                .ConfigureAwait(true);

            if (updateResult.HasUpdate)
            {
                WriteUpdateLog(
                    $"Update found. Current: {updateResult.CurrentVersion}. Latest: {updateResult.LatestVersion}. " +
                    $"Download: {updateResult.DownloadUri}.");

                GitAutoUpdateResult autoUpdateResult = await gitAutoUpdater
                    .DownloadAndStartInstallerAsync(updateResult, CancellationToken.None)
                    .ConfigureAwait(true);

                if (autoUpdateResult.StartedInstaller)
                {
                    WriteUpdateLog("Silent installer was started. Exiting current VALOWATCH process for replacement.");
                    BeginInvoke((MethodInvoker)(() =>
                    {
                        trayIcon.Visible = false;
                        Application.Exit();
                    }));
                    return;
                }

                WriteUpdateLog(
                    $"Auto update did not start. Status: {autoUpdateResult.Status}. Message: {autoUpdateResult.Message}.");
                return;
            }

            WriteUpdateLog($"Update check finished. Status: {updateResult.Status}. Message: {updateResult.Message}.");
        }
        finally
        {
            nextGitUpdateCheckAtUtc = DateTimeOffset.UtcNow.Add(GitUpdateCheckInterval);
            gitUpdateCheckInProgress = false;
        }
    }

    private void DisposeStratsOverlay()
    {
        if (stratsOverlayForm is null)
        {
            return;
        }

        try
        {
            stratsOverlayForm.Close();
        }
        finally
        {
            stratsOverlayForm.Dispose();
            stratsOverlayForm = null;
        }
    }

    private void RefreshDiscordStatusLabel()
    {
        discordStatusLabel.Text = discordBotVoiceRelay.StatusText;
        if (disableDiscordAutomation)
        {
            discordStatusLabel.Text = "Discord disabled";
            discordStatusLabel.BackColor = Color.FromArgb(110, 118, 136);
            return;
        }

        discordStatusLabel.BackColor = discordBotVoiceRelay.IsRunning
            ? Color.FromArgb(88, 101, 242)
            : Color.FromArgb(110, 118, 136);
    }

    private sealed record TeammateControls(TextBox RiotIdTextBox, Label StateLabel);
}
