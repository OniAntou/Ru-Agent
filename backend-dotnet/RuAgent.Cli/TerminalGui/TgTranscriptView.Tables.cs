using RuAgent.Cli.Tui;

namespace RuAgent.Cli.TerminalGui;

public sealed partial class TgTranscriptView
{
    // Valid only while the wrapped history is valid. Full rebuilds rediscover the
    // last table; append-only rows preserve its source indices until retention.
    private int _lastTableStart = -1;
    private int _lastTableEnd = -1;
    private int _lastTableColumns;

    private bool RequiresTableRebuild(bool replacingTail)
    {
        if (_wrapWidth < 0) return true;
        var tail = _lines.Count - 1;
        // Editing an already rendered table must also handle removal of pipes or
        // changes of role; otherwise its old formatted body would remain visible.
        if (replacingTail && _lastTableEnd == tail) return true;
        if (tail < 1 || _lines[tail].Style != TranscriptStyle.Prose) return false;
        var text = _lines[tail].Text;
        if (_lastTableEnd == tail - 1 && _lastTableStart >= 0
            && _lastTableEnd - _lastTableStart < TuiMarkdownTable.MaximumRows
            && text.Contains('|') && TuiMarkdownTable.ColumnCount(text) == _lastTableColumns) return true;
        return _lines[tail - 1].Style == TranscriptStyle.Prose
            && TuiMarkdownTable.IsHeaderSeparator(_lines[tail - 1].Text, text);
    }
}
