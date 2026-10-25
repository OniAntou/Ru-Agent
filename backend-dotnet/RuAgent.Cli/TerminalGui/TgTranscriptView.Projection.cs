namespace RuAgent.Cli.TerminalGui;

public sealed partial class TgTranscriptView
{
    /// <summary>Replaces the complete provisional paragraph, including all storage segments.</summary>
    internal int ReplaceProjectedRows(int previousCount, IReadOnlyList<TgTranscriptRow> rows, int unchangedPrefix = 0)
    {
        if (_lines.Count > 0 && previousCount - unchangedPrefix == 1 && rows.Count - unchangedPrefix == 1)
        {
            var row = rows[^1];
            ReplaceTail(row.Text, row.Style, row.Spans);
            _lines[^1] = _lines[^1] with { ContinuesPrevious = row.ContinuesPrevious };
            return Math.Min(rows.Count, _lines.Count - (_historyTruncated ? 1 : 0));
        }

        if (previousCount > 0)
        {
            // A long provisional paragraph may have evicted its own oldest segments.
            // Keep the retention marker and replace only the surviving provisional tail.
            var count = Math.Min(previousCount, _lines.Count - (_historyTruncated ? 1 : 0)) - unchangedPrefix;
            for (var index = 0; index < count; index++) RemoveLine(_lines.Count - 1);
            RemoveBlocksAfter(_lines.Count);
            if (count > 0)
            {
                _wrapWidth = -1;
                _tailDirty = false;
            }
        }

        foreach (var row in rows.Skip(unchangedPrefix)) Append(row.Text, row.Style, row.Spans, row.ContinuesPrevious);
        if (_lines.Count == 0) ContentCleared?.Invoke();
        SetNeedsDraw();
        return Math.Min(rows.Count, _lines.Count - (_historyTruncated ? 1 : 0));
    }
}
