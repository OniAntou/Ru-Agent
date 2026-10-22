using System.Globalization;
using System.Text;

namespace RuAgent.Cli.Tui;

/// <summary>
/// Terminal text measurement. Every structural line the TUI paints is measured and clipped
/// with these helpers so borders stay aligned for combining marks, CJK, emoji and ANSI
/// styling alike.
///
/// This is the single text engine for both output sinks: the Terminal.Gui transcript and
/// the append-only text writer. Neither may re-implement width, wrapping or rune decoding,
/// because two implementations is exactly how a row ends up one cell wider than the
/// terminal and every absolute cursor move after it lands on the wrong line.
/// </summary>
public static class TuiText
{
    /// <summary>Tab stop used by the terminal and by every measurement here.</summary>
    public const int TabWidth = 8;

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

            if (text[index] == '\t')
            {
                width = NextTabStop(width);
                index++;
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

    private static int NextTabStop(int column) => (column / TabWidth + 1) * TabWidth;

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

    /// <summary>
    /// East-Asian Wide/Fullwidth plus the emoji blocks terminals draw as two cells. The
    /// emoji list is explicit because a code point that measures 1 but paints 2 shifts
    /// every right-aligned cell after it on the row.
    /// </summary>
    private static bool IsWide(int codePoint) => codePoint
        is >= 0x1100 and <= 0x115F
        or >= 0x231A and <= 0x231B
        or 0x2329 or 0x232A
        or >= 0x23E9 and <= 0x23EC
        or 0x23F0 or 0x23F3
        or >= 0x25FD and <= 0x25FE
        or >= 0x2614 and <= 0x2615
        or >= 0x2648 and <= 0x2653
        or 0x267F or 0x2693 or 0x26A1
        or >= 0x26AA and <= 0x26AB
        or >= 0x26BD and <= 0x26BE
        or >= 0x26C4 and <= 0x26C5
        or 0x26CE or 0x26D4 or 0x26EA
        or >= 0x26F2 and <= 0x26F3
        or 0x26F5 or 0x26FA or 0x26FD
        or 0x2705 or >= 0x270A and <= 0x270B
        or 0x2728 or 0x274C or 0x274E
        or >= 0x2753 and <= 0x2755
        or 0x2757
        or >= 0x2795 and <= 0x2797
        or 0x27B0 or 0x27BF
        or >= 0x2B1B and <= 0x2B1C
        or 0x2B50 or 0x2B55
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
        or >= 0x1F004 and <= 0x1F004
        or 0x1F0CF
        or 0x1F18E
        or >= 0x1F191 and <= 0x1F19A
        or >= 0x1F200 and <= 0x1F320
        or >= 0x1F32D and <= 0x1F335
        or >= 0x1F337 and <= 0x1F37C
        or >= 0x1F37E and <= 0x1F393
        or >= 0x1F3A0 and <= 0x1F3CA
        or >= 0x1F3CF and <= 0x1F3D3
        or >= 0x1F3E0 and <= 0x1F3F0
        or 0x1F3F4
        or >= 0x1F3F8 and <= 0x1F43E
        or 0x1F440
        or >= 0x1F442 and <= 0x1F4FC
        or >= 0x1F4FF and <= 0x1F53D
        or >= 0x1F54B and <= 0x1F54E
        or >= 0x1F550 and <= 0x1F567
        or 0x1F57A
        or >= 0x1F595 and <= 0x1F596
        or 0x1F5A4
        or >= 0x1F5FB and <= 0x1F64F
        or >= 0x1F680 and <= 0x1F6C5
        or 0x1F6CC
        or >= 0x1F6D0 and <= 0x1F6D2
        or >= 0x1F6D5 and <= 0x1F6D7
        or >= 0x1F6EB and <= 0x1F6EC
        or >= 0x1F6F4 and <= 0x1F6FC
        or >= 0x1F7E0 and <= 0x1F7EB
        or 0x1F7F0
        or >= 0x1F90C and <= 0x1F93A
        or >= 0x1F93C and <= 0x1F945
        or >= 0x1F947 and <= 0x1F9FF
        or >= 0x1FA70 and <= 0x1FAFF
        or >= 0x20000 and <= 0x3FFFD;

    /// <summary>
    /// Clips to a visible width, appending a single-cell ellipsis when clipped. ANSI
    /// sequences are copied through and only their visible cells count.
    /// </summary>
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

            if (text[index] == '\t')
            {
                var tabStop = NextTabStop(used);
                if (tabStop > budget)
                {
                    break;
                }

                builder.Append('\t');
                used = tabStop;
                index++;
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

            AppendDecoded(builder, text, start, index, codePoint);
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
    /// Copies at most <paramref name="maxChars"/> UTF-16 units without splitting a
    /// surrogate pair. Callers append their own ellipsis; this only guarantees the slice
    /// is well-formed, because every measurement helper here rejects a lone surrogate.
    /// </summary>
    public static string SafeCut(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (text.Length <= maxChars)
        {
            return text;
        }

        var end = Math.Max(0, maxChars);
        if (end > 0 && char.IsHighSurrogate(text[end - 1]))
        {
            end--;
        }

        return text[..end];
    }

    /// <summary>Expands tabs to spaces at the shared tab stops (exact-output paths only).</summary>
    public static string ExpandTabs(string text)
    {
        if (text.IndexOf('\t') < 0)
        {
            return text;
        }

        var builder = new System.Text.StringBuilder(text.Length + 8);
        var column = 0;
        var index = 0;
        while (index < text.Length)
        {
            if (text[index] == '\t')
            {
                var next = NextTabStop(column);
                builder.Append(' ', next - column);
                column = next;
                index++;
                continue;
            }

            if (text[index] == '\u001b')
            {
                var next = SkipAnsi(text, index);
                builder.Append(text, index, next - index);
                index = next;
                continue;
            }

            var start = index;
            var codePoint = ReadCodePoint(text, ref index);
            column += RuneWidth(codePoint);
            AppendDecoded(builder, text, start, index, codePoint);
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

        text = ExpandTabs(text);
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
            var codePoint = ReadCodePoint(text, ref index);
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
        text ??= string.Empty;
        if (width <= 1 || VisibleWidth(text) <= width)
        {
            return text;
        }

        // Width 2 has no room for head + ellipsis + tail: keep one cell plus the ellipsis
        // rather than emitting a 3-cell string into a 2-cell column.
        if (width == 2)
        {
            return TakeWidth(text, 1) + "…";
        }

        var tail = Math.Max(1, (width - 1) / 2);
        var head = Math.Max(1, width - 1 - tail);
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
        var start = index - 1;
        if (start > 0 && char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start - 1]))
        {
            start--;
        }

        var status = System.Text.Rune.DecodeFromUtf16(text.AsSpan(start, index - start), out var rune, out _);
        index = start;
        return status == System.Buffers.OperationStatus.Done ? rune.Value : 0xFFFD;
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
    /// that must reproduce a line exactly -- the transcript, the document panel -- wraps here.
    /// An empty line stays one empty row so blank rows survive the round trip.
    ///
    /// Escape sequences are copied whole: splitting an SGR sequence leaks its parameter bytes
    /// onto the screen as text and leaves the following rows painted with the wrong style.
    /// </summary>
    public static List<string> HardWrapLine(string text, int width)
    {
        var rows = new List<string>();
        if (text.Length == 0)
        {
            rows.Add(string.Empty);
            return rows;
        }

        width = Math.Max(1, width);
        text = ExpandTabs(text);

        var current = new StringBuilder();
        var used = 0;
        var index = 0;
        while (index < text.Length)
        {
            if (text[index] == '\u001b')
            {
                var next = SkipAnsi(text, index);
                current.Append(text, index, next - index);
                index = next;
                continue;
            }

            var start = index;
            var codePoint = ReadCodePoint(text, ref index);
            var runeWidth = RuneWidth(codePoint);
            if (used + runeWidth > width && current.Length > 0)
            {
                rows.Add(current.ToString());
                current.Clear();
                used = 0;
            }

            // A rune wider than the row still has to be emitted somewhere; it takes its own
            // row so the overflow is one cell, not a row that keeps accumulating.
            AppendDecoded(current, text, start, index, codePoint);
            used += runeWidth;
            if (used >= width && used > width)
            {
                rows.Add(current.ToString());
                current.Clear();
                used = 0;
            }
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

    /// <summary>
    /// Copies the source slice for one decoded unit, substituting U+FFFD for an unpaired
    /// surrogate so a clipped row never carries invalid UTF-16 into the terminal.
    /// </summary>
    private static void AppendDecoded(StringBuilder builder, string text, int start, int index, int codePoint)
    {
        if (codePoint == 0xFFFD && index - start == 1 && char.IsSurrogate(text[start]))
        {
            builder.Append('\uFFFD');
            return;
        }

        builder.Append(text, start, index - start);
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
                return text[..start];
            }

            used += runeWidth;
        }

        return text;
    }

    /// <summary>
    /// Decodes the code point at <paramref name="index"/> and how many UTF-16 units it
    /// occupies. Invalid UTF-16 yields U+FFFD and consumes one unit; never throws.
    /// </summary>
    public static (int CodePoint, int Consumed) ReadRune(string text, int index)
    {
        if (index < 0 || index >= text.Length)
        {
            return (0xFFFD, 0);
        }

        var status = System.Text.Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out var consumed);
        return status == System.Buffers.OperationStatus.Done && consumed > 0
            ? (rune.Value, consumed)
            : (0xFFFD, 1);
    }

    /// <summary>Index just past the escape sequence that starts at <paramref name="index"/>.</summary>
    public static int SkipEscapeSequence(string text, int index) => SkipAnsi(text, index);

    private static int ReadCodePoint(string text, ref int index)
    {
        // DecodeFromUtf16 reports an unpaired surrogate as InvalidData/NeedMoreData instead
        // of throwing. char.ConvertToUtf32 threw, which turned one malformed code unit into
        // a dead turn: every measurement helper here is on the paint path.
        var status = System.Text.Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out var consumed);
        if (status != System.Buffers.OperationStatus.Done || consumed <= 0)
        {
            index += 1;
            return 0xFFFD;
        }

        index += consumed;
        return rune.Value;
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
