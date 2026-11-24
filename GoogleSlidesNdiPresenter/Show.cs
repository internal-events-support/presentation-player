namespace GoogleSlidesNdiPresenter;

/// <summary>One deck in a master deck. SkipStart/SkipEnd leave out that many slides from its start and end.</summary>
public sealed class MasterPart
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public int SkipStart { get; set; }
    public int SkipEnd { get; set; }
}

/// <summary>
/// A show made of several decks, played as one. Only the links are kept: the slides are pulled from
/// the decks each time it's opened or refreshed. The skips count from each end, so slides added in
/// the middle of a deck come along by themselves.
/// </summary>
public sealed class MasterDeck
{
    public const string UrlPrefix = "master:";

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<MasterPart> Parts { get; set; } = [];

    public string Url => UrlPrefix + Id;

    /// <summary>The id in a "master:…" link, or null for any other link.</summary>
    public static string? IdFromUrl(string url) =>
        url.Trim() is var u && u.StartsWith(UrlPrefix, StringComparison.OrdinalIgnoreCase) ? u[UrlPrefix.Length..].Trim() : null;
}

/// <summary>A slide as loaded from its deck.</summary>
/// <param name="PageId">Google's id for the slide, if it could be read.</param>
/// <param name="PlayerNumber">The slide's number in Google's player (0 if it's skipped there).</param>
/// <param name="ClickSteps">How many clicks its animations take before Next moves on.</param>
/// <param name="StartsByItself">It plays an animation, video or audio as soon as it's shown.</param>
public sealed record DeckSlide(string? PageId, bool Skipped, NoteText Notes, SlidePicture Picture, int PlayerNumber, int ClickSteps, bool StartsByItself)
{
    public byte[]? Png => Picture.Data;
}

/// <summary>How good a slide's picture is. Old is from before a refresh.</summary>
public enum PictureQuality { None, Old, Preview, Full }

/// <summary>A slide's picture (PNG or JPEG), which arrives some time after the deck opens.</summary>
public sealed class SlidePicture
{
    private readonly object _lock = new();
    public byte[]? Data { get; private set; }
    public PictureQuality Quality { get; private set; }

    /// <summary>Uses a picture unless there's already a better one.</summary>
    public bool Offer(byte[]? data, PictureQuality quality)
    {
        if (data == null) return false;
        lock (_lock)
        {
            if (quality <= Quality) return false;
            (Data, Quality) = (data, quality);
            return true;
        }
    }
}

/// <summary>A slide in the show. Number is its place among all the slides, from 1; skipped slides have one too, as in Google Slides.</summary>
public sealed record ShowSlide(int Part, int Number, DeckSlide Slide);

/// <summary>A deck in the show: one for a normal presentation, several for a master deck.</summary>
public sealed record ShowPart(string DeckId, string Title, long? Revision, int StripStart, int FirstSlide, int SlideCount);

/// <summary>What's open: a single deck, or the chosen slides of several.</summary>
public sealed class LoadedShow
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required bool IsMaster { get; init; }
    public required IReadOnlyList<ShowPart> Parts { get; init; }
    /// <summary>Every slide in order, including the skipped ones.</summary>
    public required IReadOnlyList<ShowSlide> Strip { get; init; }
    /// <summary>The slides that are presented.</summary>
    public required IReadOnlyList<ShowSlide> Slides { get; init; }
    public required IReadOnlyList<SlideMedia> Media { get; set; }
    /// <summary>What the live player needs to play a master deck (empty for a single deck).</summary>
    public required IReadOnlyList<LivePart> LiveParts { get; init; }

    public static LoadedShow FromDeck(LoadedPresentation deck) => Build(deck.Id, deck.Title, false, [(deck, 0, 0)]);

    public static LoadedShow FromMaster(MasterDeck master, IReadOnlyList<LoadedPresentation> decks) =>
        Build(master.Url, master.Name, true, master.Parts.Select((p, i) => (decks[i], p.SkipStart, p.SkipEnd)).ToList());

    private static LoadedShow Build(string id, string title, bool isMaster, IReadOnlyList<(LoadedPresentation Deck, int SkipStart, int SkipEnd)> sources)
    {
        var parts = new List<ShowPart>();
        var strip = new List<ShowSlide>();
        var slides = new List<ShowSlide>();
        var media = new List<SlideMedia>();
        var live = new List<LivePart>();

        foreach (var (deck, skipStart, skipEnd) in sources)
        {
            // The skips count the slides that are presented; hidden ones don't take up a place.
            var playable = Enumerable.Range(0, deck.Slides.Count).Where(i => !deck.Slides[i].Skipped).ToList();
            var first = Math.Clamp(skipStart, 0, playable.Count);
            var last = Math.Max(first, playable.Count - Math.Max(0, skipEnd)); // exclusive
            var from = last > first ? playable[first] : 0;
            var to = last > first ? playable[last - 1] + 1 : 0;
            // A normal presentation shows everything, so hidden slides at its ends are listed too.
            if (!isMaster) (from, to) = (0, deck.Slides.Count);

            var part = parts.Count;
            parts.Add(new ShowPart(deck.Id, deck.Title, deck.Revision, strip.Count, strip.Count + 1, to - from));
            var numbers = new Dictionary<string, int>();
            var pages = new List<LivePage>();
            for (var i = from; i < to; i++)
            {
                var slide = deck.Slides[i];
                var entry = new ShowSlide(part, strip.Count + 1, slide);
                strip.Add(entry);
                if (slide.PageId != null) numbers[slide.PageId] = entry.Number;
                if (slide.Skipped) continue;
                slides.Add(entry);
                pages.Add(new LivePage(slide.PageId ?? "", slide.PlayerNumber, slide.ClickSteps, slide.StartsByItself));
            }

            foreach (var m in deck.Media)
                if (numbers.TryGetValue(m.PageId, out var number)) media.Add(m with { Slide = number });

            if (isMaster && pages.Count > 0)
                live.Add(new LivePart(deck.Id, pages,
                    deck.Slides.Where(s => !s.Skipped).Select(s => new LivePage(s.PageId ?? "", s.PlayerNumber, s.ClickSteps, s.StartsByItself)).ToList(),
                    TrimmedStart: first > 0, TrimmedEnd: last < playable.Count, DeckPlayer.MediaJson(deck.Media)));
        }

        return new LoadedShow
        {
            Id = id, Title = title, IsMaster = isMaster, Parts = parts, Strip = strip, Slides = slides,
            Media = media.OrderBy(m => m.Slide).ToList(), LiveParts = live,
        };
    }
}
