using System.Text.Json;

namespace GoogleSlidesNdiPresenter;

/// <summary>A slide as the live player needs to know it.</summary>
public sealed record LivePage(string PageId, int PlayerNumber, int ClickSteps, bool StartsByItself);

/// <summary>
/// One deck of a master deck. Pages are the slides that are shown; AllPages are all the deck's slides
/// (used to find somewhere quiet to wait). Trimmed says whether slides are left out at either end.
/// </summary>
public sealed record LivePart(string DeckId, IReadOnlyList<LivePage> Pages, IReadOnlyList<LivePage> AllPages,
    bool TrimmedStart, bool TrimmedEnd, string MediaJson);

/// <summary>
/// The live slide output. A single deck plays in one <see cref="DeckPlayer"/>. A master deck keeps
/// the previous and next decks loaded in hidden players, so moving between decks is a straight cut
/// with nothing loading on screen. All public methods are thread-safe; events are raised on the UI thread.
/// </summary>
public sealed class LivePresenter : IDisposable
{
    private sealed class Slot
    {
        public required int Part { get; set; }
        public DeckPlayer? Player { get; set; }
        public required LivePage Page { get; set; } // where it's been sent
    }

    private readonly int _width;
    private readonly int _height;
    private readonly SynchronizationContext _ui;
    private DeckPlayer _active;
    private bool _disposed;

    // Master deck (null while a single deck is open).
    private IReadOnlyList<LivePart>? _parts;
    private readonly List<Slot> _slots = [];
    private int _part;
    private int _generation;
    private (int Part, LivePage Page, long Since)? _pending; // the deck being switched to
    private bool _holdFrames;

    /// <summary>(1-based slide, slide count, slide id). For a master deck the numbers count through all of it.</summary>
    public event Action<int, int, string?>? SlideChanged;
    public event Action<LiveFrame>? FrameCaptured;
    public event Action<VideoInfo?>? VideoChanged;
    public event Action<byte[], int>? AudioCaptured;

    private LivePresenter(DeckPlayer player, int width, int height)
    {
        _active = player;
        _width = width;
        _height = height;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        Hook(player);
    }

    /// <summary>Creates the player. Must be called on the UI thread.</summary>
    public static async Task<LivePresenter> CreateAsync(int width, int height) =>
        new(await DeckPlayer.CreateAsync(width, height), width, height);

    private void OnUi(Action action) => _ui.Post(_ => { if (!_disposed) action(); }, null);

    private void Hook(DeckPlayer p)
    {
        p.SlideChanged += (slide, count, param) => OnSlide(p, slide, count, param);
        p.FrameCaptured += f => { if (p == _active && !_holdFrames) FrameCaptured?.Invoke(f); };
        p.VideoChanged += v => { if (p == _active) VideoChanged?.Invoke(v); };
        p.AudioCaptured += (data, rate) => { if (p == _active) AudioCaptured?.Invoke(data, rate); };
        p.Edge += direction => OnEdge(p, direction);
    }

    // ------------------------------------------------------------------ Single deck

    public void Load(string presentationId, string? slideParam) => OnUi(() =>
    {
        LeaveMaster();
        _active.SetRange(null);
        _active.Load(presentationId, slideParam);
    });

    public void SetMedia(IReadOnlyList<SlideMedia> media) => OnUi(() => { if (_parts == null) _active.SetMedia(DeckPlayer.MediaJson(media)); });

    /// <summary>Stops the player (used when the presentation is closed).</summary>
    public void Unload() => OnUi(() =>
    {
        LeaveMaster();
        _active.SetRange(null);
        _active.Unload();
    });

    public void Next() => OnUi(() => Step(+1));
    public void Previous() => OnUi(() => Step(-1));

    /// <summary>Goes to a slide: its number in the player, or in the whole master deck.</summary>
    public void GoTo(int slide) => OnUi(() =>
    {
        if (_parts == null) { _active.GoTo(slide); return; }
        if (Find(slide) is var (part, page)) Show(part, page);
    });

    public void Video(VideoCommand command) => OnUi(() => _active.Video(command));

    private void Step(int direction)
    {
        if (_pending is var (part, page, since))
        {
            // Still switching decks. If a deck seems stuck, try loading it again.
            if (Environment.TickCount64 - since < 8000) return;
            _pending = (part, page, Environment.TickCount64);
            _slots.FirstOrDefault(s => s.Part == part)?.Player?.Load(_parts![part].DeckId, "id." + page.PageId);
            return;
        }
        if (direction > 0) _active.Next();
        else _active.Previous();
    }

    // ------------------------------------------------------------------ Master deck

    /// <summary>Opens a master deck at a slide (1-based, through all of it).</summary>
    public void LoadShow(IReadOnlyList<LivePart> parts, int slide) => OnUi(() =>
    {
        LeaveMaster();
        if (parts.Count == 0) { _active.Unload(); return; }
        _parts = parts;
        var (part, page) = Find(slide) ?? (0, parts[0].Pages[0]);
        _part = part;
        _slots.Add(new Slot { Part = part, Player = _active, Page = page });
        Open(_active, part, page);
    });

    private void LeaveMaster()
    {
        _generation++;
        foreach (var s in _slots)
            if (s.Player != null && s.Player != _active) s.Player.Dispose();
        _slots.Clear();
        _parts = null;
        _pending = null;
        _holdFrames = false;
        _part = 0;
        _active.Muted = false;
    }

    private int Total => _parts!.Sum(p => p.Pages.Count);
    private int Offset(int part) => _parts!.Take(part).Sum(p => p.Pages.Count);

    private (int Part, LivePage Page)? Find(int slide)
    {
        for (var i = 0; i < _parts!.Count; i++)
        {
            if (slide >= 1 && slide <= _parts[i].Pages.Count) return (i, _parts[i].Pages[slide - 1]);
            slide -= _parts[i].Pages.Count;
        }
        return null;
    }

    private void Open(DeckPlayer player, int part, LivePage page)
    {
        var p = _parts![part];
        player.SetMedia(p.MediaJson);
        player.SetRange(JsonSerializer.Serialize(new
        {
            first = p.Pages[0].PageId,
            last = p.Pages[^1].PageId,
            trimmedStart = p.TrimmedStart,
            trimmedEnd = p.TrimmedEnd,
            firstSteps = p.Pages[0].ClickSteps,
            lastSteps = p.Pages[^1].ClickSteps,
        }));
        player.Load(p.DeckId, "id." + page.PageId);
    }

    /// <summary>Next was pressed on a deck's last slide, or Previous on its first.</summary>
    private void OnEdge(DeckPlayer player, int direction)
    {
        if (_parts == null || player != _active || _pending != null) return;
        var part = _part + direction;
        if (part < 0 || part >= _parts.Count) return;
        Show(part, direction > 0 ? _parts[part].Pages[0] : _parts[part].Pages[^1]);
    }

    private void Show(int part, LivePage page)
    {
        _pending = null;
        if (part == _part)
        {
            Send(_active, page);
            return;
        }

        var slot = _slots.FirstOrDefault(s => s.Part == part);
        if (slot?.Player is { Ready: true } player && player.PageId == page.PageId && !page.StartsByItself)
        {
            SwitchTo(slot, cached: true); // already waiting on the right slide
            return;
        }

        _pending = (part, page, Environment.TickCount64);
        if (slot?.Player is { Ready: true } p && p.PageId != page.PageId) Send(p, page);
        else if (slot?.Player is { Ready: true } again) again.Load(_parts![part].DeckId, "id." + page.PageId); // start its media from the top
        else Want(part, page);
        if (slot != null) slot.Page = page;
    }

    private static void Send(DeckPlayer player, LivePage page)
    {
        if (page.PlayerNumber > 0) player.GoTo(page.PlayerNumber);
    }

    private void OnSlide(DeckPlayer player, int slide, int count, string? param)
    {
        if (_parts == null)
        {
            if (player == _active) SlideChanged?.Invoke(slide, count, param);
            return;
        }
        if (_pending is var (part, page, _) && _slots.FirstOrDefault(s => s.Part == part) is { } slot && slot.Player == player)
        {
            if (player.PageId == page.PageId) SwitchTo(slot, cached: false);
        }
        else if (player == _active)
        {
            Report();
        }
    }

    private void SwitchTo(Slot slot, bool cached)
    {
        var old = _active;
        _pending = null;
        _active = slot.Player!;
        _part = slot.Part;
        if (old != _active)
        {
            old.Muted = true;
            old.Video(VideoCommand.Pause);
        }
        _active.Muted = false;

        if (cached && _active.LastFrame is { } frame) FrameCaptured?.Invoke(frame);
        _ = RefreshFramesAsync(_active, hold: !cached);
        Report();
        VideoChanged?.Invoke(_active.LastVideo);
        PrepareNeighbours();
    }

    /// <summary>Asks for a fresh picture of the slide; until it's on its way, older ones are held back.</summary>
    private async Task RefreshFramesAsync(DeckPlayer player, bool hold)
    {
        _holdFrames = hold;
        await player.RestartScreencastAsync();
        if (player == _active) _holdFrames = false;
    }

    private void Report()
    {
        var pages = _parts![_part].Pages;
        for (var i = 0; i < pages.Count; i++)
        {
            if (pages[i].PageId != _active.PageId) continue;
            SlideChanged?.Invoke(Offset(_part) + i + 1, Total, null);
            if (_slots.Count == 1) PrepareNeighbours(); // the show has just opened
            return;
        }
    }

    /// <summary>Gets the decks either side ready in hidden players.</summary>
    private void PrepareNeighbours()
    {
        if (_part + 1 < _parts!.Count) Want(_part + 1, Waiting(_parts[_part + 1]));
        if (_part > 0) Want(_part - 1, _parts[_part - 1].Pages[^1]);
        foreach (var s in _slots.Where(s => Math.Abs(s.Part - _part) > 1 && s.Player != null).ToList())
        {
            _slots.Remove(s);
            s.Player!.Dispose();
        }
    }

    /// <summary>
    /// Where the next deck waits: on its first slide, ready to cut to. If something on it starts by
    /// itself it would have played out unseen, so it waits on a quiet slide and goes there when needed.
    /// </summary>
    private static LivePage Waiting(LivePart part)
    {
        var first = part.Pages[0];
        if (!first.StartsByItself) return first;
        return part.Pages.FirstOrDefault(p => !p.StartsByItself) ?? part.AllPages.FirstOrDefault(p => !p.StartsByItself) ?? first;
    }

    private async void Want(int part, LivePage page)
    {
        var slot = _slots.FirstOrDefault(s => s.Part == part);
        if (slot != null)
        {
            slot.Page = page;
            if (slot.Player == null || slot.Player == _active) return; // being created, and will open on slot.Page
            if (!slot.Player.Ready) Open(slot.Player, part, page);
            else if (slot.Player.PageId != page.PageId) Send(slot.Player, page);
            return;
        }

        slot = new Slot { Part = part, Page = page };
        _slots.Add(slot);

        // Reuse a player that's no longer next to the current deck.
        var spare = _slots.FirstOrDefault(s => s != slot && s.Player != null && s.Player != _active
            && Math.Abs(s.Part - _part) > 1 && s.Part != _pending?.Part);
        if (spare != null)
        {
            _slots.Remove(spare);
            slot.Player = spare.Player;
            slot.Player!.Muted = true;
            Open(slot.Player, part, page);
            return;
        }

        var generation = _generation;
        DeckPlayer player;
        try
        {
            player = await DeckPlayer.CreateAsync(_width, _height);
        }
        catch
        {
            _slots.Remove(slot);
            return;
        }
        if (_disposed || generation != _generation || !_slots.Contains(slot))
        {
            player.Dispose();
            return;
        }
        player.Muted = true;
        slot.Player = player;
        Hook(player);
        Open(player, part, slot.Page);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var s in _slots)
            if (s.Player != _active) s.Player?.Dispose();
        _slots.Clear();
        _active.Dispose();
    }
}
