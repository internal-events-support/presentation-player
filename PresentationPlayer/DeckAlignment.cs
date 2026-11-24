using System.Text;
using System.Text.Json;

namespace PresentationPlayer;

/// <summary>A slide in the PPTX export: whether it's hidden ("skipped" in Google Slides) and its notes.</summary>
public sealed record PptxSlide(bool Hidden, NoteText Notes);

/// <summary>A slide in Google's player. ClickSteps is how many clicks its animations take.</summary>
public sealed record PlayerPage(string PageId, int ClickSteps, bool StartsByItself, bool Hidden = false, NoteText? Notes = null);

/// <summary>
/// A deck's slides come from several places that don't all include the skipped ones: the PPTX has
/// every slide and says which are hidden, the PDF and the player may or may not leave those out.
/// This lines them up.
/// </summary>
public static class DeckAlignment
{
    /// <param name="pptx">Every slide, in order.</param>
    /// <param name="pngs">The PDF's pages, with or without the hidden slides.</param>
    /// <param name="modelPageIds">Slide ids from the editor page, including hidden slides (may be empty).</param>
    /// <param name="player">The player's slides, with or without the hidden ones (may be empty).</param>
    public static IReadOnlyList<DeckSlide> Align(IReadOnlyList<PptxSlide> pptx, IReadOnlyList<byte[]> pngs,
        IReadOnlyList<string> modelPageIds, IReadOnlyList<PlayerPage> player)
    {
        var visibleCount = pptx.Count(s => !s.Hidden);
        // The PPTX doesn't match the PDF at all: trust the PDF, and nothing is known to be hidden.
        if (pptx.Count != pngs.Count && visibleCount != pngs.Count)
        {
            pptx = Enumerable.Range(0, pngs.Count).Select(i => i < pptx.Count ? pptx[i] with { Hidden = false } : new PptxSlide(false, NoteText.Empty)).ToList();
            visibleCount = pptx.Count;
        }
        var pdfHasHidden = pngs.Count == pptx.Count;
        var modelMatches = modelPageIds.Count == pptx.Count;
        var playerHasHidden = player.Count == pptx.Count;
        var playerMatches = playerHasHidden || player.Count == visibleCount;

        var result = new List<DeckSlide>(pptx.Count);
        var visible = 0;
        for (var i = 0; i < pptx.Count; i++)
        {
            var hidden = pptx[i].Hidden;
            var pdfIndex = pdfHasHidden ? i : hidden ? -1 : visible;
            var playerIndex = !playerMatches ? -1 : playerHasHidden ? i : hidden ? -1 : visible;
            // Matching by id is the surest, when both lists have them.
            if (modelMatches && player.Count > 0)
                playerIndex = IndexOf(player, modelPageIds[i]);

            var page = playerIndex >= 0 && !hidden ? player[playerIndex] : null;
            result.Add(new DeckSlide(
                page?.PageId ?? (modelMatches ? modelPageIds[i] : null),
                hidden,
                pptx[i].Notes,
                Picture(pdfIndex >= 0 && pdfIndex < pngs.Count ? pngs[pdfIndex] : null),
                hidden ? 0 : page != null ? playerIndex + 1 : visible + 1,
                page?.ClickSteps ?? 0,
                page?.StartsByItself ?? false));
            if (!hidden) visible++;
        }
        return result;
    }

    /// <summary>The slides from the player's page alone, which has everything but the pictures.</summary>
    public static IReadOnlyList<DeckSlide> FromPlayer(IReadOnlyList<PlayerPage> player) => player
        .Select((p, i) => new DeckSlide(p.PageId, p.Hidden, p.Notes ?? NoteText.Empty, new SlidePicture(), p.Hidden ? 0 : i + 1, p.ClickSteps, p.StartsByItself))
        .ToList();

    /// <summary>Which of the deck's slides each of the PDF's pages is. The PDF may leave out hidden slides.</summary>
    public static IReadOnlyList<int> PdfPages(IReadOnlyList<DeckSlide> slides, int pageCount)
    {
        var all = Enumerable.Range(0, slides.Count).ToList();
        var order = pageCount == slides.Count ? all : all.Where(i => !slides[i].Skipped).ToList();
        return order.Take(pageCount).ToList();
    }

    private static SlidePicture Picture(byte[]? png)
    {
        var picture = new SlidePicture();
        picture.Offer(png, PictureQuality.Full);
        return picture;
    }

    private static int IndexOf(IReadOnlyList<PlayerPage> player, string pageId)
    {
        for (var i = 0; i < player.Count; i++)
            if (player[i].PageId == pageId) return i;
        return -1;
    }

    /// <summary>
    /// Reads the slides from the player's page. Its data isn't documented; what it looks like:
    ///   var viewerData = {…, docData: [[width, height], [slide, …], …], …}
    ///   slide = [pageId, index, title, [audio, …], [video, …], …, [steps, startsByItself, ms], …, notesHtml, …]
    ///   with the animation at position 7, the notes at 9, and 0 at 12 when the slide is hidden. An audio
    ///   clip's position 5 is 1 when it plays by itself.
    /// </summary>
    public static IReadOnlyList<PlayerPage> ParsePlayerPages(string html)
    {
        try
        {
            const string key = "docData: ";
            var start = html.IndexOf(key, StringComparison.Ordinal);
            if (start < 0) return [];
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(html[(start + key.Length)..]), isFinalBlock: false, default);
            using var doc = JsonDocument.ParseValue(ref reader);
            var pages = new List<PlayerPage>();
            foreach (var slide in doc.RootElement[1].EnumerateArray())
            {
                var steps = 0;
                var byItself = false;
                if (slide.GetArrayLength() > 7 && slide[7] is { ValueKind: JsonValueKind.Array } anim && anim.GetArrayLength() > 1)
                {
                    steps = anim[0].ValueKind == JsonValueKind.Array ? anim[0].GetArrayLength() : 0;
                    byItself = steps > 0 && anim[1].ValueKind == JsonValueKind.True;
                }
                var clicks = byItself ? steps - 1 : steps;
                if (slide[3].ValueKind == JsonValueKind.Array)
                    foreach (var audio in slide[3].EnumerateArray())
                        byItself |= audio.ValueKind == JsonValueKind.Array && audio.GetArrayLength() > 5 &&
                                    audio[5].ValueKind == JsonValueKind.Number && audio[5].GetDouble() == 1;
                var notes = slide.GetArrayLength() > 9 && slide[9].ValueKind == JsonValueKind.String ? NoteText.FromHtml(slide[9].GetString()) : NoteText.Empty;
                var hidden = slide.GetArrayLength() > 12 && slide[12].ValueKind == JsonValueKind.Number && slide[12].GetDouble() == 0;
                pages.Add(new PlayerPage(slide[0].GetString() ?? "", clicks, byItself, hidden, notes));
            }
            return pages;
        }
        catch
        {
            return [];
        }
    }
}
