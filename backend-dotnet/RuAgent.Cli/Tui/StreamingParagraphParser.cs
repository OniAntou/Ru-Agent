using System.Text;

namespace RuAgent.Cli.Tui;

/// <summary>Retains settled inline chunks; only the unresolved suffix returns to Markdig.</summary>
internal sealed class StreamingParagraphParser
{
    private const int CheckpointCharacters = 4096;
    private readonly StringBuilder _tail = new();
    private readonly List<TranscriptLine> _settled = [];
    private IReadOnlyList<TranscriptLine>? _preview;
    private int _settledColumn;
    internal long ParsedCharacters { get; private set; }
    internal int SettledChunkCount => _settled.Count;
    internal bool HasContent => _tail.Length > 0 || _settled.Count > 0;

    internal void Append(string text)
    {
        if (text.Length == 0) return;
        _tail.Append(text);
        _preview = null;
    }

    internal IReadOnlyList<TranscriptLine> Preview()
    {
        if (_preview is not null) return _preview;
        var raw = _tail.ToString();
        ParsedCharacters += raw.Length;
        var (runs, stable) = MarkdigInline.ParseTail(raw, _settled.Count > 0);
        if (stable >= CheckpointCharacters)
        {
            var prefix = raw[..stable];
            ParsedCharacters += prefix.Length;
            var prefixRuns = MarkdigInline.ParseFragment(prefix, _settled.Count > 0, true);
            _settled.Add(new(prefixRuns)
                { ContinuesPrevious = _settled.Count > 0, StartColumn = _settledColumn });
            foreach (var run in prefixRuns)
                TuiText.ExpandTabs(run.Text, _settledColumn, out _settledColumn);
            _tail.Remove(0, stable);
            var rest = raw[stable..];
            ParsedCharacters += rest.Length;
            runs = MarkdigInline.ParseFragment(rest, true, false);
        }
        return _preview = WithTail(runs);
    }

    // Ambiguous block prefixes (e.g. a lone pipe that may become a table) are
    // displayed without committing their source to this paragraph's parse state.
    internal IReadOnlyList<TranscriptLine> PreviewExtra(string extra)
    {
        var raw = _tail.ToString() + extra;
        ParsedCharacters += raw.Length;
        return WithTail(MarkdigInline.ParseFragment(raw, _settled.Count > 0, false));
    }

    private IReadOnlyList<TranscriptLine> WithTail(IReadOnlyList<TranscriptRun> runs) =>
        [.. _settled, new TranscriptLine(runs)
            { ContinuesPrevious = _settled.Count > 0, StartColumn = _settledColumn }];

    internal void Reset()
    {
        _tail.Clear();
        _settled.Clear();
        _preview = null;
        ParsedCharacters = 0;
        _settledColumn = 0;
    }
}
