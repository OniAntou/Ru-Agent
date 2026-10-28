namespace RuAgent.Cli.TerminalGui;

public sealed partial class TgTranscriptView
{
    private int _wrapGroupStart = -1;
    private int _wrapGroupTop;
    private bool _wrapWholeGroup;

    private int AppendViewRow(int index, int width, bool replacingTail = false)
    {
        var row = _lines[index];
        if (row.Style != TranscriptStyle.Prose)
        {
            ResetWrapGroup();
            return AppendWrapped(row.Text, row.Style, row.Spans, width);
        }
        var continues = row.ContinuesPrevious && row.Style == TranscriptStyle.Prose
            && _wrapGroupStart >= 0 && _wrapGroupStart < index;
        if (!continues)
        {
            _wrapGroupStart = index;
            _wrapGroupTop = _wrapped.Count;
            _wrapWholeGroup = !proseLikeForSpans(row.Text, row.Style);
        }

        // Indentation, quote/list affixes and a double-space layout are properties
        // of the entire logical row. They cannot be inferred from its last painted
        // line. Preserve the original wrapper for those less common cases instead
        // of treating a synthesized gutter/indent as source text.
        var tail = continues && !replacingTail ? _wrapTail : _tailPrefix;
        if (continues && !proseLikeForSpans((tail?.Text ?? "") + row.Text, row.Style))
            _wrapWholeGroup = true;

        if (continues && _wrapWholeGroup)
        {
            var source = _lines.Skip(_wrapGroupStart).Take(index - _wrapGroupStart + 1).ToArray();
            var text = string.Concat(source.Select(part => part.Text));
            var spans = source.Any(part => part.Spans is not null)
                ? source.SelectMany(part => SourceSpans(part.Text, part.Spans, part.Style)).ToArray()
                : null;
            _wrapped.RemoveRange(_wrapGroupTop, _wrapped.Count - _wrapGroupTop);
            _tailPrefix = null;
            return AppendWrapped(text, row.Style, spans, width);
        }

        return AppendWrapped(row.Text, row.Style, row.Spans, width, continues, replacingTail);
    }

    private void ResetWrapGroup()
    {
        _wrapGroupStart = -1;
        _wrapTail = null;
        _tailPrefix = null;
    }
}
