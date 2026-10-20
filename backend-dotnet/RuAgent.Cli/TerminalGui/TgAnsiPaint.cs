using System.Text;
using RuAgent.Cli.Tui;
using Terminal.Gui.Drawing;
using Attr = Terminal.Gui.Drawing.Attribute;

namespace RuAgent.Cli.TerminalGui;

/// <summary>One styled run inside a transcript row; <see cref="Text"/> holds visible runes only.</summary>
public sealed record TgSpan(string Text, Attr Attr);

/// <summary>
/// Imports legacy command and diagnostic output into Terminal.Gui spans. Interactive
/// transcript events supply native attributes directly; older TextWriter callers may
/// still paint through <see cref="TuiPalette"/> (fg per colour level, bold, dim, reset).
/// Unknown escape sequences are dropped
/// invisibly; an input with no escapes yields null spans so plain rows keep their old path.
/// Spans always cover the whole visible text of the row they belong to, which is what makes
/// wrapping able to slice them by character range.
/// </summary>
internal static class TgAnsiPaint
{
    private static readonly (int R, int G, int B) DefaultInk = TuiPalette.Rgb(TuiToken.Ink);

    /// <summary>Splits <paramref name="text"/> into visible characters and styled runs.</summary>
    public static (string Clean, IReadOnlyList<TgSpan>? Spans) Parse(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('\u001b'))
        {
            return (text, null);
        }

        var clean = new StringBuilder(text.Length);
        var spans = new List<TgSpan>();
        var current = new StringBuilder();
        var fg = DefaultInk;
        var bg = (0, 0, 0);
        var bold = false;
        var dim = false;
        var stylesSeen = false;

        void Flush()
        {
            if (current.Length == 0)
            {
                return;
            }

            var attr = BuildAttr(fg, bg, bold, dim);
            var part = current.ToString();
            current.Clear();
            if (spans.Count > 0 && spans[^1].Attr.Equals(attr))
            {
                spans[^1] = spans[^1] with { Text = spans[^1].Text + part };
                return;
            }

            spans.Add(new TgSpan(part, attr));
        }

        var index = 0;
        while (index < text.Length)
        {
            if (text[index] == '\u001b')
            {
                var next = SkipEscape(text, index, fg, bg, bold, dim, out var style, out var newFg, out var newBg, out var newBold, out var newDim);
                if (style)
                {
                    Flush();
                    stylesSeen = true;
                    fg = newFg;
                    bg = newBg;
                    bold = newBold;
                    dim = newDim;
                }

                index = next;
                continue;
            }

            if (text[index] == '\r')
            {
                index++;
                continue;
            }

            var (slice, consumed, _) = ReadRuneText(text, index);
            clean.Append(slice);
            current.Append(slice);
            index += consumed;
        }

        Flush();
        // Escapes that never touched SGR (cursor moves, OSC) left no paint state behind:
        // the row keeps its plain single-attribute path instead of a degenerate span list.
        return (clean.ToString(), stylesSeen && spans.Count > 0 ? spans : null);
    }

    private static Attr BuildAttr((int R, int G, int B) fg, (int R, int G, int B) bg, bool bold, bool dim)
    {
        var foreground = dim ? Blend(fg, bg) : TgPalette.ToColor(fg);
        var background = TgPalette.ToColor(bg);
        // Terminal.Gui carries bold as a text style; SGR dim has no style flag, so it is
        // approximated by blending the foreground halfway toward the row background.
        return bold ? new Attr(foreground, background, TextStyle.Bold) : new Attr(foreground, background);
    }

    private static Color Blend((int R, int G, int B) fg, (int R, int G, int B) bg) =>
        TgPalette.ToColor(((fg.R + bg.R) / 2, (fg.G + bg.G) / 2, (fg.B + bg.B) / 2));

    /// <summary>
    /// Skips one escape sequence. Sequence types that would change the paint state (final
    /// byte <c>m</c>) hand back the new state instead of mutating shared fields, so the
    /// parser loop stays re-entrant with a plain ReadonlySpan of the row under parse.
    /// </summary>
    private static int SkipEscape(
        string text,
        int index,
        (int R, int G, int B) fg,
        (int R, int G, int B) bg,
        bool bold,
        bool dim,
        out bool changedStyle,
        out (int R, int G, int B) newFg,
        out (int R, int G, int B) newBg,
        out bool newBold,
        out bool newDim)
    {
        changedStyle = false;
        newFg = fg;
        newBg = bg;
        newBold = bold;
        newDim = dim;
        if (index + 1 >= text.Length)
        {
            return text.Length;
        }

        var marker = text[index + 1];
        if (marker == '[')
        {
            var cursor = index + 2;
            while (cursor < text.Length && text[cursor] is >= ' ' and <= '?')
            {
                cursor++;
            }

            if (cursor >= text.Length)
            {
                return text.Length;
            }

            var final = text[cursor];
            if (final == 'm')
            {
                changedStyle = true;
                ApplySgr(
                    text.AsSpan(index + 2, cursor - index - 2),
                    ref newFg, ref newBg, ref newBold, ref newDim);
            }

            return cursor + 1;
        }

        if (marker == ']')
        {
            var cursor = index + 2;
            while (cursor < text.Length)
            {
                if (text[cursor] == '\a')
                {
                    return cursor + 1;
                }

                if (text[cursor] == '\u001b' && cursor + 1 < text.Length && text[cursor + 1] == '\\')
                {
                    return cursor + 2;
                }

                cursor++;
            }

            return text.Length;
        }

        return index + 2;
    }

    private static void ApplySgr(
        ReadOnlySpan<char> parameters,
        ref (int R, int G, int B) fg,
        ref (int R, int G, int B) bg,
        ref bool bold,
        ref bool dim)
    {
        // An empty parameter list is a reset: ESC[m equals ESC[0m.
        var parts = parameters.ToString().Split(';');
        for (var i = 0; i < parts.Length; i++)
        {
            var code = ParseParam(parts[i]);
            switch (code)
            {
                case 0:
                    fg = DefaultInk;
                    bg = (0, 0, 0);
                    bold = false;
                    dim = false;
                    break;
                case 1:
                    bold = true;
                    break;
                case 2:
                    dim = true;
                    break;
                case 22:
                    bold = false;
                    dim = false;
                    break;
                case >= 30 and <= 37:
                    fg = Basic16(code - 30);
                    break;
                case >= 40 and <= 47:
                    bg = Basic16(code - 40);
                    break;
                case >= 90 and <= 97:
                    fg = Basic16(code - 90 + 8);
                    break;
                case >= 100 and <= 107:
                    bg = Basic16(code - 100 + 8);
                    break;
                case 38:
                case 48:
                    var setForeground = code == 38;
                    if (i + 2 < parts.Length && ParseParam(parts[i + 1]) == 2 && i + 4 < parts.Length)
                    {
                        var rgb = (ParseParam(parts[i + 2]), ParseParam(parts[i + 3]), ParseParam(parts[i + 4]));
                        if (setForeground) fg = rgb; else bg = rgb;
                        i += 4;
                    }
                    else if (i + 2 < parts.Length && ParseParam(parts[i + 1]) == 5)
                    {
                        var rgb = Palette256(ParseParam(parts[i + 2]));
                        if (setForeground) fg = rgb; else bg = rgb;
                        i += 2;
                    }

                    break;
            }
        }
    }

    private static int ParseParam(string part)
    {
        var value = 0;
        foreach (var ch in part)
        {
            if (ch is < '0' or > '9')
            {
                return -1;
            }

            value = value * 10 + (ch - '0');
            if (value > 1_000_000)
            {
                return -1;
            }
        }

        return value;
    }

    private static (int R, int G, int B) Palette256(int index) => index switch
    {
        >= 0 and <= 15 => Basic16(index),
        <= 231 => Cube(index - 16),
        _ => Gray(index),
    };

    private static (int R, int G, int B) Cube(int cell)
    {
        (int R, int G, int B) Channel(int v) => v == 0 ? (0, 0, 0) : (55 + 40 * v, 55 + 40 * v, 55 + 40 * v);
        var r = Channel(cell / 36).R;
        var g = Channel((cell % 36) / 6).G;
        var b = Channel(cell % 6).B;
        return (r, g, b);
    }

    private static (int R, int G, int B) Gray(int index)
    {
        var level = 8 + (index - 232) * 10;
        return (level, level, level);
    }

    private static (int R, int G, int B) Basic16(int index) => index switch
    {
        0 => (0, 0, 0),
        1 => (205, 0, 0),
        2 => (0, 205, 0),
        3 => (205, 205, 0),
        4 => (0, 0, 238),
        5 => (205, 0, 205),
        6 => (0, 205, 205),
        7 => (229, 229, 229),
        8 => (127, 127, 127),
        9 => (255, 0, 0),
        10 => (0, 255, 0),
        11 => (255, 255, 0),
        12 => (92, 92, 255),
        13 => (255, 0, 255),
        14 => (0, 255, 255),
        _ => (255, 255, 255),
    };

    private static (string Slice, int Consumed, int CodePoint) ReadRuneText(string text, int index)
    {
        var (codePoint, consumed) = ReadRune(text, index);
        return (text.Substring(index, consumed), consumed, codePoint);
    }

    // ------------------------------------------------------------------
    // The data-tone pass. A free model may answer with important numbers and file
    // names in bare prose; the deterministic decorator tints exactly those runs so
    // they read as data wherever the renderer itself left them unpainted.
    // ------------------------------------------------------------------

    private static readonly System.Text.RegularExpressions.Regex NumberPattern =
        new(@"\b\d{1,3}(?:,\d{3})+\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex FilePathPattern =
        new(@"\b[A-Za-z][\w.-]{0,63}\.(?:md|json|jsonl|cs|py|ps1|cmd|bat|txt|yml|yaml|toml|ini|log)\b",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Splits runs at comma-grouped numbers and file-path-like tokens, tinting
    /// them with distinct accents — but only inside the default-to-ink runs. Anything the
    /// renderer already painted (bold, code, heading, notice) passes through untouched,
    /// and the visible text is never changed, only the paint boundaries.</summary>
    public static IReadOnlyList<TgSpan>? Decorate(string text, IReadOnlyList<TgSpan>? spans)
    {
        if (string.IsNullOrEmpty(text))
        {
            return spans;
        }

        var matches = new List<(int Start, int End, Attr Accent)>();
        var numberAccent = TgPalette.Attr(TuiToken.Brand);
        var pathAccent = TgPalette.Attr(TuiToken.Brand2);
        foreach (System.Text.RegularExpressions.Match match in NumberPattern.Matches(text))
        {
            matches.Add((match.Index, match.Index + match.Length, numberAccent));
        }

        foreach (System.Text.RegularExpressions.Match match in FilePathPattern.Matches(text))
        {
            matches.Add((match.Index, match.Index + match.Length, pathAccent));
        }

        if (matches.Count == 0)
        {
            return spans;
        }

        matches.Sort((left, right) => left.Start.CompareTo(right.Start));
        var source = spans is { Count: > 0 } ? spans : [new TgSpan(text, TgPalette.Attr(TuiToken.Ink))];
        var tinted = TintRuns(source, text, matches);
        return MergeAdjacent(tinted);
    }

    private static List<TgSpan> TintRuns(
        IReadOnlyList<TgSpan> source,
        string text,
        List<(int Start, int End, Attr Accent)> matches)
    {
        var ink = TgPalette.Attr(TuiToken.Ink);
        var result = new List<TgSpan>(source.Count + matches.Count);
        var cursor = 0;
        foreach (var span in source)
        {
            var spanStart = cursor;
            var spanEnd = cursor + span.Text.Length;
            cursor = spanEnd;
            if (!span.Attr.Equals(ink))
            {
                result.Add(span);
                continue;
            }

            var position = spanStart;
            foreach (var (start, end, accent) in matches)
            {
                var low = Math.Max(start, spanStart);
                var high = Math.Min(end, spanEnd);
                if (high <= low)
                {
                    continue;
                }

                if (low > position)
                {
                    result.Add(new TgSpan(span.Text[(position - spanStart)..(low - spanStart)], span.Attr));
                }

                result.Add(new TgSpan(text[low..high], accent));
                position = high;
            }

            if (position < spanEnd)
            {
                result.Add(new TgSpan(span.Text[(position - spanStart)..], span.Attr));
            }
        }

        return result;
    }

    private static List<TgSpan> MergeAdjacent(List<TgSpan> spans)
    {
        var merged = new List<TgSpan>(spans.Count);
        foreach (var span in spans)
        {
            if (span.Text.Length == 0)
            {
                continue;
            }

            if (merged.Count > 0 && merged[^1].Attr.Equals(span.Attr))
            {
                merged[^1] = merged[^1] with { Text = merged[^1].Text + span.Text };
                continue;
            }

            merged.Add(span);
        }

        return merged;
    }

    private static (int CodePoint, int Consumed) ReadRune(string text, int index)
    {
        if (char.IsSurrogatePair(text, index))
        {
            return (char.ConvertToUtf32(text, index), 2);
        }

        if (!char.IsSurrogate(text[index]))
        {
            return (text[index], 1);
        }

        return (0xFFFD, 1);
    }

    /// <summary>
    /// A wrapped row and the slice of its source spans that covers it. The slice preserves
    /// each span's attribute, so re-wrapping a painted row on resize keeps the paint aligned.
    /// </summary>
    internal sealed record WrappedSpans(string Row, IReadOnlyList<TgSpan> Spans);

    /// <summary>Splits paragraphs on newlines, then wraps each on word boundaries. Unlike
    /// <see cref="TuiText.Wrap"/> the rows keep their original spaces, which keeps every
    /// span slice aligned to its source range; a span never straddles two rows.</summary>
    public static List<WrappedSpans> WordWrapSpans(string text, IReadOnlyList<TgSpan> spans, int width)
    {
        var rows = new List<WrappedSpans>();
        if (string.IsNullOrEmpty(text) || width <= 0)
        {
            return rows;
        }

        foreach (var (paragraph, offset) in Paragraphs(text))
        {
            if (paragraph.Length == 0)
            {
                rows.Add(new WrappedSpans(string.Empty, []));
                continue;
            }

            WrapParagraphWords(spans, offset, paragraph, width, rows);
        }

        return rows;
    }

    /// <summary>Same paragraph split, then a fixed-column split that never drops characters.</summary>
    public static List<WrappedSpans> HardWrapSpans(string text, IReadOnlyList<TgSpan> spans, int width)
    {
        var rows = new List<WrappedSpans>();
        if (string.IsNullOrEmpty(text) || width <= 0)
        {
            return rows;
        }

        foreach (var (paragraph, offset) in Paragraphs(text))
        {
            if (paragraph.Length == 0)
            {
                rows.Add(new WrappedSpans(string.Empty, []));
                continue;
            }

            var index = 0;
            var start = 0;
            var used = 0;
            while (index < paragraph.Length)
            {
                var (codePoint, consumed) = ReadRune(paragraph, index);
                var runeWidth = TuiText.RuneWidth(codePoint);
                if (used + runeWidth > width && index > start)
                {
                    rows.Add(Slice(paragraph, spans, offset + start, offset + index));
                    start = index;
                    used = 0;
                }

                used += runeWidth;
                index += consumed;
            }

            rows.Add(Slice(paragraph, spans, offset + start, offset + paragraph.Length));
        }

        return rows;
    }

    private static void WrapParagraphWords(
        IReadOnlyList<TgSpan> spans, int paragraphSourceOffset, string paragraph, int width, List<WrappedSpans> rows)
    {
        var index = 0;
        var rowStart = -1;
        var rowEnd = 0;
        var rowWidth = 0;

        void Commit(int endColumn)
        {
            rows.Add(Slice(paragraph, spans, paragraphSourceOffset + rowStart, paragraphSourceOffset + endColumn));
            rowStart = -1;
            rowWidth = 0;
        }

        while (index <= paragraph.Length)
        {
            if (index == paragraph.Length)
            {
                break;
            }

            if (paragraph[index] == ' ')
            {
                var spaceStart = index;
                while (index < paragraph.Length && paragraph[index] == ' ')
                {
                    index++;
                }

                if (rowStart >= 0)
                {
                    // Between-word spaces stay measured so rows stay honest about width;
                    // Slice keeps them in the row text, matching the source exactly.
                    rowWidth += index - spaceStart;
                }

                continue;
            }

            var wordStart = index;
            while (index < paragraph.Length && paragraph[index] != ' ')
            {
                index++;
            }

            var word = paragraph[wordStart..index];
            var wordWidth = TuiText.VisibleWidth(word);
            if (rowStart >= 0 && rowWidth + wordWidth > width)
            {
                Commit(rowEnd);
            }

            if (rowStart < 0)
            {
                rowStart = wordStart;
                rowWidth = wordWidth;
                rowEnd = index;
            }
            else
            {
                rowWidth += wordWidth;
                rowEnd = index;
            }

            if (wordWidth > width)
            {
                // A word wider than the line hard-splits mid-word, like TuiText.Wrap.
                var wordIndex = wordStart;
                var chunkWidth = 0;
                var chunkStart = wordStart;
                while (wordIndex < index)
                {
                    var (codePoint, consumed) = ReadRune(paragraph, wordIndex);
                    var runeWidth = TuiText.RuneWidth(codePoint);
                    if (rowStart >= 0 && chunkWidth + runeWidth > width)
                    {
                        rows.Add(Slice(paragraph, spans, paragraphSourceOffset + chunkStart, paragraphSourceOffset + wordIndex));
                        chunkStart = wordIndex;
                        chunkWidth = 0;
                        rowStart = wordIndex;
                    }

                    chunkWidth += runeWidth;
                    wordIndex += consumed;
                }

                rowStart = chunkStart;
                rowEnd = index;
                rowWidth = chunkWidth;
            }
        }

        if (rowStart >= 0)
        {
            Commit(rowEnd);
        }
    }

    private static IEnumerable<(string Paragraph, int SourceOffset)> Paragraphs(string text)
    {
        var start = 0;
        while (start <= text.Length)
        {
            var newline = text.IndexOf('\n', start);
            if (newline < 0)
            {
                yield return (text[start..], start);
                yield break;
            }

            yield return (text[start..newline], start);
            start = newline + 1;
            if (start == text.Length)
            {
                yield return (string.Empty, start);
                yield break;
            }
        }
    }

    private static WrappedSpans Slice(string source, IReadOnlyList<TgSpan> spans, int start, int end)
    {
        var covered = 0;
        var builder = new StringBuilder(end - start);
        var slices = new List<TgSpan>();
        foreach (var span in spans)
        {
            var spanStart = covered;
            var spanEnd = covered + span.Text.Length;
            covered = spanEnd;
            if (spanEnd <= start || spanStart >= end)
            {
                continue;
            }

            var from = Math.Max(start, spanStart) - spanStart;
            var to = Math.Min(end, spanEnd) - spanStart;
            if (to <= from)
            {
                continue;
            }

            var part = span.Text[from..to];
            builder.Append(part);
            var attr = span.Attr;
            if (slices.Count > 0 && slices[^1].Attr.Equals(attr))
            {
                slices[^1] = slices[^1] with { Text = slices[^1].Text + part };
            }
            else
            {
                slices.Add(new TgSpan(part, attr));
            }
        }

        // A row whose spans under-cover its text falls back to its source characters
        // painted in ink, so nothing disappears even if the span invariant is violated.
        var row = builder.ToString();
        if (row.Length != end - start)
        {
            return new WrappedSpans(source[start..end], [new TgSpan(source[start..end], TgPalette.Attr(TuiToken.Ink))]);
        }

        return new WrappedSpans(row, slices);
    }
}
