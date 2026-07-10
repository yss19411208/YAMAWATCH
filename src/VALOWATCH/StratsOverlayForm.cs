using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VALOWATCH;

public sealed class StratsOverlayForm : Form
{
    private static readonly Uri StratsLineupsUri = new("https://strats.gg/valorant/lineups");

    private readonly WebView2 webView = new();
    private readonly Label errorLabel = new();
    private readonly System.Windows.Forms.Timer topMostPulseTimer = new();

    private Task? webViewInitializationTask;
    private Rectangle lastOverlayBounds;
    private int topMostPulseCountRemaining;

    public StratsOverlayForm()
    {
        BuildInterface();
        topMostPulseTimer.Interval = 80;
        topMostPulseTimer.Tick += (_, _) => PulseTopMostPosition();
    }

    public bool IsOverlayVisible { get; private set; }

    public async Task BringOverlayToFrontAsync(Rectangle targetBounds)
    {
        ApplyTargetBounds(targetBounds);
        lastOverlayBounds = Bounds;

        await EnsureWebViewReadyAsync().ConfigureAwait(true);
        ResumeWebViewIfSuspended();
        webView.Visible = true;

        NativeMethods.ShowWindow(Handle, NativeMethods.SwShownoactivate);
        IsOverlayVisible = true;
        SetTopMostWithoutActivation(lastOverlayBounds);
        StartTopMostPulse();
    }

    public void HideOverlayKeepingPage()
    {
        topMostPulseTimer.Stop();
        webView.Visible = false;
        Hide();
        IsOverlayVisible = false;
        _ = SuspendWebViewAsync();
    }

    public long GetApproximateWebViewPrivateMemoryBytes()
    {
        CoreWebView2? coreWebView = webView.CoreWebView2;
        if (coreWebView is null)
        {
            return 0;
        }

        long totalPrivateBytes = 0;
        try
        {
            foreach (CoreWebView2ProcessInfo processInfo in coreWebView.Environment.GetProcessInfos())
            {
                try
                {
                    using Process webViewProcess = Process.GetProcessById(processInfo.ProcessId);
                    totalPrivateBytes += webViewProcess.PrivateMemorySize64;
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException)
        {
            return 0;
        }

        return totalPrivateBytes;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams createParams = base.CreateParams;
            createParams.ExStyle |= NativeMethods.WsExToolWindow
                | NativeMethods.WsExTopMost
                | NativeMethods.WsExNoActivate;
            return createParams;
        }
    }

    private void BuildInterface()
    {
        Text = "VALOWATCH Strats Overlay";
        StartPosition = FormStartPosition.Manual;
        Size = new Size(1180, 760);
        MinimumSize = new Size(760, 480);
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(12, 14, 19);
        ForeColor = Color.White;
        Opacity = 0.92;
        KeyPreview = true;

        Rectangle workingArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(
            workingArea.Left + Math.Max(0, (workingArea.Width - Width) / 2),
            workingArea.Top + Math.Max(0, (workingArea.Height - Height) / 2));

        webView.Dock = DockStyle.Fill;
        webView.DefaultBackgroundColor = Color.FromArgb(12, 14, 19);
        webView.Visible = false;
        Controls.Add(webView);

        errorLabel.Dock = DockStyle.Fill;
        errorLabel.Visible = false;
        errorLabel.TextAlign = ContentAlignment.MiddleCenter;
        errorLabel.ForeColor = Color.FromArgb(255, 205, 112);
        errorLabel.BackColor = Color.FromArgb(12, 14, 19);
        Controls.Add(errorLabel);
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            HideOverlayKeepingPage();
            return true;
        }

        return base.ProcessCmdKey(ref message, keyData);
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            await webView.EnsureCoreWebView2Async();
            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            webView.Source = StratsLineupsUri;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowError("WebView2 Runtime is not installed.");
        }
        catch (InvalidOperationException exception)
        {
            ShowError(exception.Message);
        }
    }

    private Task EnsureWebViewReadyAsync()
    {
        if (webView.CoreWebView2 is not null)
        {
            return Task.CompletedTask;
        }

        webViewInitializationTask ??= InitializeWebViewAsync();
        return webViewInitializationTask;
    }

    private void ResumeWebViewIfSuspended()
    {
        CoreWebView2? coreWebView = webView.CoreWebView2;
        if (coreWebView is null || !coreWebView.IsSuspended)
        {
            return;
        }

        coreWebView.Resume();
    }

    private async Task SuspendWebViewAsync()
    {
        CoreWebView2? coreWebView = webView.CoreWebView2;
        if (coreWebView is null || coreWebView.IsSuspended || IsOverlayVisible)
        {
            return;
        }

        try
        {
            await coreWebView.TrySuspendAsync().ConfigureAwait(true);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // WebView2 only allows suspension once the controller is invisible.
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs eventArgs)
    {
        if (!eventArgs.IsSuccess)
        {
            ShowError("Failed to load strats.gg");
        }
    }

    private void ShowError(string message)
    {
        webView.Visible = false;
        errorLabel.Text = message;
        errorLabel.Visible = true;
        errorLabel.BringToFront();
    }

    private void ApplyTargetBounds(Rectangle targetBounds)
    {
        Rectangle safeBounds = targetBounds.Width > 0 && targetBounds.Height > 0
            ? targetBounds
            : Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);

        int overlayWidth = Math.Clamp((int)(safeBounds.Width * 0.86), 760, Math.Max(760, safeBounds.Width));
        int overlayHeight = Math.Clamp((int)(safeBounds.Height * 0.86), 480, Math.Max(480, safeBounds.Height));
        int overlayLeft = safeBounds.Left + (safeBounds.Width - overlayWidth) / 2;
        int overlayTop = safeBounds.Top + (safeBounds.Height - overlayHeight) / 2;

        Bounds = new Rectangle(overlayLeft, overlayTop, overlayWidth, overlayHeight);
    }

    private void StartTopMostPulse()
    {
        topMostPulseCountRemaining = 25;
        topMostPulseTimer.Stop();
        topMostPulseTimer.Start();
    }

    private void PulseTopMostPosition()
    {
        if (!IsOverlayVisible)
        {
            topMostPulseTimer.Stop();
            return;
        }

        if (topMostPulseCountRemaining <= 0)
        {
            topMostPulseTimer.Stop();
            return;
        }

        topMostPulseCountRemaining--;
        SetTopMostWithoutActivation(lastOverlayBounds);
    }

    private void SetTopMostWithoutActivation(Rectangle overlayBounds)
    {
        NativeMethods.SetWindowPos(
            Handle,
            NativeMethods.HwndTopMost,
            overlayBounds.Left,
            overlayBounds.Top,
            overlayBounds.Width,
            overlayBounds.Height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
    }
}
