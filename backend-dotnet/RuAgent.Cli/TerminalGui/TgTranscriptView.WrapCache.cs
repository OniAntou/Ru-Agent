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

    internal IReadOnlyList<TgTranscriptRow> WrappedLines
    {
        get { EnsureWrapped(); return _wrapped; }
    }

    private TgTranscriptRow? _wrapTail;
    private TgTranscriptRow? _tailPrefix;

    private int AppendWrapped(string text, TranscriptStyle style, IReadOnlyList<TgSpan>? spans, int width,
        bool continuesPrevious = false, bool replacingTail = false)
    {
        // Storage checkpoints are not paragraph breaks. Reflow only the preceding
        // visual row with the new chunk, leaving the settled viewport prefix cached.
        var prefix = replacingTail ? _tailPrefix
            : continuesPrevious && style == TranscriptStyle.Prose ? _wrapTail : null;
        _tailPrefix = prefix;
        if (prefix is not null)
        {
            if (!replacingTail && _wrapped.Count > 0) _wrapped.RemoveAt(_wrapped.Count - 1);
            if (prefix.Spans is not null || spans is not null)
                spans = SourceSpans(prefix.Text, prefix.Spans, style)
                    .Concat(SourceSpans(text, spans, style)).ToArray();
            text = prefix.Text + text;
        }

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

        _wrapTail = null;
        if (style == TranscriptStyle.Prose && cached.Rows.Count > 0)
        {
            // Word wrapping omits trailing spaces from paint. Keep their source
            // range in the carry so the next chunk cannot concatenate two words.
            var last = cached.Rows[^1].Text;
            var start = last.Length == 0 ? -1 : text.LastIndexOf(last, StringComparison.Ordinal);
            if (start >= 0)
                _wrapTail = new TgTranscriptRow(text[start..], style, SliceSourceSpans(spans, start));
        }
        _wrapped.AddRange(cached.Rows);
        return cached.Rows.Count;
    }

    private static IEnumerable<TgSpan> SourceSpans(string text, IReadOnlyList<TgSpan>? spans, TranscriptStyle style) =>
        spans is { Count: > 0 } ? spans : [new TgSpan(text, StyleAttribute(style))];

    private static IReadOnlyList<TgSpan>? SliceSourceSpans(IReadOnlyList<TgSpan>? spans, int start)
    {
        if (spans is null) return null;
        var result = new List<TgSpan>();
        foreach (var span in spans)
        {
            if (start < span.Text.Length) result.Add(new TgSpan(span.Text[Math.Max(0, start)..], span.Attr));
            start -= span.Text.Length;
        }
        return result;
    }
}
