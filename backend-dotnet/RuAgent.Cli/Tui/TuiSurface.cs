using System.Text;

namespace RuAgent.Cli.Tui;

/// <summary>
/// The single write path to the terminal. Every component (transcript, live row, composer,
/// dropdown) writes through this gate so a background spinner can never tear a line that the
/// key loop is painting. Mutable repaints are wrapped in synchronized output
/// (<c>CSI ?2026h</c>/<c>CSI ?2026l</c>); terminals that do not implement the mode ignore it.
/// </summary>
public sealed class TuiSurface
{
    private const string SyncStart = "\u001b[?2026h";
    private const string SyncEnd = "\u001b[?2026l";

    /// <summary>Lines kept so a rebuilt screen can show what it had on it.</summary>
    public const int HistoryCapacity = 2000;

    /// <summary>
    /// Character budget for the retained history and the cap for one retained line. The
    /// Terminal.Gui transcript enforces the same two limits; without them the classic
    /// surface bounded rows but not bytes, so one megabyte-long tool line was retained
    /// whole and a long session grew without bound.
    /// </summary>
    public const int HistoryCharacterCapacity = 200_000;

    /// <summary>Longest single line kept in the rebuild history, in UTF-16 units.</summary>
    public const int MaxLineCharacters = 16_000;

    private int _historyCharacters;

    /// <summary>Recorded lines, for rebuild diagnostics.</summary>
    public int HistoryCount
    {
        get { lock (Gate) { return _history.Count; } }
    }
    // One process, one console: a single gate serializes every component.
    private static readonly object Gate = new();

    // Transcript append point, 0-based, maintained while the sticky footer is pinned.
    // Footer, composer and live-row paints move the hardware cursor wherever they need;
    // without a recorded point the next transcript write would continue there and splice
    // its bytes into the footer rows. Every transcript write below repositions first.
    private bool _trackTranscript;
    private int _transcriptRow;
    private int _transcriptCol;
    private int _transcriptBottomRow;

    // Bounded record of finished lines, used only to repaint after a resize. Mutable repaints
    // (live rows, composer frames, panels) never enter it.
    private readonly Queue<string> _history = new();
    private readonly StringBuilder _pending = new();

    public TuiSurface(TextWriter writer)
    {
        Writer = writer ?? throw new ArgumentNullException(nameof(writer));
        SynchronizedOutput = TuiPalette.Level != TuiColorLevel.None;
    }

    public TextWriter Writer { get; }

    public bool HasLineProjection => Writer is ITranscriptLineWriter;
    public ITranscriptPresentationWriter? Presentation => Writer as ITranscriptPresentationWriter;

    public void CommitPresentation(IReadOnlyList<TranscriptLine> rows)
    {
        lock (Gate)
        {
            Presentation!.CommitPresentation(rows);
            foreach (var row in rows) CompleteLine(row.Text);
        }
    }

    public void AppendPresentation(TranscriptLine row)
    {
        lock (Gate) { Presentation!.AppendPresentation(row); CompleteLine(row.Text); }
    }

    public void AppendPresentationChunk(TranscriptLine row)
    {
        lock (Gate) { Presentation!.AppendPresentationChunk(row); Record(row.Text); }
    }

    public void AppendActivityPresentation(string activityId, TranscriptLine row, IReadOnlyList<string>? details)
    {
        lock (Gate)
        {
            Presentation!.AppendActivityPresentation(activityId, row, details);
            CompleteLine(row.Text);
        }
    }


    public void WriteActivityLine(string activityId, string text)
    {
        lock (Gate)
        {
            if (Writer is ITranscriptActivityWriter screen)
            {
                screen.AppendToolRow(activityId, text);
                CompleteLine(text);
            }
            else WriteLine(text);
        }
    }

    public void PreviewLine(string text)
    {
        lock (Gate)
        {
            if (Writer is ITranscriptLineWriter projection) projection.PreviewLine(text);
        }
    }

    /// <summary>
    /// Names the model step the next streamed prose belongs to. Only a row-marking
    /// writer (the Terminal.Gui bridge) acts on it; a raw console cannot unprint.
    /// </summary>
    public void BeginAssistantStep(string modelStepId)
    {
        lock (Gate)
        {
            if (Writer is ITranscriptStepRewriter rewriter) rewriter.BeginAssistantStep(modelStepId);
        }
    }

    /// <summary>
    /// Drops the rows a superseded step already painted — a retried attempt or a reply
    /// the host suppressed for recovery. A no-op without a row-marking writer.
    /// </summary>
    public void RewindAssistantStep(string? modelStepId = null)
    {
        lock (Gate)
        {
            if (Writer is ITranscriptStepRewriter rewriter) rewriter.RewindAssistantStep(modelStepId);
        }
    }

    public void CommitProjectedLine(IReadOnlyList<string> rows)
    {
        lock (Gate)
        {
            if (Writer is ITranscriptLineWriter projection)
            {
                projection.CommitLine(rows);
                foreach (var row in rows) CompleteLine(row);
            }
            else
            {
                foreach (var row in rows) { Write(row); Line(); }
            }
        }
    }

    /// <summary>Wrap mutable repaints in synchronized output. Enabled whenever colour is on.</summary>
    public bool SynchronizedOutput { get; set; }

    /// <summary>
    /// True when the writer is not a live console (redirected output, tests). Repaints are
    /// then dropped instead of polluting a log with carriage-return rows.
    /// </summary>
    public bool SuppressRepaints { get; set; }

    /// <summary>
    /// Replaces the last <paramref name="count"/> recorded lines with
    /// <paramref name="replacement"/>, so a rebuilt screen reprints the corrected rows
    /// instead of what was streamed. The in-progress line is kept: a repaint happens between
    /// writes, never mid-line. Returns false when the recorded history is shorter than the
    /// request, which means the rows have already scrolled out of the rebuildable window and
    /// pretending to repair them would corrupt the transcript.
    /// </summary>
    public bool ReplaceRecordedTail(int count, IReadOnlyList<string> replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (count <= 0)
        {
            return false;
        }

        lock (Gate)
        {
            if (_history.Count < count)
            {
                return false;
            }

            var kept = new List<string>(_history.Count - count);
            var index = 0;
            foreach (var line in _history)
            {
                if (index++ >= _history.Count - count)
                {
                    break;
                }

                kept.Add(line);
            }

            _history.Clear();
            foreach (var line in kept)
            {
                _history.Enqueue(line);
            }

            foreach (var line in replacement)
            {
                _history.Enqueue(line);
                while (_history.Count > HistoryCapacity)
                {
                    _history.Dequeue();
                }
            }

            // The queue was rebuilt by hand above, so recompute the character budget from
            // what actually survived instead of trying to track every enqueue/dequeue.
            _historyCharacters = 0;
            foreach (var line in _history)
            {
                _historyCharacters += line.Length;
            }

            return true;
        }
    }

    /// <summary>
    /// Starts pinning transcript appends to the tracked cursor, at the bottom row of the
    /// pinned region. Called by the sticky region when it takes over the footer; stopped
    /// by <see cref="StopTranscriptCursorTracking"/> when it leaves. While active, every
    /// transcript write first moves the hardware cursor back to the recorded point, so a
    /// footer, composer or live-row repaint that ran between two transcript writes cannot
    /// strand the next bytes in the wrong row. Emits nothing itself.
    /// </summary>
    public void TrackTranscriptCursor(int bottomRow)
    {
        lock (Gate)
        {
            _trackTranscript = true;
            _transcriptBottomRow = Math.Max(0, bottomRow);
            _transcriptRow = _transcriptBottomRow;
            _transcriptCol = 0;
        }
    }

    public void StopTranscriptCursorTracking()
    {
        lock (Gate)
        {
            _trackTranscript = false;
        }
    }

    /// <summary>Moves the hardware cursor to the recorded transcript point. Gate must be held.</summary>
    private void RepositionTranscriptCursor(TextWriter writer)
    {
        if (!_trackTranscript)
        {
            return;
        }

        writer.Write($"\u001b[{_transcriptRow + 1};{_transcriptCol + 1}H");
    }

    /// <summary>
    /// Advances the recorded point through text exactly as the terminal would move its
    /// cursor: printable runes by cell width with wrapping, tab stops every 8 cells,
    /// carriage return home, newline down with scroll clamped at the region bottom.
    /// Escape sequences move nothing here. Gate must be held.
    /// </summary>
    private void AdvanceTranscriptCursor(string text)
    {
        if (!_trackTranscript)
        {
            return;
        }

        var width = TuiViewport.Width(int.MaxValue);
        if (width < 1)
        {
            width = int.MaxValue;
        }

        var index = 0;
        while (index < text.Length)
        {
            var c = text[index];
            if (c == '\u001b')
            {
                index = TuiText.SkipEscapeSequence(text, index);
                continue;
            }

            if (c == '\r')
            {
                _transcriptCol = 0;
                index++;
                continue;
            }

            if (c == '\n')
            {
                TranscriptCursorNewLine();
                index++;
                continue;
            }

            if (c == '\t')
            {
                _transcriptCol = (_transcriptCol / 8 + 1) * 8;
                WrapTranscriptCursor(width);
                index++;
                continue;
            }

            var status = System.Text.Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out var consumed);
            if (status != System.Buffers.OperationStatus.Done || consumed <= 0)
            {
                consumed = 1;
                AdvanceTranscriptCells(1, width);
            }
            else
            {
                AdvanceTranscriptCells(TuiText.RuneWidth(rune.Value), width);
            }

            index += consumed;
        }
    }

    private void AdvanceTranscriptCells(int cells, int width)
    {
        for (var k = 0; k < cells; k++)
        {
            if (_transcriptCol >= width)
            {
                _transcriptCol = 0;
                TranscriptCursorNewLine();
            }

            _transcriptCol++;
        }
    }

    private void WrapTranscriptCursor(int width)
    {
        while (_transcriptCol >= width)
        {
            _transcriptCol -= width;
            TranscriptCursorNewLine();
        }
    }

    private void TranscriptCursorNewLine()
    {
        if (_transcriptRow < _transcriptBottomRow)
        {
            _transcriptRow++;
        }

        // At the bottom the region scrolls underneath: the logical row stays put.
        _transcriptCol = 0;
    }

    /// <summary>
    /// The last <paramref name="maxLines"/> finished lines, oldest first, each clipped to
    /// <paramref name="width"/>. This is what a rebuilt screen reprints so a resize does not
    /// blank the transcript.
    /// </summary>
    public IReadOnlyList<string> SnapshotTail(int maxLines, int width)
    {
        lock (Gate)
        {
            var take = Math.Clamp(maxLines, 0, _history.Count);
            if (take == 0)
            {
                return [];
            }

            return _history
                .Skip(_history.Count - take)
                .Select(line => TuiText.Truncate(line, width))
                .ToList();
        }
    }

    public void Write(string text)
    {
        lock (Gate)
        {
            RepositionTranscriptCursor(Writer);
            Writer.Write(text);
            Record(text);
            AdvanceTranscriptCursor(text);
        }
    }

    public void WriteLine(string text)
    {
        lock (Gate)
        {
            RepositionTranscriptCursor(Writer);
            if (text.Contains('\n'))
            {
                var parts = text.Replace("\r", "").Split('\n');
                for (var i = 0; i < parts.Length; i++)
                {
                    ClearCurrentRow(Writer);
                    Writer.WriteLine(parts[i]);
                    CompleteLine(parts[i]);
                }
                AdvanceTranscriptCursor(text);
                return;
            }

            ClearCurrentRow(Writer);
            Writer.WriteLine(text);
            CompleteLine(text);
            AdvanceTranscriptCursor(text);
        }
    }

    /// <summary>
    /// Whole-line write that stays out of the rebuild history. Resize rebuilds reprint the
    /// header and the transcript tail: recording those reprints would feed copies back into
    /// <see cref="SnapshotTail"/>, so every rebuild would print more headers than the last
    /// and grow the scrollback without bound.
    /// </summary>
    public void WriteLineVolatile(string text)
    {
        lock (Gate)
        {
            if (text.Contains('\n'))
            {
                var parts = text.Replace("\r", "").Split('\n');
                for (var i = 0; i < parts.Length; i++)
                {
                    ClearCurrentRow(Writer);
                    Writer.WriteLine(parts[i]);
                }
                return;
            }

            ClearCurrentRow(Writer);
            Writer.WriteLine(text);
        }
    }

    /// <summary>
    /// Ends the current row and moves down. This never erases: the row holds transcript
    /// content written without a trailing newline, and clearing it here destroyed every
    /// body row on a live console while leaving headers, footers and notices (which erase
    /// before they write, onto rows that are empty at that point) intact. Erasing belongs
    /// to explicit clears only (<see cref="ClearSingleLine"/>, <see cref="RepaintSingleLine"/>,
    /// and the documented leading erase in <see cref="WriteBlock"/>).
    /// </summary>
    public void Line()
    {
        lock (Gate)
        {
            RepositionTranscriptCursor(Writer);
            Writer.WriteLine();

            // Whatever was written without a newline is the row that just ended.
            CompleteLine(_pending.ToString());
            AdvanceTranscriptCursor("\n");
        }
    }

    /// <summary>Splits written text into finished lines and records them.</summary>
    private void Record(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        var start = 0;
        while (true)
        {
            var newline = text.IndexOf('\n', start);
            if (newline < 0)
            {
                _pending.Append(text, start, text.Length - start);
                return;
            }

            _pending.Append(text, start, newline - start);
            CompleteLine(_pending.ToString());
            start = newline + 1;
            if (start >= text.Length)
            {
                return;
            }
        }
    }

    private void CompleteLine(string line)
    {
        _pending.Clear();
        var bounded = line.Length > MaxLineCharacters
            ? TuiText.SafeCut(line, MaxLineCharacters)
            : line;
        _history.Enqueue(bounded);
        _historyCharacters += bounded.Length;
        while (_history.Count > HistoryCapacity)
        {
            _historyCharacters -= _history.Dequeue().Length;
        }

        while (_history.Count > 1 && _historyCharacters > HistoryCharacterCapacity)
        {
            _historyCharacters -= _history.Dequeue().Length;
        }
    }

    /// <summary>
    /// Every whole-line write first erases the current row: a live row occupies that row, and
    /// writing over it without clearing would leave the tail of the old row visible.
    /// </summary>
    private void ClearCurrentRow(TextWriter writer)
    {
        if (SuppressRepaints)
        {
            return;
        }

        writer.Write('\r');
        writer.Write("\u001b[2K");
        // Mirror the carriage return in the tracked point: the row is intentionally
        // restarted, so whatever column the transcript had reached no longer applies.
        // Gate is always held by the callers (WriteLine, WriteBlock).
        if (_trackTranscript)
        {
            _transcriptCol = 0;
        }
    }

    public void Flush()
    {
        lock (Gate)
        {
            Writer.Flush();
        }
    }

    /// <summary>Runs a mutable repaint atomically, inside a synchronized-output batch.</summary>
    public void Repaint(Action<TextWriter> paint)
    {
        ArgumentNullException.ThrowIfNull(paint);
        lock (Gate)
        {
            if (SynchronizedOutput)
            {
                Writer.Write(SyncStart);
            }

            try
            {
                paint(Writer);
            }
            finally
            {
                // A paint that throws (measurement, encoding, a bad rune) must not leave the
                // terminal inside an unterminated synchronized-output batch: the frame would
                // appear frozen and every later batch would nest another start marker.
                if (SynchronizedOutput)
                {
                    Writer.Write(SyncEnd);
                }

                Writer.Flush();
            }
        }
    }

    /// <summary>
    /// Holds the write gate across a whole multi-step screen rebuild (clear, header,
    /// transcript tail, footer, composer): a streaming append landing mid-rebuild would
    /// overflow the viewport and scroll the fresh frame into the scrollback. The body
    /// must be synchronous — never await inside. Reentrant: surface calls nest freely.
    /// </summary>
    public void Rebuild(Action<TextWriter> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        lock (Gate)
        {
            build(Writer);
            Writer.Flush();
        }
    }

    /// <summary>Repaints the current row in place: carriage return, clear, content clipped to width.</summary>
    public void RepaintSingleLine(string content, int width)
    {
        if (SuppressRepaints)
        {
            return;
        }

        Repaint(writer =>
        {
            writer.Write('\r');
            writer.Write("\u001b[2K");
            writer.Write(TuiText.Truncate(content, ClampToLiveWidth(width)));
        });
    }

    /// <summary>
    /// Bound a frame to the terminal as it is right now. Cached widths go stale the moment a
    /// window is dragged, and a single over-long row wraps — which misplaces every absolute
    /// cursor move that follows it.
    /// </summary>
    private int ClampToLiveWidth(int width)
    {
        if (SuppressRepaints)
        {
            return width;
        }

        var live = TuiViewport.Width(int.MaxValue);
        return live == int.MaxValue ? width : Math.Min(width, live);
    }

    public void ClearSingleLine(int width)
    {
        _ = width;
        if (SuppressRepaints)
        {
            return;
        }

        Repaint(writer =>
        {
            writer.Write('\r');
            writer.Write("\u001b[2K");
        });
    }

    /// <summary>
    /// Writes a finished block while keeping the live row intact: the row is cleared, the
    /// block lands, and the row is repainted in the same lock acquisition so a concurrent
    /// spinner tick can never interleave between the lines.
    /// </summary>
    public void WriteBlock(IReadOnlyList<string> lines, string? liveRow)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var limit = SuppressRepaints ? int.MaxValue : TuiViewport.Width(int.MaxValue);
        Repaint(writer =>
        {
            RepositionTranscriptCursor(writer);
            ClearCurrentRow(writer);

            foreach (var line in lines)
            {
                var clipped = limit == int.MaxValue ? line : TuiText.Truncate(line, limit);
                writer.WriteLine(clipped);
                CompleteLine(line);
                AdvanceTranscriptCursor(clipped + "\n");
            }

            if (liveRow is not null && !SuppressRepaints)
            {
                var row = limit == int.MaxValue ? liveRow : TuiText.Truncate(liveRow, limit);
                writer.Write(row);
                AdvanceTranscriptCursor(row);
            }
        });
    }
}
