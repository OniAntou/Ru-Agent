using System.Diagnostics;
using System.Text;

namespace RuAgent.Cli.Tui;

/// <summary>Native streaming block state, kept separate from tool/lifecycle presentation.</summary>
internal sealed class StreamingMarkdownProjector(
    TuiMarkdown markdown,
    Action<IReadOnlyList<TranscriptLine>> preview,
    Action<IReadOnlyList<TranscriptLine>> commit)
{
    private const int PreviewIntervalMilliseconds = 33;
    private const int CommitBatchRows = 64;
    private const int CommitBatchCharacters = 16 * 1024;
    private readonly StringBuilder _line = new();
    private readonly StreamingParagraphParser _paragraph = new();
    private int _fed;
    private bool _completedLine;
    private bool _confirmedParagraph;
    private long _lastPreview;
    private List<TranscriptLine>? _commitBatch;
    private int _commitCharacters;
    private bool _batching;
    private bool _sentCommit;

    internal void Append(string text, int width)
    {
        _batching = true;
        _sentCommit = false;
        try
        {
            AppendCore(text, width);
        }
        finally
        {
            _batching = false;
            FlushCommitBatch();
        }
    }

    private void AppendCore(string text, int width)
    {
        var start = 0;
        while (start < text.Length)
        {
            var newline = text.IndexOf('\n', start);
            var end = newline < 0 ? text.Length : newline;
            var first = _line.Length == 0;
            _line.Append(text.AsSpan(start, end - start));
            if (newline >= 0)
            {
                CompleteLine(width);
                start = newline + 1;
                continue;
            }
            if (first || Stopwatch.GetElapsedTime(_lastPreview).TotalMilliseconds >= PreviewIntervalMilliseconds)
            {
                PreviewLine(width);
                _lastPreview = Stopwatch.GetTimestamp();
            }
            break;
        }
    }

    private bool IsConfirmedParagraph()
    {
        if (_confirmedParagraph) return true;
        if (markdown.InFence || _line.Length < 32) return false;
        var prefix = _line.ToString(0, Math.Min(_line.Length, 256));
        var trimmed = prefix.TrimStart();
        return _confirmedParagraph = trimmed.Length > 0 && trimmed[0] != '|'
            && markdown.Classify(prefix) == TuiMarkdown.TuiLineKind.Paragraph;
    }

    private void FeedLine()
    {
        if (_fed == 0 && _completedLine) _paragraph.Append(" ");
        _paragraph.Append(_line.ToString(_fed, _line.Length - _fed));
        _fed = _line.Length;
        _completedLine = false;
    }

    private void PreviewLine(int width)
    {
        if (IsConfirmedParagraph())
        {
            FeedLine();
            PublishPreview(_paragraph.Preview());
            return;
        }
        var partial = _line.ToString();
        if (_completedLine || markdown.Classify(partial) == TuiMarkdown.TuiLineKind.Paragraph)
            PublishPreview(_paragraph.PreviewExtra((_completedLine ? " " : "") + partial));
        else PublishPreview(markdown.PreviewPresentation(partial, width));
    }

    private void CompleteLine(int width)
    {
        var line = _line.ToString();
        if (markdown.Classify(line) == TuiMarkdown.TuiLineKind.Paragraph)
        {
            FeedLine();
            _completedLine = true;
        }
        else
        {
            FlushParagraph();
            CommitRows(markdown.RenderPresentation(line, width));
        }
        _line.Clear();
        _fed = 0;
        _confirmedParagraph = false;
    }

    internal void Flush(int width)
    {
        if (_line.Length > 0) CompleteLine(width);
        FlushParagraph();
    }

    internal void FlushParagraph()
    {
        if (!_paragraph.HasContent) return;
        CommitRows(_paragraph.Preview());
        _paragraph.Reset();
        _completedLine = false;
    }

    internal void Reset()
    {
        _commitBatch?.Clear();
        _commitCharacters = 0;
        _line.Clear();
        _paragraph.Reset();
        _fed = 0;
        _completedLine = false;
        _confirmedParagraph = false;
        _lastPreview = 0;
    }

    private void PublishPreview(IReadOnlyList<TranscriptLine> rows)
    {
        FlushCommitBatch();
        preview(rows);
    }

    private void CommitRows(IReadOnlyList<TranscriptLine> rows)
    {
        // Keep the first visible update eager. Empty commits also clear a provisional
        // preview (for example at a closing fence), so they must remain ordered barriers.
        if (!_batching || !_sentCommit || rows.Count == 0)
        {
            FlushCommitBatch();
            _sentCommit = true;
            commit(rows);
            return;
        }

        foreach (var row in rows)
        {
            var characters = 0;
            foreach (var run in row.Runs) characters += run.Text.Length;
            if (_commitBatch is { Count: > 0 }
                && (_commitBatch.Count >= CommitBatchRows || _commitCharacters + characters > CommitBatchCharacters))
                FlushCommitBatch();
            (_commitBatch ??= new List<TranscriptLine>(CommitBatchRows)).Add(row);
            _commitCharacters += characters;
        }
    }

    private void FlushCommitBatch()
    {
        if (_commitBatch is not { Count: > 0 }) return;
        // The UI consumes rows asynchronously; never pass the reusable mutable buffer.
        var rows = _commitBatch.ToArray();
        _commitBatch.Clear();
        _commitCharacters = 0;
        commit(rows);
    }
}
