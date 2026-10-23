using System.Text;

namespace RuAgent.Cli.Tui;

/// <summary>
/// Markdown for the transcript. A coding model's answer is mostly fenced code, inline
/// code, bold and lists; printed raw those read like a log dump, so the surface renders
/// them the way the WinUI chat does.
///
/// The block forms (fence, heading, bullet, ordered item, quote, rule) are decided per
/// line. The inline forms are decided per fragment, which means a marker can straddle
/// two streamed deltas: <see cref="Holdback"/> reports the trailing run that could still
/// grow into a marker so the caller keeps it back, and
/// <see cref="RenderInline"/> treats a leading <c>*</c> or <c>_</c> as an opener only
/// when a word character precedes it, so the closer of a bold run that began in an
/// earlier delta is never mistaken for a new opener.
///
/// Tables and one level of nested list are decided per line too: a pipe-bracketed row
/// becomes a joined row, a two-space-indented list item gets the extra indent, and
/// anything nested deeper falls back to plain indented text rather than a wrong-depth
/// bullet. No block state beyond the fence is carried between lines.
/// </summary>
public sealed partial class TuiMarkdown
{
    private const string FenceChars = "```";
    private const string Indent = "    ";
    private const string Gutter = "│ ";

    private string _fence = string.Empty;
    private int _codeLine;

    /// <summary>True while the stream is inside a fenced code block.</summary>
    public bool InFence => _fence.Length > 0;

    /// <summary>
    /// Renders fenced code through <see cref="TuiCodeHighlight"/> (line-number gutter plus
    /// keyword/string/comment tint) instead of the plain gutter. Off by default, so the
    /// sequential fallback keeps its exact existing rows.
    /// </summary>
    public bool HighlightCode { get; set; }

    public IReadOnlyList<string> RenderLogicalLine(string line, int width) =>
        !InFence && IsTableCandidate(line) ? [line]
            : RenderLine(line, TryRule(line, out _) ? width : Math.Max(width, line.Length + 16));

    public string PreviewLine(string line, int width)
    {
        // Delimiters may still be arriving. Preview using a copy so a partial line
        // cannot change fence state or consume a code line number.
        var trimmed = line.TrimStart();
        if (!InFence && trimmed.Length > 0
            && (FenceChars.StartsWith(trimmed, StringComparison.Ordinal)
                || trimmed.StartsWith(FenceChars, StringComparison.Ordinal))) return string.Empty;
        var copy = new TuiMarkdown { HighlightCode = HighlightCode, _fence = _fence, _codeLine = _codeLine };
        return string.Join("\n", copy.RenderLogicalLine(line, width));
    }

    /// <summary>Forgets the fence state; used when a tool block interrupts the prose.</summary>
    public void Reset()
    {
        _fence = string.Empty;
        _codeLine = 0;
    }

    /// <summary>
    /// The opening fence becomes a dim language label (<c>── csharp ────</c>) when it names
    /// a language; a bare fence stays invisible so it contributes no row of its own.
    /// </summary>
    private static IReadOnlyList<string> FenceLabelRow(string line, int width)
    {
        var info = line.Trim().TrimStart('`').Trim();
        if (info.Length == 0 || width <= 4)
        {
            return [];
        }

        var language = ChatDisplayText.Sanitize(info, 32);
        var head = "  ── " + language + " ";
        var tail = Math.Max(0, width - TuiText.VisibleWidth(head));
        return
        [
            TuiPalette.Paint(head, TuiToken.Meta)
                + TuiPalette.Paint(new string('─', tail), TuiToken.Line),
        ];
    }

    /// <summary>
    /// Renders one complete line (no trailing newline) into zero or more display rows.
    /// A fence opener and closer produce no row of their own.
    /// </summary>
    public IReadOnlyList<string> RenderLine(string line, int width)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (InFence)
        {
            if (IsFenceLine(line))
            {
                _fence = string.Empty;
                return [];
            }

            if (HighlightCode)
            {
                _codeLine++;
                return TuiCodeHighlight.RenderLine(line, _codeLine, width, TuiPalette.Level);
            }

            return [CodeRow(line, width)];
        }

        if (IsFenceLine(line))
        {
            _fence = FenceChars;
            _codeLine = 0;
            return FenceLabelRow(line, width);
        }

        if (line.Length == 0)
        {
            return [string.Empty];
        }

        if (TryRule(line, out var rule))
        {
            return [TuiPalette.DimPaint(TuiText.Pad("  " + rule, width))];
        }

        if (TryQuote(line, out var quote))
        {
            return Wrap(TuiPalette.DimPaint(Gutter) + RenderInline(quote, width, false), width, TuiText.VisibleWidth(Gutter));
        }

        if (TryHeading(line, out var heading, out var level))
        {
            var body = RenderInline(heading, width, false);
            return Wrap(TuiPalette.Paint(level == 1 ? TuiPalette.BoldPaint(body) : body, TuiToken.Warn), width, 0);
        }

        if (TryTable(line, out var table))
        {
            return Wrap(RenderInline(table, width, false), width, 0);
        }

        if (TryNestedBullet(line, out var nested))
        {
            return Wrap("    " + TuiPalette.Paint("•", TuiToken.Brand2) + "  " + RenderInline(nested, width, false), width, 6);
        }

        if (IsDeeperBullet(line))
        {
            return Wrap(RenderInline(line.TrimEnd(), width, false), width, 0);
        }

        if (TryBullet(line, out var bullet))
        {
            return Wrap("  " + TuiPalette.Paint("•", TuiToken.Brand2) + "  " + RenderInline(bullet, width, false), width, 4);
        }

        if (TryOrdered(line, out var marker, out var ordered))
        {
            return Wrap("  " + TuiPalette.Paint(marker, TuiToken.Brand2) + "  " + RenderInline(ordered, width, false), width, 2 + marker.Length + 2);
        }

        return Wrap(RenderInline(line, width, false), width, 0);
    }

    /// <summary>
    /// The trailing run of marker characters a fragment ends on, which a following delta
    /// could still complete. Only the run at the very end counts: a marker character
    /// earlier in the buffer (the <c>_</c> in a tool-call wrapper, for instance) belongs
    /// to something else, and holding from there would swallow the text after it.
    /// </summary>
    public static int Holdback(string buffer)
    {
        if (string.IsNullOrEmpty(buffer))
        {
            return 0;
        }

        var start = buffer.Length;
        while (start > 0 && IsMarkerChar(buffer[start - 1]))
        {
            start--;
        }

        return buffer.Length - start;
    }

    /// <summary>
    /// Renders inline markdown. <paramref name="continuesWord"/> says the fragment starts
    /// in the middle of a word, which is what tells a bold closer from a bold opener.
    /// </summary>
    public static string RenderInline(string text, int width, bool continuesWord)
    {
        ArgumentNullException.ThrowIfNull(text);
        return string.Concat(PresentationInline(text, continuesWord).Select(run => run.Dim
            ? TuiPalette.DimPaint(run.Text)
            : TuiPalette.Paint(run.Bold ? TuiPalette.BoldPaint(run.Text) : run.Text, run.Token)));
    }

    private static int FindClose(string text, int from, char marker)
    {
        for (var i = from; i + 1 < text.Length; i++)
        {
            if (text[i] == marker && text[i + 1] == marker)
            {
                return i;
            }
        }

        return -1;
    }

    private static string? FindLinkLabel(string text, int start, out int after)
    {
        var close = text.IndexOf(']', start + 1);
        if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(')
        {
            after = start;
            return null;
        }

        var end = text.IndexOf(')', close + 2);
        if (end < 0)
        {
            after = start;
            return null;
        }

        after = end + 1;
        return text[(start + 1)..close];
    }

    private static bool IsMarkerChar(char c) =>
        c is '*' or '_' or '`' or '~' or '[' or ']' or '\\';

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or ')' or ']' or '"' or '\'';

    private static bool IsEscapable(char c) => !char.IsLetterOrDigit(c);

     /// <summary>
    /// A row the transcript's table renderer may group: outer pipes plus at least two
    /// separators. Inner-pipe prose stays on the normal inline path.
    /// </summary>
    private static bool IsTableCandidate(string line)
    {
        var trimmed = line.AsSpan().Trim();
        if (trimmed.Length < 3)
        {
            return false;
        }

        if (trimmed[0] != '|' && trimmed[^1] != '|')
        {
            return false;
        }

        var pipes = 0;
        foreach (var c in trimmed)
        {
            if (c == '|')
            {
                pipes++;
            }
        }

        return pipes >= 2;
    }

    private static bool IsFenceLine(string line)
    {
        var trimmed = line.AsSpan().TrimStart();
        return trimmed.StartsWith(FenceChars, StringComparison.Ordinal)
            || trimmed.StartsWith("~~~", StringComparison.Ordinal);
    }

    private static bool TryRule(string line, out string rule)
    {
        var trimmed = line.Trim();
        if (trimmed.Length >= 3
            && (trimmed.All(c => c == '-') || trimmed.All(c => c == '*') || trimmed.All(c => c == '_')))
        {
            rule = new string('─', 40);
            return true;
        }

        rule = string.Empty;
        return false;
    }

    private static bool TryQuote(string line, out string quoted)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("> ", StringComparison.Ordinal) || trimmed == ">")
        {
            quoted = trimmed.Length > 1 ? trimmed[2..] : string.Empty;
            return true;
        }

        quoted = string.Empty;
        return false;
    }

    private static bool TryHeading(string line, out string heading, out int level)
    {
        var trimmed = line.AsSpan().TrimStart();
        level = 0;
        while (level < trimmed.Length && trimmed[level] == '#')
        {
            level++;
        }

        if (level is 0 or > 6 || level >= trimmed.Length || trimmed[level] != ' ')
        {
            heading = string.Empty;
            level = 0;
            return false;
        }

        heading = trimmed[(level + 1)..].TrimEnd().ToString();
        return true;
    }

    private static bool TryBullet(string line, out string bullet)
    {
        var trimmed = line.AsSpan().TrimStart();
        if (trimmed.Length >= 2 && trimmed[0] is '-' or '*' or '+' && trimmed[1] == ' ')
        {
            bullet = trimmed[2..].TrimEnd().ToString();
            return true;
        }

        bullet = string.Empty;
        return false;
    }

    /// <summary>
    /// A pipe-bracketed table row: the leading and trailing pipes are fences, the cells
    /// between are trimmed and rejoined with a visible column separator. A second pipe is
    /// required, so a lone leading pipe stays prose.
    /// </summary>
    private static bool TryTable(string line, out string row)
    {
        var trimmed = line.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '|' || trimmed.IndexOf('|', 1) < 0)
        {
            row = string.Empty;
            return false;
        }

        var cells = trimmed.Split('|');
        var start = cells[0].Length == 0 ? 1 : 0;
        var end = cells[^1].Length == 0 ? cells.Length - 1 : cells.Length;
        var parts = new List<string>(Math.Max(0, end - start));
        for (var i = start; i < end; i++)
        {
            parts.Add(cells[i].Trim());
        }

        row = string.Join(" │ ", parts);
        return row.Length > 0;
    }

    /// <summary>An exactly two-space-indented list item, the one nesting level supported.</summary>
    private static bool TryNestedBullet(string line, out string bullet)
    {
        if (line.Length >= 4
            && line[0] == ' '
            && line[1] == ' '
            && (line[2] is '-' or '*' or '+')
            && line[3] == ' ')
        {
            bullet = line[4..].TrimEnd();
            return true;
        }

        bullet = string.Empty;
        return false;
    }

    /// <summary>
    /// A list item indented four spaces or more: past the supported nesting level, so it
    /// falls back to plain indented text instead of a bullet at the wrong depth.
    /// </summary>
    private static bool IsDeeperBullet(string line)
    {
        var spaces = 0;
        while (spaces < line.Length && line[spaces] == ' ')
        {
            spaces++;
        }

        return spaces >= 4
            && spaces + 1 < line.Length
            && (line[spaces] is '-' or '*' or '+')
            && line[spaces + 1] == ' ';
    }

    private static bool TryOrdered(string line, out string marker, out string body)
    {
        var trimmed = line.AsSpan().TrimStart();
        var digits = 0;
        while (digits < trimmed.Length && char.IsAsciiDigit(trimmed[digits]))
        {
            digits++;
        }

        if (digits is 0 or > 3
            || digits + 1 >= trimmed.Length
            || trimmed[digits] != '.'
            || trimmed[digits + 1] != ' ')
        {
            marker = string.Empty;
            body = string.Empty;
            return false;
        }

        marker = trimmed[..(digits + 1)].ToString();
        body = trimmed[(digits + 2)..].TrimEnd().ToString();
        return true;
    }

    private static string CodeRow(string line, int width)
    {
        var body = TuiText.Truncate(TuiText.Strip(line), Math.Max(1, width - TuiText.VisibleWidth(Gutter)));
        // Code is the content the operator came to read: paint it at ink weight and let the
        // gutter recede, instead of dimming both into one grey band.
        return TuiPalette.Paint(Gutter, TuiToken.Line) + TuiPalette.Paint(body, TuiToken.Ink);
    }

    /// <summary>
    /// Wraps styled text on visible width. The indent is re-emitted on the continuation
    /// rows so a wrapped bullet keeps its marker column.
    /// </summary>
    private static IReadOnlyList<string> Wrap(string styled, int width, int indent)
    {
        if (width <= 0)
        {
            return [styled];
        }

        var plain = TuiText.Strip(styled);
        if (TuiText.VisibleWidth(plain) <= width)
        {
            return [styled];
        }

        var rows = WrapStyled(styled, width, indent);
        return rows.Count == 0 ? [styled] : rows;
    }

    private static IReadOnlyList<string> WrapStyled(string styled, int width, int indent)
    {
        var rows = new List<string>();
        var current = new StringBuilder();
        var used = 0;
        var limit = Math.Max(1, width - indent);

        foreach (var run in SplitRuns(styled))
        {
            foreach (var word in run.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var text = word;
                var wordWidth = TuiText.VisibleWidth(text);
                if (current.Length > 0 && used + 1 + wordWidth > limit)
                {
                    rows.Add(current.ToString());
                    current.Clear();
                    used = 0;
                }

                if (current.Length > 0)
                {
                    current.Append(' ');
                    used += 1;
                }

                if (wordWidth > limit)
                {
                    text = TuiText.Truncate(text, limit);
                    wordWidth = limit;
                }

                current.Append(text);
                used += wordWidth;
            }
        }

        if (current.Length > 0)
        {
            rows.Add(current.ToString());
        }

        return rows;
    }

    private static IEnumerable<string> SplitRuns(string styled)
    {
        var buffer = new StringBuilder();
        foreach (var c in styled)
        {
            if (c == '\u001b')
            {
                if (buffer.Length > 0)
                {
                    yield return buffer.ToString();
                    buffer.Clear();
                }
            }

            buffer.Append(c);
        }

        if (buffer.Length > 0)
        {
            yield return buffer.ToString();
        }
    }
}
