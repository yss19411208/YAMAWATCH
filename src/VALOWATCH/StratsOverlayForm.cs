using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace VALOWATCH;

public sealed class StratsOverlayForm : Form
{
    private static readonly Uri StratsLineupsUri = new("https://strats.gg/valorant/lineups");

    private readonly WebView2 webView = new();
    private readonly Label errorLabel = new();
    private readonly System.Windows.Forms.Timer topMostPulseTimer = new();

    private Task? webViewInitializationTask;
    private Rectangle lastOverlayBounds;
    private IntPtr returnFocusWindowHandle;
    private int topMostPulseCountRemaining;
    private bool navigationFailed;

    public StratsOverlayForm()
    {
        BuildInterface();
        topMostPulseTimer.Interval = 80;
        topMostPulseTimer.Tick += (_, _) => PulseTopMostPosition();
    }

    public bool IsOverlayVisible { get; private set; }

    public async Task PreloadAsync(Rectangle targetBounds)
    {
        ApplyTargetBounds(targetBounds);
        lastOverlayBounds = Bounds;
        await EnsureWebViewReadyAsync().ConfigureAwait(true);
    }

    public async Task BringOverlayToFrontAsync(Rectangle targetBounds, IntPtr valorantWindowHandle)
    {
        ApplyTargetBounds(targetBounds);
        lastOverlayBounds = Bounds;
        returnFocusWindowHandle = valorantWindowHandle;

        await EnsureWebViewReadyAsync().ConfigureAwait(true);
        if (webView.CoreWebView2 is not null)
        {
            if (navigationFailed)
            {
                webView.CoreWebView2.Reload();
            }
            else
            {
                errorLabel.Visible = false;
                webView.Visible = true;
                webView.BringToFront();
            }
        }

        Show();
        IsOverlayVisible = true;
        SetTopMostWithoutActivation(lastOverlayBounds);
        BringToFront();
        FocusOverlayForInteraction();
        StartTopMostPulse();
    }

    public void HideOverlayKeepingPage()
    {
        topMostPulseTimer.Stop();
        Hide();
        IsOverlayVisible = false;
        if (returnFocusWindowHandle != IntPtr.Zero && NativeMethods.IsWindow(returnFocusWindowHandle))
        {
            NativeMethods.ShowWindow(returnFocusWindowHandle, NativeMethods.SwShow);
            NativeMethods.SetForegroundWindow(returnFocusWindowHandle);
        }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams createParams = base.CreateParams;
            createParams.ExStyle |= NativeMethods.WsExToolWindow | NativeMethods.WsExTopMost;
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

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs eventArgs)
    {
        if (!eventArgs.IsSuccess)
        {
            navigationFailed = true;
            ShowError("Failed to load strats.gg");
            return;
        }

        navigationFailed = false;
        errorLabel.Visible = false;
        webView.Visible = true;
        webView.BringToFront();
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
        if (topMostPulseCountRemaining >= 20 &&
            NativeMethods.GetAncestor(NativeMethods.GetForegroundWindow(), NativeMethods.GaRoot) != Handle)
        {
            FocusOverlayForInteraction();
        }
    }

    private void FocusOverlayForInteraction()
    {
        IntPtr foregroundWindow = NativeMethods.GetForegroundWindow();
        uint currentThreadId = NativeMethods.GetCurrentThreadId();
        uint foregroundThreadId = foregroundWindow == IntPtr.Zero
            ? 0
            : NativeMethods.GetWindowThreadProcessId(foregroundWindow, out _);
        bool attached = foregroundThreadId != 0 &&
            foregroundThreadId != currentThreadId &&
            NativeMethods.AttachThreadInput(currentThreadId, foregroundThreadId, attach: true);

        try
        {
            NativeMethods.BringWindowToTop(Handle);
            Activate();
            NativeMethods.SetForegroundWindow(Handle);
            NativeMethods.SetFocus(Handle);
            if (webView.Visible)
            {
                webView.Focus();
            }
        }
        finally
        {
            if (attached)
            {
                NativeMethods.AttachThreadInput(currentThreadId, foregroundThreadId, attach: false);
            }
        }
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
