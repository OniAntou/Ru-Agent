using System.Runtime.CompilerServices;

namespace RuAgent.Cli.TerminalGui;

public sealed partial class TgTranscriptView
{
    // Keys are the retained strings themselves. Evicting a transcript row also makes
    // its cached layout collectible; the cache cannot retain earlier conversations.
    private readonly ConditionalWeakTable<string, WrappedLine> _wrapCache = new();

    private sealed record WrappedLine(
        int Width,
        TranscriptStyle Style,
        IReadOnlyList<TgSpan>? Spans,
        IReadOnlyList<TgTranscriptRow> Rows);

    private int AppendWrapped(string text, TranscriptStyle style, IReadOnlyList<TgSpan>? spans, int width)
    {
        if (!_wrapCache.TryGetValue(text, out var cached)
            || cached.Width != width || cached.Style != style || !ReferenceEquals(cached.Spans, spans))
        {
            IReadOnlyList<TgTranscriptRow> rows;
            if (spans is { Count: > 0 })
            {
                var wrapped = proseLikeForSpans(text, style)
                    ? TgAnsiPaint.WordWrapSpans(text, spans, width)
                    : TgAnsiPaint.HardWrapSpans(text, spans, width);
                rows = wrapped.Select(row => new TgTranscriptRow(row.Row, style, row.Spans)).ToArray();
            }
            else
            {
                rows = WrapLine(text, style, null, width)
                    .Select(row => new TgTranscriptRow(row, style)).ToArray();
            }

            cached = new WrappedLine(width, style, spans, rows);
            _wrapCache.Remove(text);
            _wrapCache.Add(text, cached);
        }

        _wrapped.AddRange(cached.Rows);
        return cached.Rows.Count;
    }
}
