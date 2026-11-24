using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace PresentationPlayer;

/// <summary>
/// PDF and PowerPoint files. They play in a page of this app's own that behaves like Google's player
/// (wwwroot/local.html), so everything else works with them as it does with Google decks: stepping,
/// master decks, video and audio. The page and what it needs are served by the app's own web server,
/// under an address only the players know.
/// </summary>
public static class LocalDecks
{
    private static readonly ConcurrentDictionary<string, LocalDeck> Open = new();
    private static readonly ConcurrentDictionary<string, string> Paths = new(); // by id
    private static readonly string Secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private static readonly string[] PowerPoint = [".pptx", ".ppsx", ".pptm", ".ppsm"];

    /// <summary>What can be opened, for the file dialog.</summary>
    public const string DialogFilter = "Presentations (*.pptx;*.ppsx;*.pptm;*.ppsm;*.pdf)|*.pptx;*.ppsx;*.pptm;*.ppsm;*.pdf|PowerPoint (*.pptx;*.ppsx;*.pptm;*.ppsm)|*.pptx;*.ppsx;*.pptm;*.ppsm|PDF (*.pdf)|*.pdf";

    /// <summary>Where the app's web server is.</summary>
    public static int Port { get; set; }

    /// <summary>The full path of a file, if that's what the text is ("Copy as path" puts quotes around it).</summary>
    public static string? PathOf(string text)
    {
        text = text.Trim().Trim('"').Trim();
        if (text.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.IsFile) text = uri.LocalPath;
        try { return text.Length > 2 && Path.IsPathFullyQualified(text) ? Path.GetFullPath(text) : null; }
        catch (ArgumentException) { return null; }
    }

    public static string IdFor(string path)
    {
        var id = "file-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..24].ToLowerInvariant();
        Paths[id] = path;
        return id;
    }

    public static bool IsLocal(string id) => id.StartsWith("file-", StringComparison.Ordinal);

    /// <summary>Goes up when the file is changed. Null if it can't be read just now.</summary>
    public static long? Revision(string id)
    {
        if (!Paths.TryGetValue(id, out var path)) return null;
        try { return File.Exists(path) ? Version(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static long Version(string path) => File.GetLastWriteTimeUtc(path).Ticks / TimeSpan.TicksPerMillisecond;

    internal static FileStream OpenShared(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    public static string PlayerUrl(string id) => $"http://127.0.0.1:{Port}/local/{Secret}/d/{id}/embed?start=false";

    /// <summary>Lets go of files that aren't in the show any more.</summary>
    public static void Keep(IReadOnlyCollection<string> ids)
    {
        foreach (var id in Open.Keys)
            if (!ids.Contains(id)) Open.TryRemove(id, out _);
    }

    public static async Task<LoadedPresentation> LoadAsync(string path, int width, int height, CancellationToken ct)
    {
        try
        {
            Check(path);
            var deck = await LocalDeck.OpenAsync(IdFor(path), path, Version(path), width, height, ct);
            Open[deck.Id] = deck;
            return new LoadedPresentation(deck.Id, deck.Title, deck.Slides, deck.Version, deck.Media, PresentationLoader.CheckMediaAsync(deck.Media, ct));
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException($"{Path.GetFileName(path)} isn't a PowerPoint file, or it's damaged. Files with a password can't be opened.", ex);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException($"Couldn't read {Path.GetFileName(path)}. {ex.Message}", ex);
        }
    }

    public static async Task<DeckSummary> SummariseAsync(string path, CancellationToken ct)
    {
        Check(path);
        var title = Path.GetFileNameWithoutExtension(path);
        if (IsPdf(path)) return new DeckSummary(IdFor(path), title, (int)(await LocalDeck.OpenPdfAsync(path, ct)).PageCount, []);
        await using var file = OpenShared(path);
        var pages = PptxFile.Read(file);
        return new DeckSummary(IdFor(path), title, pages.Count, Enumerable.Range(1, pages.Count).Where(n => pages[n - 1].Hidden).ToList());
    }

    internal static bool IsPdf(string path) => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    private static void Check(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".ppt") throw new InvalidOperationException("That's the old PowerPoint format. Save it as .pptx or PDF and open that.");
        if (extension == ".key") throw new InvalidOperationException("Keynote files can't be opened. Export it as PDF or PowerPoint and open that.");
        if (extension != ".pdf" && !PowerPoint.Contains(extension)) throw new InvalidOperationException("Only PowerPoint (.pptx) and PDF files can be opened.");
        if (!File.Exists(path)) throw new InvalidOperationException($"{Path.GetFileName(path)} isn't there any more ({Path.GetDirectoryName(path)}).");
    }

    /// <summary>Gets the deck's pictures, those coming up first.</summary>
    internal static async Task LoadPicturesAsync(LoadedPresentation loaded, Func<int> current, Action changed, CancellationToken ct)
    {
        if (!Open.TryGetValue(loaded.Id, out var deck)) return;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var at = current();
            var next = Enumerable.Range(0, loaded.Slides.Count).Where(i => loaded.Slides[i].Picture.Quality < PictureQuality.Full)
                .OrderBy(i => i >= at ? i - at : (at - i) * 3).DefaultIfEmpty(-1).First();
            if (next < 0) return;
            if (loaded.Slides[next].Picture.Offer(await deck.PageAsync(next), PictureQuality.Full)) changed();
            else return; // couldn't be drawn
        }
    }

    public static void Map(IEndpointRouteBuilder app, Func<string, string, IResult> asset)
    {
        app.MapGet("/local/{secret}/d/{id}/{**what}", async (HttpContext ctx, string secret, string id, string what) =>
        {
            if (secret != Secret || !Open.TryGetValue(id, out var deck)) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "no-cache";
            if (what == "embed") return asset("local.html", "text/html; charset=utf-8");
            if (what == "clip") return asset("local-clip.html", "text/html; charset=utf-8");
            if (what == "deck.json") return Results.Json(deck.Describe());
            if (what.StartsWith("page/", StringComparison.Ordinal) && int.TryParse(what.AsSpan(5), out var page) && page >= 0 && page < deck.Slides.Count)
            {
                ctx.Response.Headers.CacheControl = "public, max-age=31536000, immutable"; // the address has the version
                return await deck.PageAsync(page) is { } png ? Results.Bytes(png, "image/png") : Results.NotFound();
            }
            if (what.StartsWith("media/", StringComparison.Ordinal) && deck.MediaFile(what[6..]) is var (file, type))
                return Results.File(file, type, enableRangeProcessing: true);
            return Results.NotFound();
        });
    }
}

/// <summary>A PDF or PowerPoint file that's open.</summary>
internal sealed class LocalDeck
{
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = "video/mp4", [".m4v"] = "video/mp4", [".mov"] = "video/mp4", [".webm"] = "video/webm", [".mkv"] = "video/webm", [".ogv"] = "video/ogg",
        [".mp3"] = "audio/mpeg", [".m4a"] = "audio/mp4", [".aac"] = "audio/aac", [".wav"] = "audio/wav", [".ogg"] = "audio/ogg", [".flac"] = "audio/flac",
    };

    public required string Id { get; init; }
    public required string Title { get; init; }
    public required long Version { get; init; }
    public required IReadOnlyList<DeckSlide> Slides { get; init; }
    public required IReadOnlyList<SlideMedia> Media { get; init; }

    private readonly ConcurrentDictionary<int, Lazy<Task<byte[]?>>> _pages = new();
    private readonly SemaphoreSlim _drawing = new(1, 1);
    private readonly Dictionary<string, (string File, string Type)> _files = [];
    private List<List<object>> _clips = []; // by slide, for the player
    private PdfDocument? _pdf;
    private IReadOnlyList<int>? _pdfPages; // the PDF's page for each slide, -1 if it has none
    private string? _pictures;
    private int _width, _height;

    public static async Task<LocalDeck> OpenAsync(string id, string path, long version, int width, int height, CancellationToken ct)
    {
        var title = Path.GetFileNameWithoutExtension(path);
        if (LocalDecks.IsPdf(path))
        {
            var pdf = await OpenPdfAsync(path, ct);
            var slides = Enumerable.Range(1, (int)pdf.PageCount).Select(n => new DeckSlide("p" + n, false, NoteText.Empty, new SlidePicture(), n, 0, false)).ToList();
            return new LocalDeck { Id = id, Title = title, Version = version, Slides = slides, Media = [], _pdf = pdf, _width = width, _height = height, _clips = slides.Select(_ => new List<object>()).ToList() };
        }

        var key = $"{id}-{version}";
        IReadOnlyList<PptxPage> pages;
        var media = new List<SlideMedia>();
        var files = new Dictionary<string, (string, string)>();
        var clips = new List<List<object>>();
        await using (var file = LocalDecks.OpenShared(path))
        {
            pages = PptxFile.Read(file);
            file.Seek(0, SeekOrigin.Begin);
            using var zip = new ZipArchive(file, ZipArchiveMode.Read);
            for (var i = 0; i < pages.Count; i++) clips.Add(Clips(zip, pages[i], i, path, key, files, media));
        }

        var number = 0;
        var deck = new LocalDeck
        {
            Id = id, Title = title, Version = version, Media = media, _width = width, _height = height, _clips = clips,
            Slides = pages.Select(p => new DeckSlide(p.Id, p.Hidden, p.Notes, new SlidePicture(), p.Hidden ? 0 : ++number,
                p.Clips.Count(c => c.Start == "on click"), p.Clips.Any(c => c.Start == "automatic"))).ToList(),
        };
        foreach (var (name, file) in files) deck._files[name] = file;

        var drawn = await PptxConverter.ConvertAsync(path, key, width, height, ct);
        if (Directory.Exists(drawn)) deck._pictures = drawn;
        else
        {
            deck._pdf = await OpenPdfAsync(drawn, ct);
            // Hidden slides may have been left out.
            var order = DeckAlignment.PdfPages(deck.Slides, (int)deck._pdf.PageCount);
            deck._pdfPages = Enumerable.Range(0, deck.Slides.Count).Select(slide => order.ToList().IndexOf(slide)).ToList();
        }
        return deck;
    }

    private static List<object> Clips(ZipArchive zip, PptxPage page, int slide, string path, string key, Dictionary<string, (string, string)> files, List<SlideMedia> media)
    {
        var list = new List<object>();
        foreach (var c in page.Clips.OrderBy(c => c.Order))
        {
            var youTube = PptxFile.YouTubeId(c.Link);
            var extension = Path.GetExtension(c.Entry ?? c.Link ?? "");
            string? name = null, access;
            if (youTube != null)
            {
                access = null;
            }
            else if (!Types.TryGetValue(extension, out var type))
            {
                access = "unsupported";
            }
            else if (c.Entry != null && zip.GetEntry(c.Entry) is { } entry)
            {
                name = Path.GetFileName(c.Entry);
                if (!files.ContainsKey(name)) files[name] = (Extract(entry, key, name), type);
                access = "ok";
            }
            else if (c.Link != null && Linked(c.Link, path) is { } linked)
            {
                name = $"linked{files.Count + 1}{extension.ToLowerInvariant()}";
                files[name] = (linked, type);
                access = "ok";
            }
            else
            {
                access = "missing";
            }

            var id = youTube ?? name ?? "";
            media.Add(new SlideMedia(slide + 1, page.Id, c.Kind, c.Name, id, c.Start, c.Muted, youTube != null ? "youtube" : "file", access));
            if (access is not (null or "ok")) continue;
            list.Add(new
            {
                kind = c.Kind, youTube, file = name, start = c.Start, muted = c.Muted, loop = c.Loop, volume = c.Volume,
                x = c.At.X, y = c.At.Y, w = c.At.Width, h = c.At.Height, from = c.TrimStart, cut = c.TrimEnd,
            });
        }
        return list;
    }

    /// <summary>A clip that's in a file of its own next to the presentation. Not over the network: opening it would sign in to whatever it names.</summary>
    private static string? Linked(string link, string path)
    {
        try
        {
            var file = Uri.TryCreate(link, UriKind.Absolute, out var uri) ? uri.IsFile && !uri.IsUnc ? uri.LocalPath : null : Path.Combine(Path.GetDirectoryName(path)!, Uri.UnescapeDataString(link));
            if (file == null || file.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            if (File.Exists(file)) return file;
            // It may have been moved along with the presentation.
            var beside = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileName(file));
            return File.Exists(beside) ? beside : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string Extract(ZipArchiveEntry entry, string key, string name)
    {
        var folder = Path.Combine(AppFolders.Local, "Media", key);
        var file = Path.Combine(folder, name);
        if (File.Exists(file) && new FileInfo(file).Length == entry.Length) return file;
        Directory.CreateDirectory(folder);
        entry.ExtractToFile(file, overwrite: true);
        // Other versions of the same file, and files that haven't been opened for a while.
        var deck = key[..key.LastIndexOf('-')];
        foreach (var old in new DirectoryInfo(Path.Combine(AppFolders.Local, "Media")).GetDirectories())
        {
            if (old.Name == key || (!old.Name.StartsWith(deck, StringComparison.Ordinal) && old.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-14))) continue;
            try { old.Delete(true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* still playing */ }
        }
        return file;
    }

    public (string File, string Type)? MediaFile(string name) => _files.TryGetValue(name, out var f) ? f : null;

    /// <summary>What the player page needs to know.</summary>
    public object Describe() => new
    {
        pages = Slides.Select((s, i) => (s, i)).Where(x => !x.s.Skipped)
            .Select(x => new { id = x.s.PageId, image = $"page/{x.i}?v={Version}", clips = _clips[x.i] }).ToList(),
    };

    internal static async Task<PdfDocument> OpenPdfAsync(string path, CancellationToken ct)
    {
        byte[] bytes;
        await using (var file = LocalDecks.OpenShared(path))
        {
            bytes = new byte[file.Length];
            await file.ReadExactlyAsync(bytes, ct);
        }
        var input = new InMemoryRandomAccessStream(); // the document reads from it as pages are drawn
        var writer = input.AsStreamForWrite();
        await writer.WriteAsync(bytes, ct);
        await writer.FlushAsync(ct);
        input.Seek(0);
        try
        {
            return await PdfDocument.LoadFromStreamAsync(input);
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x8007052B)) // wrong password
        {
            throw new InvalidOperationException("This PDF needs a password. Save a copy without one and open that.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("This PDF couldn't be read; it may be damaged.", ex);
        }
    }

    /// <summary>A slide's picture as PNG. Drawn the first time it's asked for.</summary>
    public Task<byte[]?> PageAsync(int slide) => _pages.GetOrAdd(slide, s => new Lazy<Task<byte[]?>>(() => Task.Run(() => DrawAsync(s)))).Value;

    private async Task<byte[]?> DrawAsync(int slide)
    {
        try
        {
            if (_pictures != null) return await File.ReadAllBytesAsync(Path.Combine(_pictures, slide + 1 + ".png"));
            var page = _pdfPages == null ? slide : _pdfPages[slide];
            if (page < 0 || _pdf == null) return null;
            await _drawing.WaitAsync();
            try { return await PresentationLoader.RenderPageAsync(_pdf, (uint)page, _width, _height); }
            finally { _drawing.Release(); }
        }
        catch (Exception)
        {
            return null;
        }
    }
}
