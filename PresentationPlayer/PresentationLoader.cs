using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace PresentationPlayer;

/// <summary>
/// A deck as opened. Slides are all of its slides, including skipped (hidden) ones; their pictures
/// come later (<see cref="PresentationLoader.LoadPicturesAsync"/>). MediaAccess is Media once it's
/// known whether each clip can be played.
/// </summary>
public sealed record LoadedPresentation(string Id, string Title, IReadOnlyList<DeckSlide> Slides, long? Revision, IReadOnlyList<SlideMedia> Media,
    Task<IReadOnlyList<SlideMedia>> MediaAccess);

/// <summary>What the master deck editor shows about a deck.</summary>
public sealed record DeckSummary(string Id, string Title, int Slides, IReadOnlyList<int> Skipped);

/// <summary>
/// Downloads a Google Slides presentation without OAuth by using Google's export endpoints:
/// the PDF export provides the slide images and the PPTX export provides the speaker notes.
/// The presentation must be shared as "Anyone with the link can view", or with the Google
/// account signed in through <see cref="Google"/>, whose cookies are then sent along.
/// </summary>
public static partial class PresentationLoader
{
    private static readonly GoogleCookieHandler Cookies = new();
    private static readonly HttpClient Http = CreateClient();

    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>The signed-in Google account, if the app has one.</summary>
    public interface IGoogleSession
    {
        bool SignedIn { get; }
        /// <summary>Google has ended the session, so the account has to sign in again.</summary>
        bool Expired { get; }
        string? Email { get; }
        /// <summary>Checks whether Google still accepts the session.</summary>
        Task CheckAsync();
        Task<IReadOnlyList<Cookie>> GetCookiesAsync();
    }

    public static IGoogleSession? Google { get; set; }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(Cookies) { Timeout = TimeSpan.FromSeconds(120) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) PresentationPlayer/1.0");
        return client;
    }

    [GeneratedRegex(@"/presentation/d/([a-zA-Z0-9_-]{20,})")]
    private static partial Regex UrlIdRegex();

    [GeneratedRegex(@"^[a-zA-Z0-9_-]{20,}$")]
    private static partial Regex BareIdRegex();

    /// <summary>The presentation's id, from an edit/view URL, a bare id or the path of a file.</summary>
    public static string? ExtractId(string urlOrId)
    {
        urlOrId = urlOrId.Trim();
        if (LocalDecks.PathOf(urlOrId) is { } path) return LocalDecks.IdFor(path);
        if (urlOrId.Contains("/presentation/d/e/"))
            return null; // "Publish to web" URLs can't be exported; need the normal share link.
        var m = UrlIdRegex().Match(urlOrId);
        if (m.Success) return m.Groups[1].Value;
        return BareIdRegex().IsMatch(urlOrId) ? urlOrId : null;
    }

    public static async Task<LoadedPresentation> LoadAsync(string url, int width, int height, CancellationToken ct = default)
    {
        if (LocalDecks.PathOf(url) is { } path) return await LocalDecks.LoadAsync(path, width, height, ct);
        try
        {
            return await OpenAsync(url, width, height, ct);
        }
        catch (NoAccessException) when (Google is { SignedIn: true } google)
        {
            // Being refused may mean Google has just ended the session; then say that instead.
            await google.CheckAsync();
            if (!google.Expired) throw;
            return await OpenAsync(url, width, height, ct); // signed out it may be public; otherwise the error says to sign in
        }
    }

    private static async Task<LoadedPresentation> OpenAsync(string url, int width, int height, CancellationToken ct)
    {
        var id = ExtractId(url) ?? throw new ArgumentException(
            "That doesn't look like a Google Slides link (https://docs.google.com/presentation/d/<id>/edit) " +
            "or the path of a PowerPoint or PDF file.");

        await Cookies.SyncAsync(Google);
        // The player's page has the slides and notes, the editor's page the title, revision and media.
        // Both are small, so the deck opens quickly; the pictures follow.
        var pageTask = FetchPageInfoAsync(id, ct);
        var player = await FetchPlayerPagesAsync(id, ct);
        var page = await pageTask;

        IReadOnlyList<DeckSlide> slides;
        var title = page.Title;
        if (player.Count > 0)
        {
            slides = DeckAlignment.FromPlayer(player);
        }
        else
        {
            // Couldn't read the player's page: fall back to the exports, which also say why if there's no access.
            var baseUrl = $"https://docs.google.com/presentation/d/{id}/export";
            var pdfTask = DownloadAsync($"{baseUrl}/pdf", "%PDF"u8.ToArray(), ct);
            var pptxTask = DownloadAsync($"{baseUrl}/pptx", "PK"u8.ToArray(), ct);
            await Task.WhenAll(pdfTask, pptxTask);
            var pngs = new List<byte[]>();
            await RenderPdfAsync(pdfTask.Result, width, height, (_, png) => pngs.Add(png), ct);
            title ??= ParsePptxTitle(pptxTask.Result);
            slides = DeckAlignment.Align(ParsePptxNotes(pptxTask.Result), pngs, page.PageIds, player);
        }

        ReuseCachedPictures(id, page.Revision, slides);
        return new LoadedPresentation(id, string.IsNullOrWhiteSpace(title) ? "Untitled presentation" : title, slides, page.Revision, page.Media,
            CheckMediaAsync(page.Media, ct));
    }

    // Pictures of recently opened decks, by deck and revision, so going back to an unchanged deck is instant.
    private const int CachedDecks = 8;
    private static readonly List<(string Id, long Revision, Dictionary<string, SlidePicture> Pictures)> PictureCache = [];

    private static void ReuseCachedPictures(string id, long? revision, IReadOnlyList<DeckSlide> slides)
    {
        lock (PictureCache)
        {
            foreach (var (cachedId, cachedRevision, pictures) in PictureCache)
            {
                if (cachedId != id) continue;
                // Pictures from another revision stand in until the new ones arrive.
                foreach (var s in slides)
                    if (s.PageId != null && pictures.TryGetValue(s.PageId, out var p))
                        s.Picture.Offer(p.Data, cachedRevision == revision ? p.Quality : PictureQuality.Old);
            }
        }
    }

    private static void CachePictures(LoadedPresentation deck)
    {
        if (deck.Revision is not { } revision) return;
        var pictures = deck.Slides.Where(s => s.PageId != null && s.Picture.Quality >= PictureQuality.Preview)
            .GroupBy(s => s.PageId!).ToDictionary(g => g.Key, g => g.First().Picture);
        if (pictures.Count == 0) return;
        lock (PictureCache)
        {
            PictureCache.RemoveAll(c => c.Id == deck.Id);
            PictureCache.Insert(0, (deck.Id, revision, pictures));
            if (PictureCache.Count > CachedDecks) PictureCache.RemoveRange(CachedDecks, PictureCache.Count - CachedDecks);
        }
    }

    // Google only allows a handful of exports of a deck at once ("429 Too Many Requests").
    private const int PreviewWorkers = 2;
    private const int PreviewsAhead = 5;

    /// <summary>
    /// Gets the deck's pictures. Google takes a while to make the PDF with all of them, so meanwhile the
    /// slides next to <paramref name="current"/> are fetched one by one at a lower resolution.
    /// </summary>
    public static async Task LoadPicturesAsync(LoadedPresentation deck, int width, int height, Func<int> current, Action changed, CancellationToken ct)
    {
        if (deck.Slides.All(s => s.Picture.Quality == PictureQuality.Full)) return;
        if (LocalDecks.IsLocal(deck.Id))
        {
            await LocalDecks.LoadPicturesAsync(deck, current, changed, ct);
            return;
        }
        var taken = new HashSet<int>();
        var pdfFailed = false;
        long quietUntil = 0;
        using var previews = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // The PDF is asked for first: it's the one request that brings everything.
        var pdfTask = DownloadAsync($"https://docs.google.com/presentation/d/{deck.Id}/export/pdf", "%PDF"u8.ToArray(), ct, retry: true);
        var previewTask = Task.WhenAll(Enumerable.Range(0, PreviewWorkers).Select(_ => PreviewsAsync(previews.Token)));
        try
        {
            var pdf = await pdfTask;
            IReadOnlyList<int>? places = null;
            await RenderPdfAsync(pdf, width, height, (page, png) =>
            {
                if (places != null && page < places.Count && deck.Slides[places[page]].Picture.Offer(png, PictureQuality.Full)) changed();
            }, ct, pages => places = DeckAlignment.PdfPages(deck.Slides, pages));
            // Hidden slides may not be in the PDF; their previews are all there is.
            if (deck.Slides.All(s => s.Picture.Quality >= PictureQuality.Preview)) previews.Cancel();
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // Downloading may be turned off for this deck: get every slide's preview instead.
        }
        finally
        {
            Volatile.Write(ref pdfFailed, true); // from here on, whatever is still missing is up to the previews
            CachePictures(deck); // even if another deck is being opened before they've all arrived
        }
        try { await previewTask; } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        CachePictures(deck);

        async Task PreviewsAsync(CancellationToken stop)
        {
            var refusals = 0;
            while (refusals < 12)
            {
                int place;
                bool finishing;
                lock (taken)
                {
                    finishing = Volatile.Read(ref pdfFailed);
                    var at = current();
                    place = Enumerable.Range(0, deck.Slides.Count)
                        .Where(i => !taken.Contains(i) && deck.Slides[i].PageId != null && deck.Slides[i].Picture.Quality < PictureQuality.Preview)
                        .Where(i => finishing || (i >= at - 1 && i <= at + PreviewsAhead))
                        .OrderBy(i => i >= at ? i - at : (at - i) * 3) // what's coming up matters most
                        .DefaultIfEmpty(-1).First();
                    if (place >= 0) taken.Add(place);
                }
                if (place < 0)
                {
                    if (finishing) return;
                    await Task.Delay(400, stop); // the PDF is on its way; the operator may move on meanwhile
                    continue;
                }

                var wait = Interlocked.Read(ref quietUntil) - Environment.TickCount64;
                if (wait > 0) await Task.Delay((int)wait, stop);
                var slide = deck.Slides[place];
                try
                {
                    using var resp = await Http.GetAsync(
                        $"https://docs.google.com/presentation/d/{deck.Id}/export/jpeg?id={deck.Id}&pageid={Uri.EscapeDataString(slide.PageId!)}", stop);
                    if (IsBusy(resp.StatusCode))
                    {
                        refusals++;
                        Interlocked.Exchange(ref quietUntil, Environment.TickCount64 + Math.Min(15000, 1500 * refusals));
                        lock (taken) taken.Remove(place); // it'll be tried again
                        continue;
                    }
                    if (!resp.IsSuccessStatusCode || resp.Content.Headers.ContentType?.MediaType is not ("image/jpeg" or "image/png")) continue;
                    if (slide.Picture.Offer(await resp.Content.ReadAsByteArrayAsync(stop), PictureQuality.Preview)) changed();
                }
                catch (HttpRequestException) { /* try the next one */ }
                catch (TaskCanceledException) when (!stop.IsCancellationRequested) { /* timed out */ }
            }
        }
    }

    /// <summary>Google is asking to slow down, or having a moment: worth trying again shortly.</summary>
    private static bool IsBusy(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout;

    /// <summary>Loads every deck of a master deck (a few at a time), in order.</summary>
    public static async Task<IReadOnlyList<LoadedPresentation>> LoadPartsAsync(MasterDeck master, int width, int height, CancellationToken ct = default)
    {
        if (master.Parts.Count == 0) throw new InvalidOperationException("This master deck has no presentations in it yet.");
        using var gate = new SemaphoreSlim(6);
        // The same deck can be in there more than once; it only needs downloading once.
        var byId = new Dictionary<string, Task<LoadedPresentation>>();
        var tasks = new List<Task<LoadedPresentation>>();
        foreach (var (part, i) in master.Parts.Select((p, i) => (p, i)))
        {
            var id = ExtractId(part.Url) ?? throw new InvalidOperationException($"Presentation {i + 1} of this master deck isn't a Google Slides link or a file.");
            if (!byId.TryGetValue(id, out var task))
                byId[id] = task = LoadGatedAsync(part, i);
            tasks.Add(task);
        }
        return await Task.WhenAll(tasks);

        async Task<LoadedPresentation> LoadGatedAsync(MasterPart part, int i)
        {
            await gate.WaitAsync(ct);
            try { return await LoadAsync(part.Url, width, height, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var name = string.IsNullOrWhiteSpace(part.Title) ? $"presentation {i + 1}" : $"“{part.Title}”";
                var message = $"Couldn't load {name} from the master deck. {ex.Message}";
                if (ex is NoAccessException) throw new NoAccessException(message, ExtractId(part.Url));
                throw new InvalidOperationException(message, ex);
            }
            finally { gate.Release(); }
        }
    }

    /// <summary>A deck's title, how many slides it has and which are skipped, without downloading it.</summary>
    public static async Task<DeckSummary> SummariseAsync(string url, CancellationToken ct = default)
    {
        if (LocalDecks.PathOf(url) is { } path) return await LocalDecks.SummariseAsync(path, ct);
        var id = ExtractId(url) ?? throw new ArgumentException("That doesn't look like a Google Slides link.");
        await Cookies.SyncAsync(Google);
        var pageTask = FetchPageInfoAsync(id, ct);
        var pages = await FetchPlayerPagesAsync(id, ct);
        var page = await pageTask;
        var count = pages.Count > 0 ? pages.Count : page.PageIds.Count;
        if (count == 0) throw NoAccess(id);
        return new DeckSummary(id, page.Title ?? "Untitled presentation", count,
            Enumerable.Range(1, pages.Count).Where(n => pages[n - 1].Hidden).ToList());
    }

    internal static async Task<IReadOnlyList<SlideMedia>> CheckMediaAsync(IReadOnlyList<SlideMedia> media, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var checks = media
            .Where(m => m.FileId != null && m.Access == null)
            .Select(m => (m.Source, Id: m.FileId!))
            .Distinct()
            .ToDictionary(k => k, k => CheckAccessAsync(k.Source, k.Id, timeout.Token));
        try { await Task.WhenAll(checks.Values); } catch { /* individual failures are reported as null */ }
        return media
            .Select(m => m.FileId != null && m.Access == null && checks.TryGetValue((m.Source, m.FileId), out var t) && t.IsCompletedSuccessfully
                ? m with { Access = t.Result }
                : m)
            .ToList();
    }

    /// <summary>
    /// Whether a clip will play for the signed-in account (or anyone, when signed out). Slides plays
    /// Drive videos and audio only for people who can open the file itself, whoever can open the deck.
    /// </summary>
    private static async Task<string?> CheckAccessAsync(string source, string fileId, CancellationToken ct)
    {
        if (source == "youtube")
        {
            // oEmbed answers 401 when embedding is turned off and 403/404 for private or removed videos.
            var url = "https://www.youtube.com/oembed?format=json&url=" +
                Uri.EscapeDataString("https://www.youtube.com/watch?v=" + fileId);
            using var yt = await Http.GetAsync(url, ct);
            return (int)yt.StatusCode switch
            {
                200 => "ok",
                401 => "not embeddable",
                403 or 404 => "private",
                _ => null,
            };
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://drive.google.com/file/d/{Uri.EscapeDataString(fileId)}/view");
        req.Headers.AcceptLanguage.ParseAdd("en-US");
        using var resp = await Http.SendAsync(req, ct);
        var status = (int)resp.StatusCode;
        if (status is 401 or 403 or 404) return "no access";
        if (status != 200) return null;
        // A sign-in page instead of the file also means no access.
        var host = resp.RequestMessage?.RequestUri?.Host ?? "";
        return host.StartsWith("accounts.", StringComparison.OrdinalIgnoreCase) ? "no access" : "ok";
    }

    private static async Task<byte[]> DownloadAsync(string url, byte[] magic, CancellationToken ct, bool retry = false)
    {
        using var resp = await GetAsync(url, retry ? 8 : 2, ct);
        if (resp.StatusCode is HttpStatusCode.NotFound)
        {
            if (Google?.Expired == true) throw NoAccess();
            // Signed in, Google hides decks the account can't open behind a 404.
            if (Google?.SignedIn == true)
                throw new NoAccessException("Presentation not found. Check the link, and that it's shared with " + AccountName() + ".");
            throw new InvalidOperationException("Presentation not found (404). Check the link.");
        }
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw NoAccess();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Google returned {(int)resp.StatusCode} {resp.ReasonPhrase}. Try again in a moment.");

        // Without access Google answers with a sign-in or "request access" page instead of the file.
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length < magic.Length || !bytes.AsSpan(0, magic.Length).SequenceEqual(magic))
            throw NoAccess();
        return bytes;
    }

    private static async Task<HttpResponseMessage> GetAsync(string url, int attempts, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var resp = await Http.GetAsync(url, ct);
            if (!IsBusy(resp.StatusCode) || attempt >= attempts) return resp;
            resp.Dispose();
            await Task.Delay(Math.Min(20000, 1000 * (1 << attempt)), ct);
        }
    }

    private static string AccountName() => Google?.Email ?? "the Google account you're signed in with";

    /// <summary>The deck exists (or may) but the signed-in account, or anyone without signing in, can't open it.</summary>
    public sealed class NoAccessException(string message, string? deckId = null) : InvalidOperationException(message)
    {
        /// <summary>The deck, when it's one of several.</summary>
        public string? DeckId { get; } = deckId;
    }

    private static NoAccessException NoAccess(string? deckId = null) => new(Google?.Expired == true
        ? $"Google has signed {AccountName()} out, so this presentation can't be opened. Sign in again to open it."
        : Google?.SignedIn == true
        ? $"{AccountName()} can't open this presentation. Ask the owner to share it with that account, " +
          "or to share it as \"Anyone with the link can view\"."
        : "This presentation isn't shared publicly. Share it as \"Anyone with the link can view\", " +
          "or sign in to a Google account that can open it (Settings → Google account).", deckId);

    /// <summary>
    /// Sends the signed-in account's Google cookies with every request, redirects included.
    /// Redirects are followed here so the cookie jar can be swapped between loads (sign in/out).
    /// </summary>
    private sealed class GoogleCookieHandler() : DelegatingHandler(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All })
    {
        private volatile CookieContainer _jar = new();

        public async Task SyncAsync(IGoogleSession? session)
        {
            var jar = new CookieContainer();
            if (session?.SignedIn == true)
            {
                foreach (var c in await session.GetCookiesAsync())
                {
                    try { jar.Add(c); } catch (CookieException) { /* skip odd cookies */ }
                }
            }
            _jar = jar;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            for (var hop = 0; ; hop++)
            {
                var jar = _jar;
                var uri = request.RequestUri!;
                request.Headers.Remove("Cookie");
                if (jar.GetCookieHeader(uri) is { Length: > 0 } header) request.Headers.Add("Cookie", header);

                var resp = await base.SendAsync(request, ct);
                if (resp.Headers.TryGetValues("Set-Cookie", out var setCookies))
                {
                    foreach (var sc in setCookies)
                    {
                        try { jar.SetCookies(uri, sc); } catch (CookieException) { /* ignore */ }
                    }
                }

                var status = (int)resp.StatusCode;
                if (status is not (301 or 302 or 303 or 307 or 308) || resp.Headers.Location is not { } location || hop >= 10)
                    return resp;
                resp.Dispose();
                var next = new HttpRequestMessage(status == 303 ? HttpMethod.Get : request.Method, new Uri(uri, location));
                foreach (var h in request.Headers.Where(h => h.Key != "Cookie")) next.Headers.TryAddWithoutValidation(h.Key, h.Value);
                request = next;
            }
        }
    }

    private static async Task RenderPdfAsync(byte[] pdf, int width, int height, Action<int, byte[]> rendered, CancellationToken ct, Action<int>? opened = null)
    {
        using var input = new InMemoryRandomAccessStream();
        var writer = input.AsStreamForWrite();
        await writer.WriteAsync(pdf);
        await writer.FlushAsync();
        input.Seek(0);

        var doc = await PdfDocument.LoadFromStreamAsync(input);
        opened?.Invoke((int)doc.PageCount);
        for (uint i = 0; i < doc.PageCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            rendered((int)i, await RenderPageAsync(doc, i, width, height));
        }
    }

    /// <summary>A page of a PDF as PNG, as large as fits.</summary>
    internal static async Task<byte[]> RenderPageAsync(PdfDocument doc, uint index, int width, int height)
    {
        using var page = doc.GetPage(index);
        var size = page.Size;
        var scale = Math.Min(width / size.Width, height / size.Height);
        var options = new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, Math.Round(size.Width * scale)),
            DestinationHeight = (uint)Math.Max(1, Math.Round(size.Height * scale)),
        };

        using var output = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(output, options); // PNG by default
        output.Seek(0);
        using var ms = new MemoryStream();
        await output.AsStreamForRead().CopyToAsync(ms);
        return ms.ToArray();
    }

    [GeneratedRegex(@"<title>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"""revision"":(\d+)")]
    private static partial Regex RevisionRegex();

    /// <summary>
    /// The deck's current revision number, which Google bumps on every edit. Null if the page
    /// couldn't be read. Used to spot decks that changed since they were loaded.
    /// </summary>
    public static async Task<long?> FetchRevisionAsync(string id, CancellationToken ct = default) =>
        LocalDecks.IsLocal(id) ? LocalDecks.Revision(id) : (await FetchPageInfoAsync(id, ct)).Revision;

    private sealed record PageInfo(string? Title, long? Revision, IReadOnlyList<SlideMedia> Media, IReadOnlyList<string> PageIds);

    /// <summary>
    /// The slides as Google's player has them, from the data embedded in its page: their ids, how many
    /// clicks their animations take, and whether something starts by itself. Empty if it can't be read.
    /// </summary>
    private static async Task<IReadOnlyList<PlayerPage>> FetchPlayerPagesAsync(string id, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://docs.google.com/presentation/d/{id}/embed?start=false");
            req.Headers.AcceptLanguage.ParseAdd("en-US");
            using var resp = await Http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode ? DeckAlignment.ParsePlayerPages(await resp.Content.ReadAsStringAsync(ct)) : [];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Reads the presentation's normal page for its name (from the page title, "Name - Google Slides")
    /// and its revision number (the page embeds "revision":N in its model data).
    /// </summary>
    private static async Task<PageInfo> FetchPageInfoAsync(string id, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://docs.google.com/presentation/d/{id}/edit");
            req.Headers.AcceptLanguage.ParseAdd("en-US");
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return new(null, null, [], []);
            var html = await resp.Content.ReadAsStringAsync(ct);
            return new(ParseTitle(html), ParseRevision(html), SlideMediaParser.Parse(html), SlideMediaParser.PageIds(html));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(null, null, [], []);
        }
    }

    private static string? ParseTitle(string html)
    {
        var m = TitleRegex().Match(html);
        if (!m.Success) return null;
        var title = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
        // Drop the product suffix, which Google localises ("- Google Slides", "- Presentaciones de Google", …).
        var cut = title.LastIndexOf(" - ", StringComparison.Ordinal);
        if (cut > 0 && title[(cut + 3)..].Contains("Google", StringComparison.OrdinalIgnoreCase)) title = title[..cut];
        return title.Length == 0 || title.Equals("Google Slides", StringComparison.OrdinalIgnoreCase) ? null : title;
    }

    // The page repeats the revision once per model chunk; take the highest.
    private static long? ParseRevision(string html)
    {
        long? revision = null;
        foreach (Match m in RevisionRegex().Matches(html))
            if (long.TryParse(m.Groups[1].ValueSpan, out var r) && r > (revision ?? -1)) revision = r;
        return revision;
    }

    private static string? ParsePptxTitle(byte[] pptx)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(pptx), ZipArchiveMode.Read);
            XNamespace dc = "http://purl.org/dc/elements/1.1/";
            return LoadXml(zip, "docProps/core.xml")?.Root?.Element(dc + "title")?.Value.Trim();
        }
        catch
        {
            return null;
        }
    }

    internal static List<PptxSlide> ParsePptxNotes(byte[] pptx)
    {
        using var zip = new ZipArchive(new MemoryStream(pptx), ZipArchiveMode.Read);

        var presentation = LoadXml(zip, "ppt/presentation.xml")
            ?? throw new InvalidOperationException("Invalid PPTX export (missing presentation.xml).");
        var presRels = LoadRels(zip, "ppt/presentation.xml");

        var result = new List<PptxSlide>();
        var slideIds = presentation.Root?.Element(P + "sldIdLst")?.Elements(P + "sldId") ?? [];
        foreach (var sldId in slideIds)
        {
            var rid = (string?)sldId.Attribute(R + "id");
            if (rid == null || !presRels.TryGetValue(rid, out var slidePath)) continue;

            var slide = LoadXml(zip, slidePath);
            var hidden = (string?)slide?.Root?.Attribute("show") is "0" or "false";

            var notes = NoteText.Empty;
            var notesPath = LoadRels(zip, slidePath)
                .Values.FirstOrDefault(p => p.Contains("notesSlides/", StringComparison.OrdinalIgnoreCase));
            if (notesPath != null && LoadXml(zip, notesPath) is { } notesXml)
                notes = ExtractNotesText(notesXml);

            result.Add(new PptxSlide(hidden, notes));
        }
        return result;
    }

    internal static NoteText ExtractNotesText(XDocument notesXml)
    {
        // The speaker notes live in the shape whose placeholder type is "body".
        var bodyShape = notesXml.Descendants(P + "sp").FirstOrDefault(sp =>
            (string?)sp.Element(P + "nvSpPr")?.Element(P + "nvPr")?.Element(P + "ph")?.Attribute("type") == "body");
        var txBody = bodyShape?.Element(P + "txBody");
        if (txBody == null) return NoteText.Empty;

        var text = new NoteText.Builder();
        foreach (var para in txBody.Elements(A + "p"))
        {
            if (!text.IsEmpty) text.Append("\n");
            foreach (var node in para.Elements())
            {
                if (node.Name == A + "r" || node.Name == A + "fld")
                    text.Append((string?)node.Element(A + "t"), IsOn(node, "b"), IsOn(node, "i"));
                else if (node.Name == A + "br")
                    text.Append("\n");
            }
        }
        return text.Build();

        static bool IsOn(XElement run, string attribute) =>
            (string?)run.Element(A + "rPr")?.Attribute(attribute) is "1" or "true";
    }

    internal static XDocument? LoadXml(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);
        if (entry == null) return null;
        using var s = entry.Open();
        return XDocument.Load(s);
    }

    /// <summary>Returns relationship Id -> absolute part path for the given part.</summary>
    private static Dictionary<string, string> LoadRels(ZipArchive zip, string partPath)
    {
        var dir = Path.GetDirectoryName(partPath)?.Replace('\\', '/') ?? "";
        var relsPath = $"{dir}/_rels/{Path.GetFileName(partPath)}.rels";
        var rels = new Dictionary<string, string>();
        var doc = LoadXml(zip, relsPath);
        if (doc?.Root == null) return rels;

        foreach (var r in doc.Root.Elements(Rel + "Relationship"))
        {
            var id = (string?)r.Attribute("Id");
            var target = (string?)r.Attribute("Target");
            if (id == null || target == null || (string?)r.Attribute("TargetMode") == "External") continue;
            rels[id] = ResolvePath(dir, target);
        }
        return rels;
    }

    internal static string ResolvePath(string baseDir, string target)
    {
        if (target.StartsWith('/')) return target.TrimStart('/');
        var parts = new List<string>(baseDir.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var seg in target.Split('/'))
        {
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (seg != "." && seg != "") parts.Add(seg);
        }
        return string.Join('/', parts);
    }
}
