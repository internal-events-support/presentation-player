using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PresentationPlayer;

/// <summary>
/// The app window: a native frame hosting the web UI (served by <see cref="ApiServer"/>)
/// in WebView2. Also owns the offscreen live player.
/// </summary>
public sealed class MainForm : Form
{
    private static readonly Color Background = Color.FromArgb(0x22, 0x22, 0x22);
    private static readonly Color TopBar = Color.FromArgb(0x38, 0x38, 0x38); // the UI's top bar
    private const int TopEdge = 4; // strip above the UI that resizes the window from the top

    private readonly PresentationController _controller;
    private readonly AppSettings _settings;
    private readonly ApiServer _api;
    private readonly AudioRouter _audio;
    private readonly GoogleAccount _google;
    private readonly DisplayOutputs _displays;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Background };
    private LivePresenter? _live;
    private SlideDrawer? _drawer;
    private bool _ownFrame = true; // the UI draws the title bar

    public MainForm(PresentationController controller, AppSettings settings, ApiServer api, AudioRouter audio, GoogleAccount google, DisplayOutputs displays)
    {
        _displays = displays;
        _google = google;
        _audio = audio;
        _controller = controller;
        _settings = settings;
        _api = api;

        Text = "Presentation Player";
        Icon = AppIcon.Create();
        BackColor = TopBar;
        Padding = new Padding(0, TopEdge, 0, 0);
        MinimumSize = new Size(940, 620);
        StartPosition = FormStartPosition.Manual;
        RestorePlacement();
        Controls.Add(_web);

        Shown += async (_, _) =>
        {
            _displays.AppWindow = Handle;
            _displays.Start();
            await StartAsync();
        };
        // Keyboard shortcuts and presentation clickers should work as soon as the window is active.
        Activated += (_, _) => _web.Focus();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DarkTitleBar.Apply(Handle, TopBar);
        UpdateFrame();
    }

    // ---- Title bar: the UI's top bar takes its place (drag regions and buttons are in app.css/app.js). ----

    protected override void WndProc(ref Message m)
    {
        const int WM_NCCALCSIZE = 0x83, WM_NCHITTEST = 0x84;
        const int HTNOWHERE = 0, HTCLIENT = 1, HTCAPTION = 2, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;

        if (_ownFrame && m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
        {
            // Keep Windows' side and bottom borders (resizing, shadow, snapping) but not the caption.
            var top = Marshal.ReadInt32(m.LParam, 4);
            base.WndProc(ref m);
            // A maximised window hangs over the screen edge by the width of its border.
            if (IsZoomed(Handle)) top += GetSystemMetricsForDpi(33, (uint)DeviceDpi) + GetSystemMetricsForDpi(92, (uint)DeviceDpi);
            Marshal.WriteInt32(m.LParam, 4, top);
            m.Result = IntPtr.Zero;
            return;
        }

        base.WndProc(ref m);

        if (_ownFrame && m.Msg == WM_NCHITTEST && (int)m.Result is HTNOWHERE or HTCLIENT or HTCAPTION && Padding.Top > 0)
        {
            var p = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
            if (p.Y < Padding.Top) m.Result = p.X < 12 ? HTTOPLEFT : p.X >= ClientSize.Width - 12 ? HTTOPRIGHT : HTTOP;
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!_ownFrame || WindowState == FormWindowState.Minimized) return;
        var top = WindowState == FormWindowState.Maximized ? 0 : TopEdge;
        if (Padding.Top != top) Padding = new Padding(0, top, 0, 0);
        ReportWindowState();
    }

    private void ReportWindowState()
    {
        if (_web.CoreWebView2 is { } core)
            core.PostWebMessageAsString(WindowState == FormWindowState.Maximized ? "window:maximized" : "window:normal");
    }

    /// <summary>Asks for a presentation file (or several).</summary>
    public Task<string[]> BrowseAsync(bool many)
    {
        var chosen = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginInvoke(() =>
        {
            using var dialog = new OpenFileDialog { Filter = LocalDecks.DialogFilter, Multiselect = many, Title = many ? "Add presentations" : "Open presentation" };
            chosen.TrySetResult(dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileNames : []);
        });
        return chosen.Task;
    }

    private void OnUiMessage(string message)
    {
        switch (message)
        {
            case "window:minimize": WindowState = FormWindowState.Minimized; break;
            case "window:maximize": WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; break;
            case "window:close": Close(); break;
            case "window:ready": ReportWindowState(); break;
        }
    }

    /// <summary>Goes back to the normal Windows title bar, for when the UI can't be shown.</summary>
    private void UseSystemFrame()
    {
        _ownFrame = false;
        Padding = Padding.Empty;
        UpdateFrame();
    }

    private void UpdateFrame() =>
        SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020); // no size/move/z-order/activate, frame changed

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    private async Task StartAsync()
    {
        try
        {
            var env = await WebViewEnvironment.GetAsync();
            await _web.EnsureCoreWebView2Async(env);
            ConfigureWebView(_web.CoreWebView2);
            _web.CoreWebView2.Navigate(_api.LocalUrl + "#token=" + _api.AppToken);
        }
        catch (Exception ex)
        {
            UseSystemFrame();
            MessageBox.Show(this,
                "The app window needs the Microsoft Edge WebView2 Runtime.\n\n" +
                "The controls will open in your web browser instead. Install WebView2 from microsoft.com/edge/webview2 to fix this.\n\n" + ex.Message,
                "WebView2 missing", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            OpenExternal(_api.LocalUrl);
        }

        try
        {
            await _google.InitializeAsync(this, await WebViewEnvironment.GetAsync());
        }
        catch
        {
            // Without WebView2 there's no Google sign-in; public decks still work.
        }

        SlideDrawer.Current = _drawer = new SlideDrawer(this);
        try
        {
            _live = await LivePresenter.CreateAsync(_controller.OutputSize.Width, _controller.OutputSize.Height);
            _controller.AttachLive(_live);
            _live.AudioCaptured += _audio.Feed;
        }
        catch (Exception ex)
        {
            _controller.SetLiveError("The live player couldn't start (" + ex.Message + "), so slides are static images.");
        }

        if (!string.IsNullOrWhiteSpace(_settings.LastUrl))
        {
            try { await _controller.LoadAsync(_settings.LastUrl); }
            catch { /* reported in the UI via the state's status */ }
        }
    }

    private void ConfigureWebView(CoreWebView2 core)
    {
        var s = core.Settings;
        s.AreBrowserAcceleratorKeysEnabled = false; // no F5 / Ctrl+R / Ctrl+P / Ctrl+F in an app
        s.IsZoomControlEnabled = false;
        s.IsPinchZoomEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsSwipeNavigationEnabled = false;
        s.AreDevToolsEnabled = Debugger.IsAttached;
        s.IsNonClientRegionSupportEnabled = true; // the top bar drags the window

        // Only text fields get a context menu (cut/copy/paste); no "Back / Reload / Inspect".
        core.ContextMenuRequested += (_, e) =>
        {
            if (!e.ContextMenuTarget.IsEditable) e.Handled = true;
        };

        var origin = new Uri(_api.LocalUrl).GetLeftPart(UriPartial.Authority);

        core.WebMessageReceived += (_, e) =>
        {
            if (!e.Source.StartsWith(origin, StringComparison.OrdinalIgnoreCase)) return;
            string message;
            try { message = e.TryGetWebMessageAsString(); }
            catch (ArgumentException) { return; } // not a string
            if (message == "drop")
            {
                // Files dropped on the window: only their names are sent to the page, so it's told where they are.
                var paths = e.AdditionalObjects.OfType<CoreWebView2File>().Select(f => f.Path).ToList();
                core.PostWebMessageAsString("files:" + System.Text.Json.JsonSerializer.Serialize(paths));
                return;
            }
            OnUiMessage(message);
        };
        core.NavigationCompleted += (_, e) =>
        {
            // Links opened in the browser instead are cancelled here, which isn't a failure.
            if (!e.IsSuccess && _ownFrame && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled) UseSystemFrame();
        };

        // Links such as "Edit in Slides" open in the default browser.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternal(e.Uri);
        };
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(origin, StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                OpenExternal(e.Uri);
            }
        };

        // The open dialog offers a Slides link found on the clipboard.
        core.PermissionRequested += (_, e) =>
        {
            if (e.PermissionKind == CoreWebView2PermissionKind.ClipboardRead &&
                e.Uri.StartsWith(origin, StringComparison.OrdinalIgnoreCase))
                e.State = CoreWebView2PermissionState.Allow;
        };
    }

    private static void OpenExternal(string url)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private void RestorePlacement()
    {
        var p = _settings.Window;
        var bounds = p == null ? Rectangle.Empty : new Rectangle(p.X, p.Y, p.Width, p.Height);
        if (p != null && Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(bounds)) && p.Width >= 600 && p.Height >= 400)
        {
            Bounds = bounds;
            if (p.Maximized) WindowState = FormWindowState.Maximized;
        }
        else
        {
            var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1600, 900);
            var size = new Size(Math.Min(1440, area.Width - 80), Math.Min(900, area.Height - 80));
            Bounds = new Rectangle(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2, size.Width, size.Height);
        }
    }

    /// <summary>The app was closed because Windows is shutting down or signing out.</summary>
    public bool WindowsEnding { get; private set; }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        WindowsEnding = e.CloseReason == CloseReason.WindowsShutDown;
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _settings.Window = new WindowPlacement
        {
            X = b.X, Y = b.Y, Width = b.Width, Height = b.Height,
            Maximized = WindowState == FormWindowState.Maximized,
        };
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        SlideDrawer.Current = null;
        _drawer?.Dispose();
        _live?.Dispose();
        _web.Dispose();
        base.OnFormClosed(e);
    }

    /// <summary>Win11 dark caption that matches the app background (falls back to plain dark mode on Win10).</summary>
    internal static class DarkTitleBar
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static void Apply(IntPtr hwnd, Color caption)
        {
            int on = 1;
            DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));           // DWMWA_USE_IMMERSIVE_DARK_MODE
            int color = caption.R | (caption.G << 8) | (caption.B << 16);   // COLORREF
            DwmSetWindowAttribute(hwnd, 35, ref color, sizeof(int));        // DWMWA_CAPTION_COLOR
            int text = 0xE8E8EE;
            DwmSetWindowAttribute(hwnd, 36, ref text, sizeof(int));         // DWMWA_TEXT_COLOR
        }
    }
}

/// <summary>Draws the app icon (matches wwwroot/icon.svg).</summary>
internal static class AppIcon
{
    public static Icon Create()
    {
        using var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundedRect(new RectangleF(0, 0, 64, 64), 15);
            using var bg = new LinearGradientBrush(new Point(0, 0), new Point(64, 64), Color.FromArgb(0x7a, 0xb4, 0xf7), Color.FromArgb(0x0e, 0x72, 0xed));
            g.FillPath(bg, path);
            using var pen = new Pen(Color.White, 3.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var screen = RoundedRect(new RectangleF(13, 16, 38, 25), 4);
            g.DrawPath(pen, screen);
            g.FillPolygon(Brushes.White, new PointF[] { new(28, 23.5f), new(28, 33.5f), new(36.5f, 28.5f) });
            g.DrawLine(pen, 24, 48, 40, 48);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
