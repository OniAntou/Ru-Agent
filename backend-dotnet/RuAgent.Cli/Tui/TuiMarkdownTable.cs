namespace RuAgent.Cli.Tui;

/// <summary>Renders a verified header/separator/body block from retained raw rows.</summary>
internal static partial class TuiMarkdownTable
{
    internal const int MaximumRows = 100;

    internal static bool IsHeaderSeparator(string header, string divider) =>
        TryReadHeader(header, divider, out _, out _);

    internal static int ColumnCount(string row) => Cells(row).Length;

    private static bool TryReadHeader(string headerText, string dividerText, out TranscriptLine[] header, out string[] divider)
    {
        header = []; divider = [];
        if (!headerText.Contains('|')) return false;
        // A rendered dash must originate in a literal dash or an HTML entity.
        // Conservatively validate entity-containing text with the existing cell renderer.
        if (!MayBeDivider(dividerText)) return false;
        divider = Cells(dividerText);
        if (divider.Length is < 2 or > 12
            || divider.Any(cell => cell.Trim(':').Length < 3 || cell.Trim(':').Any(c => c != '-'))) return false;
        header = StyledCells(headerText);
        return header.Length == divider.Length;
    }

    internal static bool TryRender(IReadOnlyList<string> lines, int start, int width,
        out int consumed, out IReadOnlyList<string> rendered)
    {
        var success = TryRenderPresentation(lines, start, width, out consumed, out var rows);
        rendered = rows.Select(row => row.Text).ToArray();
        return success;
    }

    internal static bool TryRenderPresentation(IReadOnlyList<string> lines, int start, int width,
        out int consumed, out IReadOnlyList<TranscriptLine> rendered)
    {
        consumed = 0;
        rendered = [];
        // Keep rejected history rows outside the formatter's captured render state.
        if (start + 1 >= lines.Count || !lines[start].Contains('|')
            || !MayBeDivider(lines[start + 1])) return false;
        return RenderTable(lines, start, width, out consumed, out rendered);
    }

    private static bool MayBeDivider(string text) => text.AsSpan().IndexOfAny('-', '&') >= 0;

    private static bool RenderTable(IReadOnlyList<string> lines, int start, int width,
        out int consumed, out IReadOnlyList<TranscriptLine> rendered)
    {
        consumed = 0;
        rendered = [];
        if (!TryReadHeader(lines[start], lines[start + 1], out var header, out var divider)) return false;
        var styledHeader = header.Select(DecorateCell).ToArray();
        var rows = new List<TranscriptLine[]> { styledHeader };
        consumed = 2;
        while (start + consumed < lines.Count && rows.Count < MaximumRows)
        {
            var raw = lines[start + consumed];
            if (!raw.Contains('|')) break;
            var cells = StyledCells(raw);
            if (cells.Length != header.Length) break;
            rows.Add(cells.Select(DecorateCell).ToArray());
            consumed++;
        }
        var available = width - (header.Length - 1) * 3;
        var output = new List<TranscriptLine>();
        if (width < 40 || available < header.Length * 4)
        {
            foreach (var row in rows.Skip(1))
            {
                for (int c = 0; c < header.Length; c++)
                    output.AddRange(WrapCell(JoinCells([styledHeader[c], TranscriptLine.Plain(": "), row[c]]), Math.Max(1, width)));
                output.Add(TranscriptLine.Plain(string.Empty));
            }
        }
        else
        {
            var widths = Enumerable.Range(0, header.Length)
                .Select(c => Math.Clamp(rows.Max(row => TuiText.VisibleWidth(row[c].Text)), 4, 60)).ToArray();
            while (widths.Sum() > available)
            {
                int largest = Array.IndexOf(widths, widths.Max());
                widths[largest]--;
            }
            var aligns = Enumerable.Range(0, header.Length)
                .Select(c => AlignmentFor(divider[c], rows.Skip(1).Select(row => row[c].Text)))
                .ToArray();
            foreach (var row in rows)
            {
                var wrapped = row.Select((cell, c) => WrapCell(cell, widths[c])).ToArray();
                for (int r = 0; r < wrapped.Max(cell => cell.Count); r++)
                {
                    var parts = new List<TranscriptLine>();
                    for (int c = 0; c < widths.Length; c++)
                    {
                        var cell = r < wrapped[c].Count ? wrapped[c][r] : TranscriptLine.Plain(string.Empty);
                        parts.Add(AlignCell(cell, widths[c], aligns[c]));
                    }
                    output.Add(JoinCells(parts, " │ "));
                }
                if (ReferenceEquals(row, styledHeader)) output.Add(TranscriptLine.Plain(string.Join("─┼─", widths.Select(w => new string('─', w)))));
            }
        }
        rendered = output;
        return true;
    }

    private enum CellAlign
    {
        Left,
        Right,
        Center,
    }

    /// <summary>
    /// Column alignment: an explicit Markdown marker wins; otherwise a column whose body is
    /// all numbers is right-aligned. Models routinely emit price and count columns with a
    /// plain <c>---</c> separator, and left-aligned numbers are the first thing a reader
    /// notices in a table.
    /// </summary>
    private static CellAlign AlignmentFor(string divider, IEnumerable<string> body)
    {
        var spec = divider.Trim();
        if (spec.Length > 1 && spec[0] == ':' && spec[^1] == ':')
        {
            return CellAlign.Center;
        }

        var values = body.Select(value => value.Trim()).Where(value => value.Length > 0).ToList();
        if (values.Count > 0 && values.All(IsNumeric))
        {
            return CellAlign.Right;
        }

        return spec.EndsWith(':') ? CellAlign.Right : CellAlign.Left;
    }

    private static bool IsNumeric(string text)
    {
        var digits = 0;
        foreach (var c in text)
        {
            if (char.IsAsciiDigit(c))
            {
                digits++;
                continue;
            }

            if (c is '.' or ',' or '-' or '+' or '%' or ' ' or '\u2212' or '\u00A0')
            {
                continue;
            }

            return false;
        }

        return digits > 0;
    }

    private static string[] Cells(string line) => StyledCells(line).Select(cell => cell.Text).ToArray();

    private static TranscriptLine[] StyledCells(string line)
    {
        var cells = new List<TranscriptLine>();
        var buffer = new System.Text.StringBuilder();
        bool code = false;
        var trimmed = line.Trim().Trim('|');
        for (int i = 0; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if (c == '\\' && i + 1 < trimmed.Length && trimmed[i + 1] == '|') { buffer.Append('|'); i++; }
            else if (c == '`') { code = !code; buffer.Append(c); }
            else if (c == '|' && !code) { cells.Add(ParseCell(buffer.ToString().Trim())); buffer.Clear(); }
            else buffer.Append(c);
        }
        cells.Add(ParseCell(buffer.ToString().Trim()));
        return cells.ToArray();
    }
}
