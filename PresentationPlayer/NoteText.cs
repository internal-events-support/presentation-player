using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace PresentationPlayer;

/// <summary>A stretch of bold and/or italic text in the notes.</summary>
public sealed record NoteStyle(int Start, int Length, bool Bold, bool Italic);

/// <summary>Speaker notes: the plain text plus where it's bold or italic.</summary>
public sealed partial record NoteText(string Text, IReadOnlyList<NoteStyle> Styles)
{
    public static readonly NoteText Empty = new("", []);

    [GeneratedRegex(@"<(?<close>/?)(?<tag>[a-zA-Z0-9]+)(?<attrs>[^>]*)>|[^<]+")]
    private static partial Regex HtmlRegex();

    [GeneratedRegex(@"font-weight:\s*(?<w>[a-z0-9]+)")]
    private static partial Regex WeightRegex();

    [GeneratedRegex(@"font-style:\s*(?<s>[a-z]+)")]
    private static partial Regex StyleRegex();

    /// <summary>Notes as the player has them: paragraphs of text with styled spans.</summary>
    public static NoteText FromHtml(string? html)
    {
        if (string.IsNullOrEmpty(html)) return Empty;
        var text = new Builder();
        var styles = new Stack<(string Tag, bool Bold, bool Italic)>();
        foreach (Match m in HtmlRegex().Matches(html))
        {
            if (!m.Groups["tag"].Success)
            {
                var (_, bold, italic) = styles.Count > 0 ? styles.Peek() : default;
                text.Append(WebUtility.HtmlDecode(m.Value).Replace("\u200B", "").Replace('\u00A0', ' '), bold, italic);
                continue;
            }
            var tag = m.Groups["tag"].Value.ToLowerInvariant();
            if (m.Groups["close"].Length > 0)
            {
                while (styles.Count > 0 && styles.Pop().Tag != tag) { }
                continue;
            }
            if (tag == "br") { text.Append("\n"); continue; }
            if (tag is "p" or "li" && !text.IsEmpty) text.Append("\n");
            var (_, b, i) = styles.Count > 0 ? styles.Peek() : default;
            if (tag is "b" or "strong") b = true;
            if (tag is "i" or "em") i = true;
            var attrs = m.Groups["attrs"].Value;
            if (WeightRegex().Match(attrs) is { Success: true } w)
                b = w.Groups["w"].Value is "bold" || (int.TryParse(w.Groups["w"].Value, out var weight) && weight >= 600);
            if (StyleRegex().Match(attrs) is { Success: true } s) i = s.Groups["s"].Value is "italic" or "oblique";
            styles.Push((tag, b, i));
        }
        return text.Build();
    }

    /// <summary>Collects runs of text, then trims the ends like <see cref="string.Trim()"/>.</summary>
    public sealed class Builder
    {
        private readonly StringBuilder _text = new();
        private readonly List<NoteStyle> _styles = [];

        public bool IsEmpty => _text.Length == 0;

        public void Append(string? text, bool bold = false, bool italic = false)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (bold || italic)
            {
                if (_styles.Count > 0 && _styles[^1] is var last && last.Start + last.Length == _text.Length && last.Bold == bold && last.Italic == italic)
                    _styles[^1] = last with { Length = last.Length + text.Length };
                else
                    _styles.Add(new NoteStyle(_text.Length, text.Length, bold, italic));
            }
            _text.Append(text);
        }

        public NoteText Build()
        {
            var all = _text.ToString();
            var text = all.Trim();
            if (text.Length == 0) return Empty;
            var cut = all.Length - all.TrimStart().Length;
            var styles = new List<NoteStyle>();
            foreach (var s in _styles)
            {
                var start = Math.Max(0, s.Start - cut);
                var end = Math.Min(text.Length, s.Start + s.Length - cut);
                if (end > start) styles.Add(s with { Start = start, Length = end - start });
            }
            return new NoteText(text, styles);
        }
    }
}
