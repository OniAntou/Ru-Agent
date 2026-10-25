namespace RuAgent.Cli.TerminalGui;

public sealed partial class TgTranscriptWriter
{
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Tui.TranscriptLine, NativeParagraph> _nativeParagraphs = new();
    private sealed record NativeParagraph(TgTranscriptRow[] Rows);

    private IEnumerable<TgTranscriptRow> ParagraphRows(Tui.TranscriptLine row) =>
        _nativeParagraphs.GetValue(row, line => new NativeParagraph(SegmentParagraph(line).ToArray())).Rows;

    /// <summary>
    /// Split after Markdown styling and tab expansion, before the view's per-row cap.
    /// Total history retention still applies; no paragraph needs an unbounded view row.
    /// </summary>
    private static IEnumerable<TgTranscriptRow> SegmentParagraph(Tui.TranscriptLine row)
    {
        var native = NativeRow(row);
        if (native.Style != TranscriptStyle.Prose || native.Text.Length <= TgTranscriptView.MaxRetainedLineCharacters)
        {
            yield return native;
            yield break;
        }

        var spanIndex = 0;
        var spanStart = 0;
        for (var start = 0; start < native.Text.Length;)
        {
            var end = Math.Min(native.Text.Length, start + TgTranscriptView.MaxRetainedLineCharacters);
            if (end < native.Text.Length && char.IsHighSurrogate(native.Text[end - 1])) end--;
            List<TgSpan>? spans = native.Spans is null ? null : [];
            while (native.Spans is { } source && spanIndex < source.Count && spanStart < end)
            {
                var span = source[spanIndex];
                var spanEnd = spanStart + span.Text.Length;
                var from = Math.Max(start, spanStart);
                var to = Math.Min(end, spanEnd);
                if (to > from) spans!.Add(new TgSpan(span.Text[(from - spanStart)..(to - spanStart)], span.Attr));
                if (spanEnd > end) break;
                spanStart = spanEnd;
                spanIndex++;
            }
            yield return new TgTranscriptRow(native.Text[start..end], native.Style, spans)
                { ContinuesPrevious = row.ContinuesPrevious || start > 0 };
            start = end;
        }
    }
}
