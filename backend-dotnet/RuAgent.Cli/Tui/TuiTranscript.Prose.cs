using System.Diagnostics;

namespace RuAgent.Cli.Tui;

public sealed partial class TuiTranscript
{
    public void Flush()
    {
        if (_lineBuffer.Length > 0)
        {
            var rest = _lineBuffer;
            _lineBuffer = string.Empty;
            RenderStyled(rest);
        }
        FlushProseLine();
        // Metadata must not split an unfinished word. Publish progress only at an
        // existing transcript boundary, retaining counts rather than a second task list.
        if (_pendingPlanProgress is { } progress && progress != _lastPlanProgress)
        {
            CloseToolGroup();
            if (_surface.Presentation is not null)
                _surface.AppendPresentation(TranscriptLine.Plain("  · " + progress, TranscriptRole.Meta, TuiToken.Meta));
            else WriteLines(TuiPalette.Paint("  · " + progress, TuiToken.Meta));
            _lastPlanProgress = progress;
        }
        _pendingPlanProgress = null;
        _surface.Flush();
    }

    private static int PartialMarkerSuffixLength(string buffer)
    {
        var max = Math.Min(buffer.Length, Math.Max(ToolCallOpen.Length, ToolCallClose.Length) - 1);
        for (var len = max; len >= 1; len--)
        {
            var tail = buffer[^len..];
            if (ToolCallOpen.StartsWith(tail, StringComparison.Ordinal)
                || ToolCallClose.StartsWith(tail, StringComparison.Ordinal))
            {
                return len;
            }
        }

        return 0;
    }

    private void RenderStyled(string segment)
    {
        var rest = segment;
        while (rest.Length > 0)
        {
            var openIndex = rest.IndexOf(ToolCallOpen, StringComparison.Ordinal);
            var closeIndex = rest.IndexOf(ToolCallClose, StringComparison.Ordinal);
            int markerIndex;
            bool isOpen;
            if (openIndex >= 0 && (closeIndex < 0 || openIndex < closeIndex))
            {
                markerIndex = openIndex;
                isOpen = true;
            }
            else if (closeIndex >= 0)
            {
                markerIndex = closeIndex;
                isOpen = false;
            }
            else
            {
                WriteStyledText(rest);
                return;
            }

            if (markerIndex > 0)
            {
                WriteStyledText(rest[..markerIndex]);
            }

            if (!_atLineStart)
            {
                FlushProseLine();
                if (_inToolBlock) _surface.Line();
                _atLineStart = true;
            }

            // A buffered paragraph must not survive a tool block: its row would land after
            // the tool rows it preceded.
            FlushParagraph();

            // Prose on both sides of a tool block is independent text: a fence open when the
            // tool call started cannot survive it.
            _markdown.Reset();
            _inToolBlock = isOpen;
            rest = rest[(markerIndex + (isOpen ? ToolCallOpen.Length : ToolCallClose.Length))..];
        }
    }

    private void WriteStyledText(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (!_inToolBlock)
        {
            WriteProse(text);
            return;
        }

        var start = 0;
        while (true)
        {
            var newline = text.IndexOf('\n', start);
            var line = newline < 0 ? text[start..] : text[start..newline];
            if (line.Length > 0)
            {
                if (_surface.Presentation is not null)
                {
                    var row = new TranscriptRunBuilder();
                    if (_atLineStart) row.Append("  │ ", TuiToken.Line);
                    _surface.AppendPresentationChunk(row.Append(line, dim: true).Line(TranscriptRole.Meta));
                }
                else
                {
                    if (_atLineStart) _surface.Write(TuiPalette.Paint("  │ ", TuiToken.Line));
                    _surface.Write(TuiPalette.DimPaint(line));
                }
                _atLineStart = false;
            }

            if (newline < 0)
            {
                return;
            }

            _surface.Line();
            _atLineStart = true;
            start = newline + 1;
            if (start >= text.Length)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Prose leaves the surface through the markdown renderer. A completed line goes
    /// through <see cref="TuiMarkdown.RenderLine"/> so block forms (fence, bullet, heading)
    /// are decided from the complete raw line. A screen previews the unfinished tail
    /// in place without changing parser state; plain output waits for newline/Flush.
    /// </summary>
    private void WriteProse(string text)
    {
        if (_surface.Presentation is { } presentation)
        {
            // Tool metadata must settle before any provisional paragraph rows, not
            // between its frozen prefix and the tail when the source line completes.
            CloseToolGroup();
            _streamingMarkdown ??= new StreamingMarkdownProjector(_markdown,
                presentation.PreviewPresentation, _surface.CommitPresentation);
            _streamingMarkdown.Append(text, Width);
            _atLineStart = text.EndsWith('\n');
            return;
        }
        var start = 0;
        while (true)
        {
            var newline = text.IndexOf('\n', start);
            if (newline < 0)
            {
                if (start < text.Length)
                {
                    _proseLine.Append(text.AsSpan(start));
                    // Parse the growing tail at frame cadence, not for every token.
                    // The first fragment and completed line are always published.
                    if (_surface.HasLineProjection && (_proseLine.Length == text.Length - start
                        || Stopwatch.GetElapsedTime(_lastProsePreview).TotalMilliseconds >= ProsePreviewIntervalMilliseconds))
                    {
                        var partial = _proseLine.ToString();
                        _surface.PreviewLine(_markdown.PreviewLine(partial, Width));
                        _lastProsePreview = Stopwatch.GetTimestamp();
                    }
                    _atLineStart = false;
                }

                return;
            }

            _proseLine.Append(text.AsSpan(start, newline - start));
            WriteCompleteLine(_proseLine.ToString());
            _proseLine.Clear();
            start = newline + 1;
            if (start >= text.Length)
            {
                _atLineStart = true;
                return;
            }
        }
    }

    private void WriteCompleteLine(string line)
    {
        // Prose ends a tool block: the summary lands before the first line of the reply.
        CloseToolGroup();
        var rows = _surface.HasLineProjection
            ? _markdown.RenderLogicalLine(line, Width)
            : _markdown.RenderLine(line, Width);
        _surface.CommitProjectedLine(rows);
    }

    /// <summary>
    /// Commits native paragraph chunks before another block interrupts them.
    /// </summary>
    private void FlushParagraph()
    {
        _streamingMarkdown?.FlushParagraph();
    }

    private void FlushProseLine()
    {
        if (_surface.Presentation is not null)
        {
            _streamingMarkdown?.Flush(Width);
            _atLineStart = true;
            return;
        }
        if (_proseLine.Length > 0)
        {
            WriteCompleteLine(_proseLine.ToString());
            _proseLine.Clear();
        }

        // The unfinished line closes with the block it belonged to.
        FlushParagraph();
        _atLineStart = true;
    }

    // ------------------------------------------------------------------
    // Activity frames: detail for the live row and for the settled tool line.
    // ------------------------------------------------------------------
}
