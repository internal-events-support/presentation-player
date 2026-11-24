using System.Threading.Channels;
using GoogleSlidesNdiPresenter.Ndi;

namespace GoogleSlidesNdiPresenter;

public sealed record PresentationState(
    string Url,
    string? PresentationId,
    string? Title,
    long Version,       // increments on every successful load (cache-busts slide thumbnails)
    long? LoadedRevision,   // Google's revision number of the deck as loaded (null if unknown)
    long? LatestRevision,   // newest revision seen while polling Google every 30 s
    bool OutOfDate,         // the deck has been edited in Google Slides since it was loaded
    int ThumbnailCount, // slides with a thumbnail available (/api/slides/{n}.jpg)
    int Slide,          // 1-based, 0 when nothing is loaded
    int SlideCount,
    float NotesFontSize,
    bool NotesAutoFit,
    float NotesEffectiveFontSize,
    float NotesScroll,
    float NotesMaxScroll,
    string Notes,
    bool Loading,
    string? LoadingId,      // deck being opened or refreshed, whoever started it (UI, phone, Companion…)
    string? LoadingTitle,   // its title if known (from recents), for the "Opening …" overlay
    bool LoadingRefresh,    // true when reloading the deck that's already open
    bool Live,
    VideoInfo? Video,   // video on the current slide, null when there is none
    string Status,
    string? SlidesNdiName,
    string? NotesNdiName,
    int SlidesReceivers,
    int NotesReceivers,
    string? NdiError,
    string? NdiDiscovery,   // discovery server(s) the NDI outputs register with, or null for automatic discovery
    string? LiveError,
    IReadOnlyList<RecentPresentation> Recent,
    IReadOnlyList<SlideMedia> Media,    // video and audio on each slide, with how they start
    bool IsMaster,                      // a master deck (several decks played as one) is open
    string? DeckId,                     // Google's id of the deck the current slide is from
    string? PageId,                     // and of the slide itself
    IReadOnlyList<int> Skipped,         // numbers of the slides that are skipped (hidden in Google Slides)
    IReadOnlyList<ShowPart> Parts,      // the decks the show is made of, and where each starts
    IReadOnlyList<int> MissingPictures, // numbers of the slides whose picture hasn't arrived yet
    bool GoogleExpired,                 // Google has ended the session: sign in again
    string NextPreview,                 // width of the next slide in the presenter view: full or half
    IReadOnlyList<MasterDeck> MasterDecks);

/// <summary>
/// Owns the presentation state and pushes rendered frames to the two NDI senders.
/// When a <see cref="LivePresenter"/> is attached, the slide output is the live Google Slides
/// player (animations/videos included) and navigation is forwarded to it; otherwise slides are
/// static images from the PDF export. All public members are thread-safe.
/// </summary>
public sealed class PresentationController : IDisposable
{
    public const float MinFontSize = 12;
    public const float MaxFontSize = 300;
    public const float FontStep = 4;
    private const float ScrollLines = 3;
    private static readonly TimeSpan RevisionPollInterval = TimeSpan.FromSeconds(30);

    private readonly object _lock = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly AppSettings _settings;
    // The output size this run started with; a new one in the settings waits for a restart.
    private readonly int _width;
    private readonly int _height;
    private readonly NdiSender? _slidesSender;
    private readonly NdiSender? _notesSender;
    private readonly Channel<LiveFrame> _liveFrames = Channel.CreateBounded<LiveFrame>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    private LivePresenter? _live;
    private string? _liveId;          // presentation currently in the live player
    private int _liveCount;           // slide count reported by the live player (0 = not yet)
    private int _liveSlide;           // and the slide it's on
    private string? _liveSlideParam;  // e.g. "id.p62", used to keep position on refresh
    private VideoInfo? _video;

    private LoadedShow? _show;
    private int _index;
    private float _fontSize;
    private bool _autoFit;
    private float _effectiveFontSize;
    private float _scroll;
    private float _maxScroll;
    private string _url;
    private bool _loading;
    private readonly string? _ndiDiscovery;
    private string? _loadingId;
    private string? _loadingTitle;
    private bool _loadingRefresh;
    private string _status = "Ready";
    private Bitmap? _slideFrame;
    private Bitmap? _notesFrame;
    private PresenterBackground? _background;
    private long _frameVersion;
    // Starts from the clock so thumbnail URLs (cached by the browser) never repeat across runs.
    private long _version = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
    private string? _liveError;
    private bool _googleExpired;
    private CancellationTokenSource? _pictures;
    private long _pictureNews;
    private TaskCompletionSource? _liveReady; // waiting for the player to show the deck being opened
    // Revisions of the decks in the show: as loaded, and the newest seen since.
    private readonly Dictionary<string, (long? Loaded, long? Latest)> _revisions = new();
    private CancellationTokenSource? _revisionPoll;
    private readonly Dictionary<(int Index, int Width), byte[]> _thumbCache = new();

    public event Action? StateChanged;

    public string? NdiError { get; }

    public Size OutputSize => new(_width, _height);

    /// <summary>Increments every time an output frame changes (for preview refresh).</summary>
    public long FrameVersion => Interlocked.Read(ref _frameVersion);

    public PresentationController(AppSettings settings)
    {
        _settings = settings;
        _width = settings.Width;
        _height = settings.Height;
        _url = settings.LastUrl;
        _fontSize = Math.Clamp(settings.NotesFontSize, MinFontSize, MaxFontSize);
        _effectiveFontSize = _fontSize;
        _autoFit = settings.AutoFitNotes;

        try
        {
            NdiConfig.Apply(settings.NdiDiscoveryServer); // must happen before NDI starts
            _ndiDiscovery = NdiConfig.Normalise(settings.NdiDiscoveryServer) is { Length: > 0 } d ? d : null;
            NdiInterop.EnsureResolver();
            if (!NdiInterop.Initialize())
                throw new InvalidOperationException("NDIlib_initialize failed (unsupported CPU?).");
            _slidesSender = new NdiSender(settings.SlidesNdiName, settings.Width, settings.Height, settings.FrameRate);
            _notesSender = new NdiSender(settings.NotesNdiName, settings.Width, settings.Height, settings.FrameRate);
        }
        catch (DllNotFoundException)
        {
            NdiError = "NDI runtime not found. Install NDI Tools or the NDI Runtime from ndi.video.";
        }
        catch (Exception ex)
        {
            NdiError = "NDI unavailable: " + ex.Message;
        }

        lock (_lock) RenderStaticSlide();
        _ = Task.Run(ProcessLiveFramesAsync);
    }

    /// <summary>Connects the live player; from now on it drives the slide output and navigation.</summary>
    public void AttachLive(LivePresenter live)
    {
        _live = live;
        live.SlideChanged += OnLiveSlideChanged;
        live.FrameCaptured += f => _liveFrames.Writer.TryWrite(f);
        live.VideoChanged += v =>
        {
            lock (_lock) _video = v;
            OnChanged();
        };
    }

    // The slides that are presented, and all of them including skipped ones.
    private int CountLocked => _show?.Slides.Count ?? _liveCount;
    private int TotalLocked => _show?.Strip.Count ?? _liveCount;
    private int NumberLocked => CurrentLocked?.Number ?? (CountLocked > 0 ? _index + 1 : 0);

    private ShowSlide? CurrentLocked => _show is { } s && _index >= 0 && _index < s.Slides.Count ? s.Slides[_index] : null;

    // The whole show's revision: the sum over its decks, so it goes up when any of them is edited.
    private long? RevisionLocked(bool latest)
    {
        long? sum = null;
        foreach (var (loaded, newest) in _revisions.Values)
            if ((latest ? newest : loaded) is { } r) sum = (sum ?? 0) + r;
        return sum;
    }

    public PresentationState GetState()
    {
        lock (_lock)
        {
            var count = CountLocked;
            var current = CurrentLocked;
            return new PresentationState(
                _url,
                _show?.Id,
                _show?.Title,
                _version,
                RevisionLocked(latest: false),
                RevisionLocked(latest: true),
                OutOfDateLocked,
                _show?.Strip.Count ?? 0,
                count > 0 ? NumberLocked : 0,
                TotalLocked,
                _fontSize,
                _autoFit,
                _effectiveFontSize,
                _scroll,
                _maxScroll,
                NotesFor(_index)?.Text ?? "",
                _loading,
                _loading ? _loadingId : null,
                _loading ? _loadingTitle : null,
                _loading && _loadingRefresh,
                _live != null,
                _video,
                _status,
                _slidesSender?.Name,
                _notesSender?.Name,
                _slidesSender?.Connections ?? 0,
                _notesSender?.Connections ?? 0,
                NdiError,
                _ndiDiscovery,
                _liveError,
                _settings.Recent.ToList(),
                _show?.Media ?? [],
                _show?.IsMaster ?? false,
                current != null ? _show!.Parts[current.Part].DeckId : _show?.Parts.FirstOrDefault()?.DeckId,
                current?.Slide.PageId,
                _show?.Strip.Where(s => s.Slide.Skipped).Select(s => s.Number).ToList() ?? [],
                _show?.Parts ?? [],
                _show?.Strip.Select((s, i) => (s, i)).Where(x => x.s.Slide.Picture.Quality < PictureQuality.Preview).Select(x => x.i + 1).ToList() ?? [],
                _googleExpired,
                _settings.NextPreview,
                _settings.MasterDecks.ToList());
        }
    }

    public void SetGoogleExpired(bool expired)
    {
        lock (_lock)
        {
            if (_googleExpired == expired) return;
            _googleExpired = expired;
        }
        OnChanged();
    }

    /// <summary>Records why the live player couldn't start (slides fall back to static images).</summary>
    public void SetLiveError(string message)
    {
        lock (_lock) _liveError = message;
        OnChanged();
    }

    /// <summary>JPEG thumbnail of a slide by its number (1-based), or null if it has none (yet).</summary>
    public byte[]? GetSlideThumbnail(int place, int width)
    {
        byte[]? png;
        var key = (place - 1, Math.Clamp(width, 64, 1920));
        lock (_lock)
        {
            if (_show == null || place < 1 || place > _show.Strip.Count) return null;
            if (_thumbCache.TryGetValue(key, out var cached)) return cached;
            var picture = _show.Strip[place - 1].Slide.Picture;
            png = picture.Quality >= PictureQuality.Preview ? picture.Data : null;
        }
        if (png == null) return null;

        using var full = FrameRenderer.RenderSlide(png, _width, _height);
        using var thumb = FrameRenderer.Thumbnail(full, key.Item2);
        var jpeg = FrameRenderer.EncodeJpeg(thumb, 85);
        lock (_lock) _thumbCache[key] = jpeg;
        return jpeg;
    }

    /// <summary>Closes the open presentation: both outputs go to the holding scene.</summary>
    public async Task CloseAsync()
    {
        await _loadGate.WaitAsync();
        try
        {
            lock (_lock)
            {
                _show = null;
                StopPicturesLocked();
                _url = "";
                _index = 0;
                _scroll = 0;
                _video = null;
                _liveId = null;
                _liveCount = 0;
                _liveSlide = 0;
                _liveSlideParam = null;
                _status = "Ready";
                StopRevisionPollLocked();
                _version++;
                _thumbCache.Clear();
                InvalidateNotes();
                RenderStaticSlide();
            }
            _live?.Unload();
            _settings.LastUrl = "";
            _settings.Save();
        }
        finally
        {
            _loadGate.Release();
        }
        OnChanged();
    }

    /// <summary>Saves a master deck (new when it has no id), and returns it.</summary>
    public MasterDeck SaveMaster(MasterDeck deck)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(deck.Id)) deck.Id = Guid.NewGuid().ToString("N")[..12];
            var i = _settings.MasterDecks.FindIndex(m => m.Id == deck.Id);
            if (i >= 0) _settings.MasterDecks[i] = deck;
            else _settings.MasterDecks.Add(deck);
        }
        _settings.Save();
        OnChanged();
        return deck;
    }

    public void DeleteMaster(string id)
    {
        lock (_lock) _settings.MasterDecks.RemoveAll(m => m.Id == id);
        _settings.Save();
        OnChanged();
    }

    /// <summary>Width of the next slide in the presenter view: "full" or "half".</summary>
    public void SetNextPreview(string value)
    {
        lock (_lock)
        {
            _settings.NextPreview = value == "half" ? "half" : "full";
            InvalidateNotes();
            RenderNotesFrame();
        }
        _settings.Save();
        OnChanged();
    }

    public void RemoveRecent(string id)
    {
        lock (_lock) _settings.Recent.RemoveAll(r => r.Id == id);
        _settings.Save();
        OnChanged();
    }

    /// <summary>Returns a copy of the current slide output frame (caller disposes).</summary>
    public Bitmap? GetSlidePreview(int maxWidth = int.MaxValue)
    {
        lock (_lock) return _slideFrame == null ? null : FrameRenderer.Thumbnail(_slideFrame, maxWidth);
    }

    /// <summary>Returns a copy of the current notes output frame (caller disposes).</summary>
    public Bitmap? GetNotesPreview(int maxWidth = int.MaxValue)
    {
        lock (_lock) return _notesFrame == null ? null : FrameRenderer.Thumbnail(_notesFrame, maxWidth);
    }

    /// <summary>
    /// Loads (or reloads) the presentation. Pass null to refresh the current URL.
    /// Reloading the same presentation keeps the current slide position.
    /// </summary>
    public async Task LoadAsync(string? url = null, CancellationToken ct = default)
    {
        url = (url ?? _url).Trim();
        if (url.Length == 0) throw new ArgumentException("No Google Slides URL set.");

        await _loadGate.WaitAsync(ct);
        try
        {
            var masterId = MasterDeck.IdFromUrl(url);
            MasterDeck? master = null;
            if (masterId != null)
            {
                lock (_lock) master = _settings.MasterDecks.FirstOrDefault(m => m.Id == masterId)
                    ?? _settings.MasterDecks.FirstOrDefault(m => m.Name.Equals(masterId, StringComparison.OrdinalIgnoreCase));
                if (master == null) throw new ArgumentException("That master deck no longer exists.");
                url = master.Url;
            }
            var id = master?.Url ?? PresentationLoader.ExtractId(url);
            lock (_lock)
            {
                _loading = true;
                _loadingId = id;
                _loadingRefresh = id != null && id == _show?.Id;
                _loadingTitle = master?.Name ?? (_loadingRefresh ? _show?.Title : _settings.Recent.FirstOrDefault(r => r.Id == id)?.Title);
                _status = "Loading presentation…";
                // A single deck starts loading in the player straight away. A master deck's player needs to
                // know its slides first, so whatever is on screen stays until everything has downloaded.
                if (master == null && id != null && _live != null)
                {
                    var same = id == _liveId;
                    if (!same) { _liveCount = 0; _liveSlide = 0; _liveSlideParam = null; }
                    _liveId = id;
                    _live.Load(id, same ? _liveSlideParam : null);
                }
            }
            OnChanged();

            LoadedShow loaded;
            IReadOnlyList<LoadedPresentation> decks;
            try
            {
                decks = await Task.Run(async () => master != null
                    ? await PresentationLoader.LoadPartsAsync(master, _width, _height, ct)
                    : [await PresentationLoader.LoadAsync(url, _width, _height, ct)], ct);
                loaded = master != null ? LoadedShow.FromMaster(master, decks) : LoadedShow.FromDeck(decks[0]);
                if (master != null && loaded.Slides.Count == 0)
                    throw new InvalidOperationException("Every slide of this master deck is left out. Lower the numbers of slides to skip.");
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    _loading = false;
                    _status = "Load failed: " + ex.Message;
                }
                OnChanged();
                throw;
            }

            Task liveReady = Task.CompletedTask;
            lock (_lock)
            {
                // Refreshing stays on the same slide, even if slides were added before it.
                var was = CurrentLocked;
                var sameShow = _show?.Id == loaded.Id;
                var keep = -1;
                if (sameShow && was?.Slide.PageId is { } pageId)
                {
                    // The same deck can be in a master deck twice: prefer the one it was in.
                    var deckId = _show!.Parts[was.Part].DeckId;
                    var same = Enumerable.Range(0, loaded.Slides.Count)
                        .Where(i => loaded.Slides[i].Slide.PageId == pageId && loaded.Parts[loaded.Slides[i].Part].DeckId == deckId).ToList();
                    if (same.Count > 0) keep = same.Cast<int?>().FirstOrDefault(i => loaded.Slides[i!.Value].Part == was.Part) ?? same[0];
                }
                _show = loaded;
                _url = url;
                _status = $"Loaded {loaded.Strip.Count} slides at {DateTime.Now:HH:mm:ss}";
                _version++;
                _thumbCache.Clear();
                StartRevisionPollLocked(loaded.Parts);

                if (master != null)
                {
                    _index = keep >= 0 ? keep : sameShow ? Math.Clamp(_index, 0, loaded.Slides.Count - 1) : 0;
                    _liveId = loaded.Id;
                    _liveCount = 0; // hold the output until the player is on the slide
                    _liveSlide = 0;
                    _liveSlideParam = null;
                    _video = null;
                    _live?.LoadShow(loaded.LiveParts, _index + 1);
                    // Keep the names shown in the editor up to date.
                    for (var i = 0; i < master.Parts.Count; i++) master.Parts[i].Title = loaded.Parts[i].Title;
                }
                else
                {
                    if (_live == null && keep >= 0) _index = keep;
                    else if (_live == null && !sameShow) _index = 0;
                    else if (_liveCount > 0) _index = LiveIndexLocked() ?? _index;
                    _index = Math.Clamp(_index, 0, Math.Max(0, CountLocked - 1));
                    // The live player uses this to find the current slide's audio for Play/Pause/Rewind.
                    _live?.SetMedia(loaded.Media);
                    _settings.Recent.RemoveAll(r => r.Id == loaded.Id);
                    _settings.Recent.Insert(0, new RecentPresentation
                    {
                        Id = loaded.Id, Url = url, Title = loaded.Title,
                        SlideCount = loaded.Strip.Count, LastOpened = DateTime.Now,
                    });
                    if (_settings.Recent.Count > 8) _settings.Recent.RemoveRange(8, _settings.Recent.Count - 8);
                    if (_liveCount > 0 && _liveCount != loaded.Slides.Count && _liveCount != loaded.Strip.Count)
                        _status += $" (player reports {_liveCount}; notes may be misaligned)";
                }
                InvalidateNotes();
                if (_live == null || (_liveCount == 0 && !sameShow)) RenderStaticSlide();
                else RenderNotesFrame();
                StartPicturesLocked(loaded, decks.DistinctBy(d => d.Id).ToList());
                if (_live != null && _liveCount == 0)
                {
                    _liveReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    liveReady = _liveReady.Task;
                }
            }
            OnChanged();

            // It's open once its slide is on the output, which the player usually has ready by now.
            await Task.WhenAny(liveReady, Task.Delay(8000, ct));
            lock (_lock)
            {
                _liveReady = null;
                _loading = false;
            }

            _settings.LastUrl = url;
            _settings.Save();
            OnChanged();
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>Next animation build or slide (like a clicker).</summary>
    public bool Next()
    {
        if (_live != null) { _live.Next(); return true; }
        return GoToIndexStatic(i => i + 1);
    }

    /// <summary>Previous animation build or slide.</summary>
    public bool Previous()
    {
        if (_live != null) { _live.Previous(); return true; }
        return GoToIndexStatic(i => i - 1);
    }

    /// <summary>Goes to a slide by its number. A skipped slide isn't shown, so it goes to the next one that is.</summary>
    public bool GoTo(int slideNumber)
    {
        int index, target;
        lock (_lock)
        {
            if (_show is { } show)
            {
                index = -1;
                for (var i = 0; i < show.Slides.Count && index < 0; i++)
                    if (show.Slides[i].Number >= slideNumber) index = i;
                if (index < 0) index = show.Slides.Count - 1; // only skipped slides after it
                if (index < 0) return false;
                // A master deck's player counts the slides that are shown; a single deck's has Google's numbering.
                target = show.IsMaster ? index + 1 : show.Slides[index].Slide.PlayerNumber;
            }
            else
            {
                index = slideNumber - 1;
                target = slideNumber;
            }
        }
        if (_live == null) return GoToIndexStatic(_ => index);
        _live.GoTo(target);
        return true;
    }

    /// <summary>Plays, pauses or toggles the video on the current slide (live player only).</summary>
    public bool ControlVideo(VideoCommand command)
    {
        if (_live == null) return false;
        _live.Video(command);
        return true;
    }

    private bool GoToIndexStatic(Func<int, int> next)
    {
        lock (_lock)
        {
            var count = CountLocked;
            if (count == 0) return false;
            var target = next(_index);
            if (target < 0 || target >= count || target == _index) return false;
            _index = target;
            _scroll = 0;
            InvalidateNotes();
            RenderStaticSlide();
        }
        OnChanged();
        return true;
    }

    private void OnLiveSlideChanged(int slide, int count, string? slideParam)
    {
        lock (_lock)
        {
            _liveReady?.TrySetResult();
            _liveCount = count;
            _liveSlide = slide;
            _liveSlideParam = slideParam;
            if (LiveIndexLocked() is not { } index) return;
            if (index != _index)
            {
                _index = index;
                _scroll = 0;
            }
            InvalidateNotes();
            RenderNotesFrame();
        }
        OnChanged();
    }

    /// <summary>The show's slide the player is on, or null if it isn't one of them.</summary>
    private int? LiveIndexLocked()
    {
        if (_show is not { } show || _liveId != show.Id)
            return Math.Clamp(_liveSlide - 1, 0, Math.Max(0, _liveCount - 1)); // its slides are still downloading
        if (!show.IsMaster && _liveSlideParam is ['i', 'd', '.', .. var page])
            for (var i = 0; i < show.Slides.Count; i++)
                if (show.Slides[i].Slide.PageId == page) return i;
        if (!show.IsMaster)
            for (var i = 0; i < show.Slides.Count; i++)
                if (show.Slides[i].Slide.PlayerNumber == _liveSlide) return i;
        return show.IsMaster && _liveSlide >= 1 && _liveSlide <= show.Slides.Count ? _liveSlide - 1 : null;
    }

    private async Task ProcessLiveFramesAsync()
    {
        await foreach (var frame in _liveFrames.Reader.ReadAllAsync())
        {
            try
            {
                var bmp = FrameRenderer.RenderLiveFrame(frame, _width, _height);
                lock (_lock)
                {
                    if (_liveCount == 0) { bmp.Dispose(); continue; } // player not ready / switching decks
                    SetSlideFrame(bmp);
                    RenderNotesFrame();
                }
            }
            catch
            {
                // Skip undecodable frames.
            }
        }
    }

    // While auto-fit is on, A+/A- start from the fitted size and switch back to manual sizing.
    public void IncreaseNotesSize() => AdjustNotesSize(+FontStep);
    public void DecreaseNotesSize() => AdjustNotesSize(-FontStep);

    private void AdjustNotesSize(float delta)
    {
        float baseSize;
        lock (_lock) baseSize = _autoFit ? _effectiveFontSize : _fontSize;
        SetNotesSize(baseSize + delta);
    }

    /// <summary>Sets a manual notes size (turns auto-fit off).</summary>
    public void SetNotesSize(float size)
    {
        lock (_lock)
        {
            size = Math.Clamp(size, MinFontSize, MaxFontSize);
            if (size == _fontSize && !_autoFit) return;
            _fontSize = size;
            _autoFit = false;
            InvalidateNotes();
            RenderNotesFrame();
        }
        _settings.NotesFontSize = size;
        _settings.AutoFitNotes = false;
        OnChanged();
    }

    /// <summary>When on, notes are drawn at the largest size that fits without scrolling.</summary>
    public void SetNotesAutoFit(bool enabled)
    {
        lock (_lock)
        {
            if (_autoFit == enabled) return;
            _autoFit = enabled;
            _scroll = 0;
            InvalidateNotes();
            RenderNotesFrame();
        }
        _settings.AutoFitNotes = enabled;
        OnChanged();
    }

    public void ToggleNotesAutoFit()
    {
        bool current;
        lock (_lock) current = _autoFit;
        SetNotesAutoFit(!current);
    }

    public void ScrollNotesDown() => ScrollNotesBy(+1);
    public void ScrollNotesUp() => ScrollNotesBy(-1);

    private void ScrollNotesBy(int direction)
    {
        lock (_lock)
        {
            var step = _effectiveFontSize * 1.33f * ScrollLines;
            var target = Math.Clamp(_scroll + direction * step, 0, _maxScroll);
            if (target == _scroll) return;
            _scroll = target;
            InvalidateNotes();
            RenderNotesFrame();
        }
        OnChanged();
    }

    public void ScrollNotesToTop()
    {
        lock (_lock)
        {
            if (_scroll == 0) return;
            _scroll = 0;
            InvalidateNotes();
            RenderNotesFrame();
        }
        OnChanged();
    }

    private NoteText? NotesFor(int index) =>
        _show is { } s && index >= 0 && index < s.Slides.Count ? s.Slides[index].Slide.Notes : null;

    private byte[]? PngFor(int index) =>
        _show is { } s && index >= 0 && index < s.Slides.Count ? s.Slides[index].Slide.Png : null;

    // Caller must hold _lock.
    private void InvalidateNotes()
    {
        _background?.Dispose();
        _background = null;
    }

    // Caller must hold _lock.
    private void RenderStaticSlide()
    {
        // The picture isn't here yet: leave what's on the output rather than cut to black.
        if (HasDeckLocked && PngFor(_index) == null && _slideFrame != null)
        {
            RenderNotesFrame();
            return;
        }
        var drawn = CurrentLocked?.Slide.Picture;
        _drawnCurrent = (drawn, drawn?.Quality ?? default);
        SetSlideFrame(HasDeckLocked
            ? FrameRenderer.RenderSlide(PngFor(_index), _width, _height)
            : FrameRenderer.RenderHolding(HoldingScene.Slides, _width, _height));
        RenderNotesFrame();
    }

    // A deck is open (or the live player is already showing one while its notes download).
    private bool HasDeckLocked => _show != null || _liveCount > 0;

    // Caller must hold _lock.
    private void SetSlideFrame(Bitmap frame)
    {
        _slidesSender?.SetFrame(FrameRenderer.ToBgra(frame));
        _slideFrame?.Dispose();
        _slideFrame = frame;
        Interlocked.Increment(ref _frameVersion);
    }

    // Caller must hold _lock.
    private void RenderNotesFrame()
    {
        if (!HasDeckLocked)
        {
            var holding = FrameRenderer.RenderHolding(HoldingScene.Presenter, _width, _height);
            _notesSender?.SetFrame(FrameRenderer.ToBgra(holding));
            _notesFrame?.Dispose();
            _notesFrame = holding;
            Interlocked.Increment(ref _frameVersion);
            return;
        }

        if (_background == null)
        {
            var count = CountLocked;
            var next = _show is { } shown && _index + 1 < shown.Slides.Count ? shown.Slides[_index + 1].Slide.Picture : null;
            _drawnNext = (next, next?.Quality ?? default);
            _background = FrameRenderer.RenderPresenterBackground(
                NotesFor(_index), NumberLocked - 1, TotalLocked, PngFor(_index + 1), _index + 1 < count,
                _settings.NextPreview == "half",
                _fontSize, _autoFit, MinFontSize, MaxFontSize, _scroll, _width, _height);
            _scroll = _background.Scroll;
            _effectiveFontSize = _background.FontSize;
            _maxScroll = _background.MaxScroll;
        }

        var notes = _slideFrame != null
            ? FrameRenderer.ComposePresenter(_background, _slideFrame)
            : new Bitmap(_background.Bitmap);
        _notesSender?.SetFrame(FrameRenderer.ToBgra(notes));
        _notesFrame?.Dispose();
        _notesFrame = notes;
        Interlocked.Increment(ref _frameVersion);
    }

    // Caller must hold _lock.
    private void StopPicturesLocked()
    {
        _pictures?.Cancel();
        _pictures = null;
    }

    /// <summary>Brings in the pictures, and whether each clip can be played, after a deck has opened. Caller must hold _lock.</summary>
    private void StartPicturesLocked(LoadedShow show, IReadOnlyList<LoadedPresentation> decks)
    {
        StopPicturesLocked();
        var cts = new CancellationTokenSource();
        _pictures = cts;
        var gate = new SemaphoreSlim(3);
        foreach (var deck in decks)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await gate.WaitAsync(cts.Token);
                    try { await PresentationLoader.LoadPicturesAsync(deck, _width, _height, () => PlaceIn(show, deck), () => PictureArrived(show), cts.Token); }
                    finally { gate.Release(); }
                }
                catch (Exception) { /* closed, or no pictures to be had; the live slides don't need them */ }
                PictureArrived(show, always: true);
            });
            _ = Task.Run(async () =>
            {
                try
                {
                    var access = (await deck.MediaAccess).ToLookup(m => (m.PageId, m.FileId), m => m.Access);
                    lock (_lock)
                    {
                        if (_show != show) return;
                        var ids = show.Slides.Concat(show.Strip).Where(s => show.Parts[s.Part].DeckId == deck.Id).Select(s => s.Slide.PageId).ToHashSet();
                        show.Media = show.Media.Select(m => m.Access == null && ids.Contains(m.PageId) && access.Contains((m.PageId, m.FileId))
                            ? m with { Access = access[(m.PageId, m.FileId)].First() } : m).ToList();
                    }
                    OnChanged();
                }
                catch (Exception) { /* stays unknown */ }
            });
        }
    }

    /// <summary>Where in a deck its pictures matter most: the current slide, or the first that's shown.</summary>
    private int PlaceIn(LoadedShow show, LoadedPresentation deck)
    {
        ShowSlide? at;
        lock (_lock)
        {
            at = _show == show && _index < show.Slides.Count
                ? show.Slides.Skip(_index).FirstOrDefault(s => show.Parts[s.Part].DeckId == deck.Id) : null;
        }
        at ??= show.Slides.FirstOrDefault(s => show.Parts[s.Part].DeckId == deck.Id);
        for (var i = 0; at != null && i < deck.Slides.Count; i++)
            if (ReferenceEquals(deck.Slides[i], at.Slide)) return i;
        return 0;
    }

    private void PictureArrived(LoadedShow show, bool always = false)
    {
        lock (_lock)
        {
            if (_show != show) return;
            var next = _index + 1 < show.Slides.Count ? show.Slides[_index + 1].Slide.Picture : null;
            if (_drawnNext != (next, next?.Quality ?? default))
            {
                InvalidateNotes();
                RenderNotesFrame();
            }
            if (_live == null || _liveCount == 0)
            {
                var current = CurrentLocked?.Slide.Picture;
                if (_drawnCurrent != (current, current?.Quality ?? default)) RenderStaticSlide();
            }
            // The strip fills in a few times a second rather than for every picture.
            var now = Environment.TickCount64;
            if (!always && now - _pictureNews < 250) return;
            _pictureNews = now;
        }
        OnChanged();
    }

    private (SlidePicture? Picture, PictureQuality Quality) _drawnNext, _drawnCurrent;

    private bool OutOfDateLocked => _revisions.Values.Any(r => r.Latest > r.Loaded);

    // Caller must hold _lock.
    private void StartRevisionPollLocked(IReadOnlyList<ShowPart> parts)
    {
        StopRevisionPollLocked();
        foreach (var p in parts) _revisions[p.DeckId] = (p.Revision, p.Revision);
        var cts = new CancellationTokenSource();
        _revisionPoll = cts;
        _ = PollRevisionsAsync(_revisions.Keys.ToList(), cts);
    }

    // Caller must hold _lock.
    private void StopRevisionPollLocked()
    {
        _revisionPoll?.Cancel();
        _revisionPoll?.Dispose();
        _revisionPoll = null;
        _revisions.Clear();
    }

    /// <summary>
    /// Checks the open decks' revisions every 30 s so the operator can see when one was edited in
    /// Google Slides. It never refreshes by itself; that stays the operator's call.
    /// </summary>
    private async Task PollRevisionsAsync(IReadOnlyList<string> ids, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            using var timer = new PeriodicTimer(RevisionPollInterval);
            while (await timer.WaitForNextTickAsync(ct))
            {
                foreach (var id in ids)
                {
                    var revision = await PresentationLoader.FetchRevisionAsync(id, ct);
                    if (revision == null) continue; // offline or Google hiccup: try again next time

                    bool changed;
                    lock (_lock)
                    {
                        if (_revisionPoll != cts) return; // deck closed or reloaded meanwhile
                        var (loaded, latest) = _revisions[id];
                        // Couldn't read it at load time: start from here.
                        if (loaded == null) loaded = latest = revision;
                        changed = (loaded, revision) != _revisions[id] && (latest == null || revision >= latest);
                        if (changed) _revisions[id] = (loaded, revision);
                    }
                    if (changed) OnChanged();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnChanged() => StateChanged?.Invoke();

    public void Dispose()
    {
        _liveFrames.Writer.TryComplete();
        lock (_lock)
        {
            StopRevisionPollLocked();
            StopPicturesLocked();
        }
        _slidesSender?.Dispose();
        _notesSender?.Dispose();
        lock (_lock)
        {
            _slideFrame?.Dispose();
            _notesFrame?.Dispose();
            _background?.Dispose();
        }
    }
}
