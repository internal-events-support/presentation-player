using System.Drawing.Text;

namespace PresentationPlayer;

/// <summary>
/// Wraps and draws notes, with bold and italic text (GDI+ can only draw one style at a time), starting
/// new lines between words. Words are measured once and scaled, so trying many sizes for auto-fit stays quick.
/// </summary>
public sealed class NotesLayout : IDisposable
{
    private const float ReferenceSize = 100;
    private const float SmallestForWholeWords = 24; // below this, an endless word is split rather than shrunk

    // A piece of a word in one style; a word is one or more pieces that stay together on a line.
    private sealed record Piece(string Text, FontStyle Style, float Width);
    private sealed record Word(List<Piece> Pieces, float Width, float SpaceAfter, bool EndsLine);

    private readonly StringFormat _format = new(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoClip | StringFormatFlags.NoWrap };
    private readonly Graphics _g;
    private readonly List<Word> _words = [];
    private readonly Dictionary<(string, FontStyle), float> _widths = [];

    public NotesLayout(Graphics g, NoteText notes)
    {
        _g = g;
        _g.TextRenderingHint = TextRenderingHint.AntiAlias; // widths that scale evenly with size
        Split(notes);
    }

    public void Dispose() => _format.Dispose();

    private static Font MakeFont(float size, FontStyle style) => new("Segoe UI", size, style, GraphicsUnit.Pixel);

    private float Measure(string text, FontStyle style)
    {
        if (_widths.TryGetValue((text, style), out var w)) return w;
        using var font = MakeFont(ReferenceSize, style);
        return _widths[(text, style)] = _g.MeasureString(text, font, PointF.Empty, _format).Width;
    }

    /// <summary>Scripts that need shaping or run right to left, which this layout doesn't do.</summary>
    public static bool CanLayOut(string text) => !text.Any(c => c is >= '\u0590' and <= '\u109F' or >= '\u1780' and <= '\u18AF' or >= '\uFB1D' and <= '\uFEFC');

    // Chinese, Japanese and Korean have no spaces between words: lines can start at any character.
    private static bool IsCjk(char c) => c is >= '\u2E80' and <= '\u9FFF' or >= '\uAC00' and <= '\uD7AF' or >= '\uF900' and <= '\uFAFF' or >= '\uFF00' and <= '\uFFEF';

    // Where a line may start inside a run of text without spaces: after a hyphen, and in long web addresses and the like.
    private static bool CanBreakBefore(string token, int i)
    {
        char before = token[i - 1], at = token[i];
        if (IsCjk(before) || IsCjk(at)) return !"、。，．）」』】！？：；".Contains(at);
        if (before is '-' or '\u2013' or '\u2014') return i > 1 && char.IsLetter(at);
        return token.Length > 20 && "/?&=_.".Contains(before) && !"/?&=_.".Contains(at);
    }

    private void Split(NoteText notes)
    {
        var styles = new FontStyle[notes.Text.Length];
        foreach (var s in notes.Styles)
            Array.Fill(styles, (s.Bold ? FontStyle.Bold : 0) | (s.Italic ? FontStyle.Italic : 0), s.Start, s.Length);

        var text = notes.Text;
        var i = 0;
        while (i < text.Length)
        {
            var pieces = new List<Piece>();
            var tokenStart = i;
            while (i < text.Length && text[i] is not (' ' or '\t' or '\n' or '\v')) i++;
            var token = text[tokenStart..i];
            for (var at = 0; at < token.Length;)
            {
                if (at > 0 && CanBreakBefore(token, at) && pieces.Count > 0)
                {
                    _words.Add(new Word(pieces, pieces.Sum(p => p.Width), 0, false));
                    pieces = [];
                }
                var start = at++;
                var style = styles[tokenStart + start];
                while (at < token.Length && styles[tokenStart + at] == style && !CanBreakBefore(token, at)) at++;
                var part = token[start..at];
                pieces.Add(new Piece(part, style, Measure(part, style)));
            }
            var space = 0f;
            while (i < text.Length && text[i] is ' ' or '\t')
            {
                space += Measure(" ", styles[i]) * (text[i] == '\t' ? 4 : 1);
                i++;
                if (i >= text.Length) break;
            }
            var ends = i < text.Length && text[i] is '\n' or '\v';
            if (ends) i++;
            _words.Add(new Word(pieces, pieces.Sum(p => p.Width), space, ends));
        }
    }

    // Widths here are in reference units: multiply by size / ReferenceSize for pixels.
    private IEnumerable<(Piece Piece, float X, int Line)> Place(float maxWidth)
    {
        float x = 0;
        var line = 0;
        foreach (var word in _words)
        {
            if (x > 0 && x + word.Width > maxWidth) { x = 0; line++; }
            foreach (var piece in word.Pieces)
            {
                if (word.Width <= maxWidth || x + piece.Width <= maxWidth || piece.Text.Length == 1)
                {
                    yield return (piece, x, line);
                    x += piece.Width;
                    continue;
                }
                // Too long for a line even at a small size, with nowhere to break: split it.
                var rest = piece.Text;
                while (rest.Length > 0)
                {
                    var n = rest.Length;
                    while (n > 1 && x + Measure(rest[..n], piece.Style) > maxWidth) n--;
                    var w = Measure(rest[..n], piece.Style);
                    if (n == 1 && x > 0 && x + w > maxWidth) { x = 0; line++; continue; }
                    yield return (new Piece(rest[..n], piece.Style, w), x, line);
                    rest = rest[n..];
                    if (rest.Length > 0) { x = 0; line++; } else x += w;
                }
            }
            x += word.SpaceAfter;
            if (word.EndsLine) { x = 0; line++; }
        }
    }

    private static float LineHeight(Graphics g, float size)
    {
        using var font = MakeFont(size, FontStyle.Regular);
        return font.GetHeight(g);
    }

    // GDI+ insets plain notes by a sixth of their size each side; do the same so the edge doesn't move between slides.
    private static float Inset(float size) => size / 6;
    private static float TextWidth(float size, float width) => Math.Max(1, width - Inset(size) * 2);

    public float Height(float size, float width)
    {
        var lines = 1;
        foreach (var (_, _, line) in Place(TextWidth(size, width) * ReferenceSize / size)) lines = line + 1;
        return lines * LineHeight(_g, size);
    }

    /// <summary>The size at which the longest word only just fits on a line, so none has to be split.</summary>
    public float WholeWordSize(float width)
    {
        var longest = _words.Count > 0 ? _words.Max(w => w.Width) : 0;
        // From TextWidth(size, width) * ReferenceSize / size >= longest.
        var size = MathF.Floor(width * ReferenceSize / (longest + ReferenceSize / 3 + 0.5f));
        return size >= SmallestForWholeWords ? size : float.MaxValue;
    }

    /// <summary>The largest whole size at which the notes fit the area without scrolling (or the smallest allowed).</summary>
    public float LargestFitting(SizeF area, float min, float max)
    {
        int lo = (int)Math.Ceiling(min), hi = (int)Math.Floor(Math.Min(max, WholeWordSize(area.Width)));
        if (Height(lo, area.Width) > area.Height) return lo;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (Height(mid, area.Width) <= area.Height) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    public void Draw(Brush brush, float size, RectangleF area, float scroll)
    {
        var scale = size / ReferenceSize;
        var lineHeight = LineHeight(_g, size);
        var fonts = new Dictionary<FontStyle, Font>();
        try
        {
            foreach (var (piece, x, line) in Place(TextWidth(size, area.Width) / scale))
            {
                var y = area.Y - scroll + line * lineHeight;
                if (y + lineHeight < area.Y) continue;
                if (y > area.Bottom) break;
                if (!fonts.TryGetValue(piece.Style, out var font)) fonts[piece.Style] = font = MakeFont(size, piece.Style);
                _g.DrawString(piece.Text, font, brush, area.X + Inset(size) + x * scale, y, _format);
            }
        }
        finally
        {
            foreach (var f in fonts.Values) f.Dispose();
        }
    }
}
