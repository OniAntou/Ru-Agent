namespace RuAgent.Cli.Tui;

/// <summary>Renders a verified header/separator/body block from retained raw rows.</summary>
internal static class TuiMarkdownTable
{
    internal static bool TryRender(IReadOnlyList<string> lines, int start, int width,
        out int consumed, out IReadOnlyList<string> rendered)
    {
        consumed = 0;
        rendered = [];
        if (start + 1 >= lines.Count || !lines[start].Contains('|')) return false;
        var header = Cells(lines[start]);
        var divider = Cells(lines[start + 1]);
        if (header.Length is < 2 or > 12 || divider.Length != header.Length
            || divider.Any(cell => cell.Trim(':').Length < 3 || cell.Trim(':').Any(c => c != '-'))) return false;
        var rows = new List<string[]> { header };
        consumed = 2;
        while (start + consumed < lines.Count && rows.Count < 100)
        {
            var raw = lines[start + consumed];
            if (!raw.Contains('|')) break;
            var cells = Cells(raw);
            if (cells.Length != header.Length) break;
            rows.Add(cells);
            consumed++;
        }
        var available = width - (header.Length - 1) * 3;
        var output = new List<string>();
        if (width < 40 || available < header.Length * 4)
        {
            foreach (var row in rows.Skip(1))
            {
                for (int c = 0; c < header.Length; c++)
                    output.AddRange(TuiText.Wrap(header[c] + ": " + row[c], Math.Max(1, width)));
                output.Add(string.Empty);
            }
        }
        else
        {
            var widths = Enumerable.Range(0, header.Length)
                .Select(c => Math.Clamp(rows.Max(row => TuiText.VisibleWidth(row[c])), 4, 60)).ToArray();
            while (widths.Sum() > available)
            {
                int largest = Array.IndexOf(widths, widths.Max());
                widths[largest]--;
            }
            foreach (var row in rows)
            {
                var wrapped = row.Select((cell, c) => TuiText.Wrap(cell, widths[c])).ToArray();
                for (int r = 0; r < wrapped.Max(cell => cell.Count); r++)
                {
                    var parts = new List<string>();
                    for (int c = 0; c < widths.Length; c++)
                    {
                        var text = r < wrapped[c].Count ? wrapped[c][r] : string.Empty;
                        int padding = Math.Max(0, widths[c] - TuiText.VisibleWidth(text));
                        parts.Add(divider[c].EndsWith(':') ? new string(' ', padding) + text : text + new string(' ', padding));
                    }
                    output.Add(string.Join(" │ ", parts));
                }
                if (ReferenceEquals(row, header)) output.Add(string.Join("─┼─", widths.Select(w => new string('─', w))));
            }
        }
        rendered = output;
        return true;
    }

    private static string[] Cells(string line)
    {
        var cells = new List<string>();
        var buffer = new System.Text.StringBuilder();
        bool code = false;
        var trimmed = line.Trim().Trim('|');
        for (int i = 0; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if (c == '\\' && i + 1 < trimmed.Length && trimmed[i + 1] == '|') { buffer.Append('|'); i++; }
            else if (c == '`') { code = !code; buffer.Append(c); }
            else if (c == '|' && !code) { cells.Add(TuiText.Strip(string.Concat(TuiMarkdown.PresentationInline(buffer.ToString().Trim()).Select(run => run.Text)))); buffer.Clear(); }
            else buffer.Append(c);
        }
        cells.Add(TuiText.Strip(string.Concat(TuiMarkdown.PresentationInline(buffer.ToString().Trim()).Select(run => run.Text))));
        return cells.ToArray();
    }
}
