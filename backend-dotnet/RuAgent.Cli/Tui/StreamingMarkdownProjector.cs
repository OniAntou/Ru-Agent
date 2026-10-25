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
    private readonly StringBuilder _line = new();
    private readonly StreamingParagraphParser _paragraph = new();
    private int _fed;
    private bool _completedLine;
    private bool _confirmedParagraph;
    private long _lastPreview;

    internal void Append(string text, int width)
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
            preview(_paragraph.Preview());
            return;
        }
        var partial = _line.ToString();
        if (_completedLine || markdown.Classify(partial) == TuiMarkdown.TuiLineKind.Paragraph)
            preview(_paragraph.PreviewExtra((_completedLine ? " " : "") + partial));
        else preview(markdown.PreviewPresentation(partial, width));
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
            commit(markdown.RenderPresentation(line, width));
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
        commit(_paragraph.Preview());
        _paragraph.Reset();
        _completedLine = false;
    }

    internal void Reset()
    {
        _line.Clear();
        _paragraph.Reset();
        _fed = 0;
        _completedLine = false;
        _confirmedParagraph = false;
        _lastPreview = 0;
    }
}
