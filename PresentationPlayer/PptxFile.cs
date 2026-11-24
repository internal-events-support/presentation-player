using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PresentationPlayer;

/// <summary>A video or audio clip on a slide. The position is in fractions of the slide.</summary>
/// <param name="ShapeId">The shape's id on its slide.</param>
/// <param name="Entry">Where the clip is inside the file, or null when it isn't in there.</param>
/// <param name="Link">Where it is otherwise: a web address or another file.</param>
/// <param name="Start">"automatic", "on click" or "manual".</param>
/// <param name="TrimStart">Seconds cut from the start; TrimEnd from the end.</param>
internal sealed record PptxClip(int ShapeId, string Kind, string Name, string? Entry, string? Link, RectangleF At, string Start, int Order,
    bool Muted, bool Loop, double Volume, double TrimStart, double TrimEnd);

internal sealed record PptxPage(string Id, bool Hidden, NoteText Notes, IReadOnlyList<PptxClip> Clips);

/// <summary>
/// What's read from a PowerPoint file directly: its slides in order, which are hidden, the notes, and
/// the video and audio. The pictures of the slides come from PowerPoint or LibreOffice (<see cref="PptxConverter"/>).
/// </summary>
internal static partial class PptxFile
{
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace P14 = "http://schemas.microsoft.com/office/powerpoint/2010/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static IReadOnlyList<PptxPage> Read(Stream file)
    {
        using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        var presentation = PresentationLoader.LoadXml(zip, "ppt/presentation.xml")?.Root
            ?? throw new InvalidOperationException("This isn't a PowerPoint file, or it's damaged.");
        var size = presentation.Element(P + "sldSz");
        var (width, height) = (Number(size, "cx") ?? 12192000, Number(size, "cy") ?? 6858000);
        var rels = Relationships(zip, "ppt/presentation.xml");

        var pages = new List<PptxPage>();
        foreach (var id in presentation.Element(P + "sldIdLst")?.Elements(P + "sldId") ?? [])
        {
            if ((string?)id.Attribute(R + "id") is not { } rid || !rels.TryGetValue(rid, out var target)) continue;
            var path = target.Target;
            if (PresentationLoader.LoadXml(zip, path)?.Root is not { } slide) continue;
            var slideRels = Relationships(zip, path);

            var notes = NoteText.Empty;
            var notesPath = slideRels.Values.FirstOrDefault(r => !r.External && r.Target.Contains("notesSlides/", StringComparison.OrdinalIgnoreCase)).Target;
            if (notesPath != null && PresentationLoader.LoadXml(zip, notesPath) is { } notesXml)
                notes = PresentationLoader.ExtractNotesText(notesXml);

            IReadOnlyList<PptxClip> clips;
            try { clips = Clips(slide, slideRels, width, height); }
            catch (Exception) { clips = []; } // the slide is still worth showing
            pages.Add(new PptxPage("s" + (string?)id.Attribute("id"), (string?)slide.Attribute("show") is "0" or "false", notes, clips));
        }
        return pages;
    }

    private static IReadOnlyList<PptxClip> Clips(XElement slide, Dictionary<string, (string Target, bool External)> rels, double width, double height)
    {
        var starts = Starts(slide);
        var clips = new List<PptxClip>();
        foreach (var pic in slide.Descendants(P + "pic"))
        {
            var nvPr = pic.Element(P + "nvPicPr")?.Element(P + "nvPr");
            var video = nvPr?.Element(A + "videoFile");
            var audio = nvPr?.Elements().FirstOrDefault(e => e.Name == A + "audioFile" || e.Name == A + "wavAudioFile");
            if ((video ?? audio) is not { } file) continue;

            // Newer files say where the clip is in an extension; the other is for older versions of PowerPoint.
            var media = nvPr!.Descendants(P14 + "media").FirstOrDefault();
            var rid = (string?)media?.Attribute(R + "embed") ?? (string?)media?.Attribute(R + "link") ?? (string?)file.Attribute(R + "link") ?? (string?)file.Attribute(R + "embed");
            if (rid == null || !rels.TryGetValue(rid, out var target)) continue;

            var c = pic.Element(P + "nvPicPr")!.Element(P + "cNvPr");
            var id = (int)(Number(c, "id") ?? 0);
            var (start, order) = starts.TryGetValue(id, out var s) ? s : ("manual", 0);
            var node = slide.Descendants(P + "cMediaNode").FirstOrDefault(m => Target(m) == id);
            var trim = media?.Element(P14 + "trim");
            var name = (string?)c?.Attribute("name") ?? "";
            clips.Add(new PptxClip(id, video != null ? "video" : "audio", name,
                target.External ? null : target.Target, target.External ? target.Target : null,
                Place(pic, width, height), start, order,
                (string?)node?.Attribute("mute") is "1" or "true",
                (string?)node?.Element(P + "cTn")?.Attribute("repeatCount") == "indefinite",
                Math.Clamp((Number(node, "vol") ?? 100000) / 100000, 0, 1),
                (Number(trim, "st") ?? 0) / 1000, (Number(trim, "end") ?? 0) / 1000));
        }
        return clips;
    }

    /// <summary>
    /// How each clip starts. The slide's main sequence is a list of clicks, each with the effects it
    /// sets off; the ones set off without a click are in there too, before the first click effect.
    /// </summary>
    private static Dictionary<int, (string Start, int Order)> Starts(XElement slide)
    {
        var starts = new Dictionary<int, (string, int)>();
        var main = slide.Element(P + "timing")?.Descendants(P + "cTn").FirstOrDefault(t => (string?)t.Attribute("nodeType") == "mainSeq");
        var order = 0;
        foreach (var step in main?.Element(P + "childTnLst")?.Elements() ?? [])
        {
            order++;
            var onClick = step.Descendants(P + "cTn").Any(t => (string?)t.Attribute("nodeType") == "clickEffect");
            foreach (var cmd in step.Descendants(P + "cmd"))
                if (((string?)cmd.Attribute("cmd"))?.StartsWith("play", StringComparison.OrdinalIgnoreCase) == true && Target(cmd) is { } id)
                    starts.TryAdd(id, (onClick ? "on click" : "automatic", order));
        }
        return starts;
    }

    private static int? Target(XElement e) =>
        e.Descendants(P + "spTgt").Select(t => (int?)Number(t, "spid")).FirstOrDefault();

    /// <summary>Where a picture is on the slide, through any groups it's in.</summary>
    private static RectangleF Place(XElement pic, double width, double height)
    {
        var xfrm = pic.Element(P + "spPr")?.Element(A + "xfrm");
        if (xfrm == null) return new RectangleF(0, 0, 1, 1); // it takes its place from the layout
        var (x, y, w, h) = (Number(xfrm.Element(A + "off"), "x") ?? 0, Number(xfrm.Element(A + "off"), "y") ?? 0, Number(xfrm.Element(A + "ext"), "cx") ?? width, Number(xfrm.Element(A + "ext"), "cy") ?? height);
        foreach (var group in pic.Ancestors(P + "grpSp"))
        {
            var g = group.Element(P + "grpSpPr")?.Element(A + "xfrm");
            if (g == null || Number(g.Element(A + "chExt"), "cx") is not > 0 || Number(g.Element(A + "chExt"), "cy") is not > 0) continue;
            var (sx, sy) = ((Number(g.Element(A + "ext"), "cx") ?? 0) / Number(g.Element(A + "chExt"), "cx")!.Value, (Number(g.Element(A + "ext"), "cy") ?? 0) / Number(g.Element(A + "chExt"), "cy")!.Value);
            x = (Number(g.Element(A + "off"), "x") ?? 0) + (x - (Number(g.Element(A + "chOff"), "x") ?? 0)) * sx;
            y = (Number(g.Element(A + "off"), "y") ?? 0) + (y - (Number(g.Element(A + "chOff"), "y") ?? 0)) * sy;
            (w, h) = (w * sx, h * sy);
        }
        return new RectangleF((float)(x / width), (float)(y / height), (float)(w / width), (float)(h / height));
    }

    private static double? Number(XElement? e, string attribute) =>
        double.TryParse((string?)e?.Attribute(attribute), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static Dictionary<string, (string Target, bool External)> Relationships(ZipArchive zip, string part)
    {
        var dir = part[..(part.LastIndexOf('/') + 1)];
        var rels = new Dictionary<string, (string, bool)>();
        foreach (var r in PresentationLoader.LoadXml(zip, $"{dir}_rels/{part[dir.Length..]}.rels")?.Root?.Elements(Rel + "Relationship") ?? [])
        {
            var (id, target, external) = ((string?)r.Attribute("Id"), (string?)r.Attribute("Target"), (string?)r.Attribute("TargetMode") == "External");
            if (id != null && target != null) rels[id] = (external ? target : PresentationLoader.ResolvePath(dir, target), external);
        }
        return rels;
    }

    [GeneratedRegex(@"(?:youtube(?:-nocookie)?\.com/(?:embed/|v/|watch\?(?:.*&)?v=)|youtu\.be/)([A-Za-z0-9_-]{11})")]
    private static partial Regex YouTubeRegex();

    /// <summary>The id of a YouTube video, or null for any other link.</summary>
    public static string? YouTubeId(string? link) => link != null && YouTubeRegex().Match(link) is { Success: true } m ? m.Groups[1].Value : null;
}
