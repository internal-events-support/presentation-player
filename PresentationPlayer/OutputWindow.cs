using System.Runtime.InteropServices;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using Color = System.Drawing.Color;
using Size = System.Drawing.Size;

namespace PresentationPlayer;

/// <summary>
/// Shows an output on a display: in a window, or filling a display with no border. It has its own
/// thread, so nothing the app does holds it up.
/// </summary>
internal sealed class OutputWindow
{
    private readonly Thread _thread;
    private readonly TaskCompletionSource<View?> _view = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private byte[]? _latest;

    /// <param name="activate">Brings it to the front and gives it the keyboard.</param>
    /// <param name="closed">The window was closed by the user (Esc, double-click or its close button).</param>
    /// <param name="moved">Where the window is, after the user moved or resized it.</param>
    /// <param name="key">A key pressed in it; returns whether it did anything.</param>
    public OutputWindow(string title, Size frame, WindowPlacement? placement, DisplayInfo? display, bool activate,
        Action<OutputWindow> closed, Action<WindowPlacement> moved, Func<Keys, bool> key)
    {
        Display = display;
        _thread = new Thread(() =>
        {
            View view;
            try
            {
                view = new View(title, frame, () => Volatile.Read(ref _latest), placement, display, activate, () => closed(this), moved, key);
                _ = view.Handle;
            }
            catch (Exception)
            {
                _view.TrySetResult(null);
                return;
            }
            _view.TrySetResult(view);
            // Nobody touches the keyboard for long stretches of a show: keep the display from going to sleep.
            SetThreadExecutionState(0x80000003); // continuous, display and system required
            Application.Run(view);
            SetThreadExecutionState(0x80000000);
        }) { IsBackground = true, Name = title };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>The display it fills, or null for a window.</summary>
    public DisplayInfo? Display { get; private set; }

    public void SetFrame(byte[] bgra)
    {
        Volatile.Write(ref _latest, bgra);
        // Before it's ready there's nothing to tell; it draws the newest frame when it opens.
        if (_view.Task is { IsCompletedSuccessfully: true, Result: { } view }) view.FrameReady();
    }

    /// <summary>The display it fills has moved or changed resolution.</summary>
    public void Cover(DisplayInfo display)
    {
        Display = display;
        Post(v => v.Bounds = display.Bounds);
    }

    public void Close() => Post(v => v.CloseQuietly());

    /// <summary>Waits for it to be gone, when the app is closing.</summary>
    public void WaitClosed() => _thread.Join(1000);

    private void Post(Action<View> action) => _ = _view.Task.ContinueWith(t =>
    {
        try { t.Result?.BeginInvoke(() => action(t.Result)); }
        catch (InvalidOperationException) { /* already closed */ }
    }, TaskScheduler.Default);

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    private sealed class View : Form
    {
        private const int WM_FRAME = 0x8001;
        private const int WM_ERASEBKGND = 0x14, WM_PAINT = 0x0F, WM_DISPLAYCHANGE = 0x7E;

        private readonly Size _frame;
        private readonly Func<byte[]?> _latest;
        private readonly DisplayInfo? _display;
        private readonly bool _activate;
        private readonly Action _closed;
        private readonly Action<WindowPlacement> _moved;
        private readonly Func<Keys, bool> _key;
        private readonly System.Windows.Forms.Timer _cursorTimer = new() { Interval = 2000 };
        private Point? _lastMouse;
        private bool _cursorHidden;
        private bool _quiet;
        private IntPtr _handle;
        private int _queued;
        private byte[]? _shown;

        private ID2D1Factory? _factory;
        private ID2D1HwndRenderTarget? _target;
        private ID2D1Bitmap? _bitmap;
        private bool _bitmapStale;

        public View(string title, Size frame, Func<byte[]?> latest, WindowPlacement? placement, DisplayInfo? display, bool activate,
            Action closed, Action<WindowPlacement> moved, Func<Keys, bool> key)
        {
            _latest = latest;
            (_frame, _display, _activate, _closed, _moved, _key) = (frame, display, activate, closed, moved, key);
            Text = title;
            Icon = AppIcon.Create();
            BackColor = Color.Black;
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.Manual;
            SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);

            if (display != null)
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                Bounds = display.Bounds;
                _cursorTimer.Tick += (_, _) => HideCursor();
                _cursorTimer.Start();
            }
            else
            {
                MinimumSize = new Size(320, 220);
                Place(placement);
            }
        }

        protected override bool ShowWithoutActivation => !_activate;

        protected override CreateParams CreateParams
        {
            get
            {
                var p = base.CreateParams;
                if (_display != null) p.ExStyle |= 0x8 | 0x80; // above other windows, and not in Alt+Tab
                return p;
            }
        }

        private void Place(WindowPlacement? p)
        {
            var bounds = p == null ? Rectangle.Empty : new Rectangle(p.X, p.Y, p.Width, p.Height);
            if (p != null && p.Width >= 200 && p.Height >= 150 && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds)))
            {
                Bounds = bounds;
                if (p.Maximized) WindowState = FormWindowState.Maximized;
                return;
            }
            var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1600, 900);
            var width = Math.Min(960, area.Width / 2);
            ClientSize = new Size(width, width * _frame.Height / _frame.Width);
            Location = new Point(area.X + (area.Width - Width) / 2, area.Y + (area.Height - Height) / 2);
        }

        public void FrameReady()
        {
            if (_handle != IntPtr.Zero && Interlocked.Exchange(ref _queued, 1) == 0 && !PostMessage(_handle, WM_FRAME, IntPtr.Zero, IntPtr.Zero))
                Volatile.Write(ref _queued, 0);
        }

        public void CloseQuietly()
        {
            _quiet = true;
            Close();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _handle = Handle;
            Volatile.Write(ref _queued, 0);
            DropTarget();
            if (_display == null) MainForm.DarkTitleBar.Apply(Handle, Color.FromArgb(0x38, 0x38, 0x38));
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // Covering the display the app is on: say how to get back to it.
            if (_display != null && _activate) Hint("Press Esc or double-click to leave full screen");
            if (_activate) Activate();
        }

        private void Hint(string text)
        {
            var label = new Label
            {
                Text = text, AutoSize = true, BackColor = Color.FromArgb(0x2c, 0x2c, 0x2e), ForeColor = Color.White,
                Font = new Font("Segoe UI", 13), Padding = new Padding(16, 10, 16, 10),
            };
            Controls.Add(label);
            label.Location = new Point((ClientSize.Width - label.Width) / 2, ClientSize.Height - label.Height - 48);
            label.DoubleClick += (_, _) => Close();
            var timer = new System.Windows.Forms.Timer { Interval = 4000 };
            timer.Tick += (_, _) => { timer.Dispose(); Controls.Remove(label); label.Dispose(); Invalidate(); };
            timer.Start();
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_FRAME:
                    Volatile.Write(ref _queued, 0);
                    Draw();
                    return;
                case WM_ERASEBKGND:
                    m.Result = 1;
                    return;
                case WM_PAINT:
                    Draw();
                    ValidateRect(Handle, IntPtr.Zero);
                    return;
            }
            base.WndProc(ref m);
            if (m.Msg == WM_DISPLAYCHANGE) Invalidate();
        }

        private void Draw()
        {
            if (_latest() is { } newest && newest != _shown)
            {
                _shown = newest;
                _bitmapStale = true;
            }
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0 || WindowState == FormWindowState.Minimized) return;
            try
            {
                if (_target == null) CreateTarget();
                var size = new SizeI(ClientSize.Width, ClientSize.Height);
                if (_target!.PixelSize != size) _target.Resize(size);
                if (_bitmapStale && _shown != null)
                {
                    _bitmap!.CopyFromMemory(_shown, (uint)_frame.Width * 4);
                    _bitmapStale = false;
                }
                _target.BeginDraw();
                _target.Clear(new Color4(0, 0, 0, 1));
                if (_shown != null)
                {
                    // Keeps the picture's shape; what's left over is black.
                    var scale = Math.Min((float)size.Width / _frame.Width, (float)size.Height / _frame.Height);
                    var (w, h) = (_frame.Width * scale, _frame.Height * scale);
                    var dest = new Vortice.RawRectF((size.Width - w) / 2, (size.Height - h) / 2, (size.Width + w) / 2, (size.Height + h) / 2);
                    _target.DrawBitmap(_bitmap!, dest, 1, BitmapInterpolationMode.Linear, null);
                }
                if (_target.EndDraw().Failure) StartAgain();
            }
            catch (SharpGen.Runtime.SharpGenException)
            {
                StartAgain();
            }
        }

        private void CreateTarget()
        {
            _factory ??= D2D1.D2D1CreateFactory<ID2D1Factory>(FactoryType.SingleThreaded);
            var format = new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore);
            _target = _factory.CreateHwndRenderTarget(
                new RenderTargetProperties(format) { DpiX = 96, DpiY = 96 },
                new HwndRenderTargetProperties { Hwnd = Handle, PixelSize = new SizeI(ClientSize.Width, ClientSize.Height), PresentOptions = PresentOptions.None });
            _bitmap = _target.CreateBitmap(new SizeI(_frame.Width, _frame.Height), IntPtr.Zero, 0, new BitmapProperties(format, 96, 96));
            _bitmapStale = true;
        }

        private void DropTarget()
        {
            _bitmap?.Dispose();
            _target?.Dispose();
            (_bitmap, _target) = (null, null);
        }

        /// <summary>The graphics device went away (a driver update or display change).</summary>
        private void StartAgain()
        {
            DropTarget();
            var retry = new System.Windows.Forms.Timer { Interval = 200 };
            retry.Tick += (_, _) => { retry.Dispose(); if (!IsDisposed) Invalidate(); };
            retry.Start();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (IsHandleCreated) Invalidate();
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            ReportPlacement();
        }

        private void ReportPlacement()
        {
            if (_display != null) return;
            var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            _moved(new WindowPlacement { X = b.X, Y = b.Y, Width = b.Width, Height = b.Height, Maximized = WindowState == FormWindowState.Maximized });
        }

        // Arrow keys and Tab are for moving between controls unless they're claimed here.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape && _display != null)
            {
                Close();
                return true;
            }
            return _key(keyData) || base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (_display != null) Close();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_display == null || e.Location == _lastMouse) return; // Windows also sends moves when nothing moved
            var first = _lastMouse == null;
            _lastMouse = e.Location;
            if (first) return;
            ShowCursor();
            _cursorTimer.Stop();
            _cursorTimer.Start();
        }

        private void HideCursor()
        {
            if (_cursorHidden || !ClientRectangle.Contains(PointToClient(MousePosition))) return;
            _cursorHidden = true;
            Cursor.Hide();
        }

        private void ShowCursor()
        {
            if (_cursorHidden) Cursor.Show();
            _cursorHidden = false;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            ShowCursor();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            ReportPlacement();
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _handle = IntPtr.Zero;
            _cursorTimer.Dispose();
            DropTarget();
            _factory?.Dispose();
            ShowCursor();
            base.OnFormClosed(e);
            if (!_quiet) _closed();
        }

        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern bool ValidateRect(IntPtr hwnd, IntPtr rect);
    }
}
