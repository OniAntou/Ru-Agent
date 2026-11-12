namespace RuAgent.Cli.Tui;

internal static partial class TuiMarkdownTable
{
    private static TranscriptLine ParseCell(string markdown)
    {
        var column = 0;
        var runs = TuiMarkdown.PresentationInline(DisplayText(markdown)).Select(run =>
        {
            var text = TuiText.ExpandTabs(TuiText.SanitizeUntrusted(run.Text), column, out var endColumn);
            column = endColumn;
            return run with { Text = text };
        }).ToArray();
        return new(runs);
    }

    private static string DisplayText(string text)
    {
        // A streamed table can temporarily end halfway through a surrogate pair.
        // Match the wrapper's replacement glyph before computing styled ranges.
        for (var index = 0; index < text.Length; index++)
        {
            if (!char.IsSurrogate(text[index])) continue;
            if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                index++;
            else
                return string.Concat(text.EnumerateRunes().Select(rune => rune.ToString()));
        }
        return text;
    }

    // Probe/count calls only parse cells. Decorate verified tables before layout so a
    // token split across visual rows keeps its tone without coloring divider syntax.
    private static TranscriptLine DecorateCell(TranscriptLine cell) => cell with { Runs = TuiDataTone.Decorate(cell.Runs) };

    private static TranscriptLine JoinCells(IEnumerable<TranscriptLine> cells, string separator = "")
    {
        var output = new TranscriptRunBuilder();
        var first = true;
        foreach (var cell in cells)
        {
            if (!first) output.Append(separator);
            output.Append(cell.Runs);
            first = false;
        }
        return output.Line();
    }

    private static TranscriptLine AlignCell(TranscriptLine cell, int width, CellAlign align)
    {
        var padding = Math.Max(0, width - TuiText.VisibleWidth(cell.Text));
        var left = align switch { CellAlign.Right => padding, CellAlign.Center => padding / 2, _ => 0 };
        return new TranscriptRunBuilder().Append(new string(' ', left)).Append(cell.Runs)
            .Append(new string(' ', padding - left)).Line();
    }

    /// <summary>Use the existing table layout, retaining the inline runs behind each wrapped range.</summary>
    private static IReadOnlyList<TranscriptLine> WrapCell(TranscriptLine cell, int width)
    {
        // TuiText.Wrap collapses spaces. Normalize the runs the same way before slicing
        // so repeated words, style boundaries and collapsed whitespace keep exact offsets.
        var normalized = new TranscriptRunBuilder();
        var previousSpace = false;
        foreach (var run in cell.Runs)
        {
            var start = 0;
            while (start < run.Text.Length)
            {
                var space = run.Text[start] == ' ';
                var end = start + 1;
                while (end < run.Text.Length && (run.Text[end] == ' ') == space) end++;
                if (!space || !previousSpace)
                    normalized.Append(space ? " " : run.Text[start..end], run.Token, run.Bold, run.Dim);
                previousSpace = space;
                start = end;
            }
        }
        var source = normalized.Line();
        var text = source.Text;
        var rows = new List<TranscriptLine>();
        var offset = 0;
        foreach (var row in TuiText.Wrap(text, width))
        {
            var start = text.IndexOf(row, offset, StringComparison.Ordinal);
            if (start < 0) throw new InvalidOperationException("Table wrapping lost its styled source range.");
            rows.Add(new(SliceRuns(source.Runs, start, row.Length)));
            offset = start + row.Length;
        }
        return rows;
    }

    private static IReadOnlyList<TranscriptRun> SliceRuns(IReadOnlyList<TranscriptRun> runs, int start, int length)
    {
        var output = new List<TranscriptRun>();
        var end = start + length;
        var offset = 0;
        foreach (var run in runs)
        {
            var low = Math.Max(start, offset);
            var high = Math.Min(end, offset + run.Text.Length);
            if (high > low) output.Add(run with { Text = run.Text[(low - offset)..(high - offset)] });
            offset += run.Text.Length;
            if (offset >= end) break;
        }
        return output;
    }
}
