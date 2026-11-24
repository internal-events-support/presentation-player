using Microsoft.Win32;

namespace PresentationPlayer;

public enum OutputKind { Slides, Notes }

/// <summary>Where an output is shown on this computer, besides NDI.</summary>
public sealed class OutputPlacement
{
    /// <summary>"off", "window" or "display".</summary>
    public string Mode { get; set; } = "off";
    /// <summary>The display it fills, and its name for when it isn't connected.</summary>
    public string Display { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public WindowPlacement? Window { get; set; }
}

/// <summary>An output's placement; not connected when the display it fills isn't there.</summary>
public sealed record OutputStatus(string Mode, string Display, string DisplayName, bool Connected);

/// <summary>Shows the slides and the presenter view on this computer's displays, in windows or full screen.</summary>
public sealed class DisplayOutputs : IDisposable
{
    private readonly object _lock = new();
    private readonly PresentationController _controller;
    private readonly AppSettings _settings;
    private readonly Dictionary<OutputKind, OutputWindow> _open = [];
    private readonly System.Threading.Timer _recheck;
    private IReadOnlyList<DisplayInfo> _displays;
    private bool _started;
    private bool _disposed;

    /// <summary>The app's own window, to tell which display it's on.</summary>
    public IntPtr AppWindow { get; set; }

    public DisplayOutputs(PresentationController controller, AppSettings settings)
    {
        _controller = controller;
        _settings = settings;
        _recheck = new System.Threading.Timer(_ => Apply(), null, Timeout.Infinite, Timeout.Infinite);
        _displays = Displays.List();
        controller.Displays = this;
        controller.FrameChanged += (kind, bgra) =>
        {
            OutputWindow? window;
            lock (_open) _open.TryGetValue(kind, out window);
            window?.SetFrame(bgra);
        };
        SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
    }

    /// <summary>Opens what was open last time.</summary>
    public void Start()
    {
        _started = true;
        Apply();
    }

    public IReadOnlyList<DisplayInfo> List()
    {
        var app = AppWindow == IntPtr.Zero ? null : Displays.PositionOf(AppWindow);
        return Volatile.Read(ref _displays).Select(d => d with { App = app is { } p && p.X == d.X && p.Y == d.Y }).ToList();
    }

    public OutputStatus Status(OutputKind kind)
    {
        var p = Placement(kind);
        if (p.Mode != "display") return new OutputStatus(p.Mode, "", "", true);
        var display = Find(p.Display);
        return new OutputStatus(p.Mode, p.Display, display?.Name ?? p.DisplayName, display != null);
    }

    /// <summary>Moves an output. Returns what's wrong with it, if anything.</summary>
    public string? Set(OutputKind kind, string mode, string? displayId)
    {
        lock (_lock)
        {
            var p = Placement(kind);
            switch (mode)
            {
                case "off" or "window":
                    p.Mode = mode;
                    break;
                case "display":
                    if (Find(displayId) is not { } display) return "That display isn't connected.";
                    // A display shows one output: if the other one is there, they swap.
                    var other = Placement(kind == OutputKind.Slides ? OutputKind.Notes : OutputKind.Slides);
                    if (other.Mode == "display" && other.Display == display.Id && !(p.Mode == "display" && p.Display == display.Id))
                    {
                        if (p.Mode == "display") (other.Display, other.DisplayName) = (p.Display, p.DisplayName);
                        else other.Mode = "off";
                    }
                    (p.Mode, p.Display, p.DisplayName) = ("display", display.Id, display.Name);
                    break;
                default:
                    return "An output is off, in a window or on a display.";
            }
        }
        _settings.Save();
        Apply();
        return null;
    }

    private DisplayInfo? Find(string? id) => Volatile.Read(ref _displays).FirstOrDefault(d => d.Id == id);

    private OutputPlacement Placement(OutputKind kind) => kind == OutputKind.Slides ? _settings.SlidesOutput : _settings.NotesOutput;

    // Plugging a display in or out sends several of these while Windows rearranges things.
    private void OnDisplaysChanged(object? sender, EventArgs e)
    {
        try { _recheck.Change(400, Timeout.Infinite); }
        catch (ObjectDisposedException) { /* closing */ }
    }

    /// <summary>Makes what's open match the settings and the displays that are there.</summary>
    private void Apply()
    {
        lock (_lock)
        {
            if (_disposed) return;
            Volatile.Write(ref _displays, Displays.List());
            if (_started) foreach (var kind in Enum.GetValues<OutputKind>()) ApplyLocked(kind);
        }
        _controller.NotifyChanged();
    }

    private void ApplyLocked(OutputKind kind)
    {
        var p = Placement(kind);
        var display = p.Mode == "display" ? Find(p.Display) : null;
        var wanted = p.Mode == "window" || display != null;
        OutputWindow? open;
        lock (_open) _open.TryGetValue(kind, out open);
        if (open != null && wanted && open.Display?.Id == display?.Id)
        {
            if (display != null && open.Display!.Bounds != display.Bounds) open.Cover(display);
            return;
        }
        if (open != null)
        {
            lock (_open) _open.Remove(kind);
            open.Close();
        }
        if (!wanted) return;

        var coversApp = display != null && List().Any(d => d.App && d.Id == display.Id);
        var window = new OutputWindow(
            (kind == OutputKind.Slides ? "Slides" : "Presenter view") + " - Presentation Player",
            _controller.OutputSize, p.Window, display, activate: display == null || coversApp,
            closed: w => Task.Run(() => OnClosed(kind, w)),
            moved: w => { p.Window = w; _settings.Save(); },
            key: OnKey);
        lock (_open) _open[kind] = window;
        _controller.SendFrame(kind, window.SetFrame);
    }

    // Closed by the user, so it stays off.
    private void OnClosed(OutputKind kind, OutputWindow window)
    {
        lock (_lock)
        {
            lock (_open)
            {
                if (!_open.TryGetValue(kind, out var open) || open != window) return;
                _open.Remove(kind);
            }
            Placement(kind).Mode = "off";
        }
        _settings.Save();
        _controller.NotifyChanged();
    }

    /// <summary>The app's keys work in the output windows too, so it doesn't matter which is in front when using a clicker.</summary>
    private bool OnKey(Keys key)
    {
        switch (key)
        {
            case Keys.Right or Keys.Space or Keys.PageDown: _controller.Next(); return true;
            case Keys.Left or Keys.PageUp: _controller.Previous(); return true;
            case Keys.Home: _controller.GoTo(1); return true;
            case Keys.End: _controller.GoTo(_controller.GetState().SlideCount); return true;
            case Keys.Up: _controller.ScrollNotesUp(); return true;
            case Keys.Down: _controller.ScrollNotesDown(); return true;
            case Keys.P: _controller.ControlVideo(VideoCommand.Toggle); return true;
            case Keys.P | Keys.Shift: _controller.ControlVideo(VideoCommand.Restart); return true;
            default: return false;
        }
    }

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
        List<OutputWindow> open;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _recheck.Dispose();
            lock (_open)
            {
                open = [.. _open.Values];
                _open.Clear();
            }
        }
        foreach (var w in open) w.Close();
        foreach (var w in open) w.WaitClosed(); // each notes where its window was
    }
}
