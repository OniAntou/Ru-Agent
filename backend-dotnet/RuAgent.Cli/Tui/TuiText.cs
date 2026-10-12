using System.Globalization;
using System.Text;

namespace RuAgent.Cli.Tui;

/// <summary>
/// Terminal text measurement. Every structural line the TUI paints is measured and clipped
/// with these helpers so borders stay aligned for combining marks, CJK, emoji and ANSI
/// styling alike.
/// </summary>
public static class TuiText
{
    public static int VisibleWidth(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var width = 0;
        var index = 0;
        while (index < text.Length)
        {
            if (text[index] == '\u001b')
            {
                index = SkipAnsi(text, index);
                continue;
            }

            var codePoint = ReadCodePoint(text, ref index);
            width += RuneWidth(codePoint);
        }

        return width;
    }

    public static string Strip(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (!text.Contains('\u001b'))
        {
            return text;
        }

        var builder = new System.Text.StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            if (text[index] == '\u001b')
            {
                index = SkipAnsi(text, index);
                continue;
            }

            builder.Append(text[index]);
            index++;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Display cells for one code point: 0 for combining/format/control, 2 for wide and
    /// emoji ranges, 1 otherwise. East-Asian ambiguous characters count as 1, matching the
    /// default (non-CJK) terminal setting this TUI targets.
    /// </summary>
    public static int RuneWidth(int codePoint)
    {
        if (codePoint == 0)
        {
            return 0;
        }

        if (codePoint < 32 || codePoint == 127)
        {
            return 0;
        }

        if (IsZeroWidth(codePoint))
        {
            return 0;
        }

        return IsWide(codePoint) ? 2 : 1;
    }

    private static bool IsZeroWidth(int codePoint)
    {
        if (codePoint is >= 0x0300 and <= 0x036F
            or >= 0x0483 and <= 0x0489
            or >= 0x0591 and <= 0x05BD
            or >= 0x0610 and <= 0x061A
            or >= 0x064B and <= 0x065F
            or >= 0x0E31 and <= 0x0E3A
            or >= 0x0E47 and <= 0x0E4E
            or >= 0x1AB0 and <= 0x1AFF
            or >= 0x1DC0 and <= 0x1DFF
            or >= 0x200B and <= 0x200F
            or >= 0x202A and <= 0x202E
            or >= 0x2060 and <= 0x2064
            or 0xFEFF
            or >= 0x20D0 and <= 0x20FF
            or >= 0xFE00 and <= 0xFE0F
            or >= 0xFE20 and <= 0xFE2F
            or >= 0xE0100 and <= 0xE01EF)
        {
            return true;
        }

        var category = CharUnicodeInfo.GetUnicodeCategory(codePoint);
        return category is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.EnclosingMark
            or UnicodeCategory.Format;
    }

    private static bool IsWide(int codePoint) => codePoint
        is >= 0x1100 and <= 0x115F
        or >= 0x2E80 and <= 0x303E
        or >= 0x3041 and <= 0x33FF
        or >= 0x3400 and <= 0x4DBF
        or >= 0x4E00 and <= 0x9FFF
        or >= 0xA000 and <= 0xA4CF
        or >= 0xAC00 and <= 0xD7A3
        or >= 0xF900 and <= 0xFAFF
        or >= 0xFE10 and <= 0xFE19
        or >= 0xFE30 and <= 0xFE6F
        or >= 0xFF00 and <= 0xFF60
        or >= 0xFFE0 and <= 0xFFE6
        or >= 0x1F300 and <= 0x1F64F
        or >= 0x1F900 and <= 0x1F9FF
        or >= 0x1FA70 and <= 0x1FAFF
        or >= 0x20000 and <= 0x3FFFD;

    /// <summary>Clips to a visible width, appending a single-cell ellipsis when clipped.</summary>
    public static string Truncate(string? text, int width)
    {
        if (string.IsNullOrEmpty(text) || width <= 0)
        {
            return string.Empty;
        }

        if (VisibleWidth(text) <= width)
        {
            return text;
        }

        var builder = new System.Text.StringBuilder(text.Length);
        var used = 0;
        var index = 0;
        var sawEscape = false;
        var budget = width - 1;
        while (index < text.Length)
        {
            if (text[index] == '\u001b')
            {
                var next = SkipAnsi(text, index);
                builder.Append(text, index, next - index);
                sawEscape = true;
                index = next;
                continue;
            }

            var start = index;
            var codePoint = ReadCodePoint(text, ref index);
            var runeWidth = RuneWidth(codePoint);
            if (used + runeWidth > budget)
            {
                index = start;
                break;
            }

            builder.Append(text, start, index - start);
            used += runeWidth;
        }

        builder.Append('…');
        if (sawEscape)
        {
            builder.Append("\u001b[0m");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Keeps the tail of a single line (the cursor end) and prefixes an ellipsis when clipped.
    /// Used by the composer so a long prompt stays anchored at the cursor.
    /// </summary>
    public static string Tail(string? text, int width)
    {
        if (string.IsNullOrEmpty(text) || width <= 0)
        {
            return string.Empty;
        }

        if (VisibleWidth(text) <= width)
        {
            return text;
        }

        var budget = width - 1;
        var tokens = new List<(int Start, int Length, int RuneWidth)>();
        var index = 0;
        while (index < text.Length)
        {
            if (text[index] == '\u001b')
            {
                var next = SkipAnsi(text, index);
                tokens.Add((index, next - index, 0));
                index = next;
                continue;
            }

            var start = index;
            int codePoint;
            if (char.IsSurrogatePair(text, index))
            {
                codePoint = char.ConvertToUtf32(text, index);
                index += 2;
            }
            else if (!char.IsSurrogate(text[index]))
            {
                codePoint = text[index];
                index += 1;
            }
            else
            {
                codePoint = 0xFFFD;
                index += 1;
            }

            tokens.Add((start, index - start, RuneWidth(codePoint)));
        }

        var used = 0;
        var cutTokenIndex = tokens.Count;
        for (var i = tokens.Count - 1; i >= 0; i--)
        {
            var w = tokens[i].RuneWidth;
            if (used + w > budget)
            {
                break;
            }

            used += w;
            cutTokenIndex = i;
        }

        if (cutTokenIndex >= tokens.Count)
        {
            return "…";
        }

        var startChar = tokens[cutTokenIndex].Start;
        return "…" + text[startChar..];
    }

    /// <summary>
    /// Shortens to a visible width keeping both ends (providers usually share a
    /// prefix, versions share a suffix). Pure so dropdown columns stay readable.
    /// </summary>
    public static string MiddleTruncate(string? text, int width)
    {
        if (string.IsNullOrEmpty(text) || width <= 1 || VisibleWidth(text) <= width)
        {
            return text ?? string.Empty;
        }

        var tail = Math.Max(1, (width - 1) / 2);
        var head = width - 1 - tail;
        return TakeWidth(text, head) + "…" + TakeTailWidth(text, tail);
    }

    private static string TakeTailWidth(string text, int width)
    {
        var used = 0;
        var index = text.Length;
        while (index > 0)
        {
            var start = index;
            var codePoint = ReadCodePointBackward(text, ref start);
            var runeWidth = RuneWidth(codePoint);
            if (used + runeWidth > width)
            {
                break;
            }

            used += runeWidth;
            index = start;
        }

        return text[index..];
    }

    private static int ReadCodePointBackward(string text, ref int index)
    {
        index -= char.IsLowSurrogate(text[index - 1]) && index >= 2 && char.IsHighSurrogate(text[index - 2]) ? 2 : 1;
        return char.ConvertToUtf32(text, index);
    }

    /// <summary>Truncates then right-pads so the result occupies exactly <paramref name="width"/> cells.</summary>
    public static string Pad(string? text, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        var clipped = Truncate(text, width);
        var pad = width - VisibleWidth(clipped);
        return pad > 0 ? clipped + new string(' ', pad) : clipped;
    }

    /// <summary>Wraps plain (unstyled) text on spaces, hard-splitting words wider than the line.</summary>
    public static IReadOnlyList<string> Wrap(string? text, int width)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text) || width <= 0)
        {
            return lines;
        }

        foreach (var paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (paragraph.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }

            var current = string.Empty;
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (VisibleWidth(candidate) <= width)
                {
                    current = candidate;
                    continue;
                }

                if (current.Length > 0)
                {
                    lines.Add(current);
                    current = string.Empty;
                }

                var chunks = HardWrapLine(word, width);
                for (var i = 0; i < chunks.Count - 1; i++)
                {
                    lines.Add(chunks[i]);
                }
                current = chunks[^1];
            }

            if (current.Length > 0)
            {
                lines.Add(current);
            }
        }

        return lines;
    }

    /// <summary>
    /// Splits one line into consecutive chunks of at most <paramref name="width"/> visible
    /// cells, keeping every space and never dropping a character. <see cref="Wrap"/> reflows on
    /// word boundaries and collapses runs of spaces, which is right for prose and wrong for the
    /// column-aligned output a command prints (a session tree loses its shape), so anything
    /// that must reproduce a line exactly — the transcript, the document panel — wraps here.
    /// An empty line stays one empty row so blank rows survive the round trip.
    /// </summary>
    public static List<string> HardWrapLine(string text, int width)
    {
        var rows = new List<string>();
        if (text.Length == 0)
        {
            rows.Add(string.Empty);
            return rows;
        }

        var current = new StringBuilder();
        var used = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var runeWidth = RuneWidth(rune.Value);
            if (used + runeWidth > width && current.Length > 0)
            {
                rows.Add(current.ToString());
                current.Clear();
                used = 0;
            }

            current.Append(rune);
            used += runeWidth;
        }

        rows.Add(current.ToString());
        return rows;
    }

    /// <summary>Hard-wraps every line of a block, in order, as one flat list of rows.</summary>
    public static List<string> HardWrap(IReadOnlyList<string> lines, int width)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var rows = new List<string>();
        foreach (var line in lines)
        {
            rows.AddRange(HardWrapLine(line ?? string.Empty, width));
        }

        return rows;
    }

    private static string TakeWidth(string text, int width)
    {
        var used = 0;
        var index = 0;
        while (index < text.Length)
        {
            var start = index;
            var codePoint = ReadCodePoint(text, ref index);
            var runeWidth = RuneWidth(codePoint);
            if (used + runeWidth > width)
            {
                if (start == 0)
                {
                    return text[..index];
                }

                return text[..start];
            }

            used += runeWidth;
        }

        return text;
    }

    private static int ReadCodePoint(string text, ref int index)
    {
        var codePoint = char.ConvertToUtf32(text, index);
        index += char.IsSurrogatePair(text, index) ? 2 : 1;
        return codePoint;
    }

    /// <summary>Advances past one escape sequence; returns the index after it.</summary>
    private static int SkipAnsi(string text, int index)
    {
        if (index + 1 >= text.Length)
        {
            return text.Length;
        }

        var marker = text[index + 1];
        if (marker == '[')
        {
            var cursor = index + 2;
            while (cursor < text.Length
                && (char.IsDigit(text[cursor]) || text[cursor] is ';' or '?' or ':' or ' ' or '>' or '<' or '='))
            {
                cursor++;
            }

            return cursor < text.Length ? cursor + 1 : text.Length;
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
}
