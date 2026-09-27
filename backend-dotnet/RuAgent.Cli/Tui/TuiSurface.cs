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

    /// <summary>Recorded lines, for rebuild diagnostics.</summary>
    public int HistoryCount
    {
        get { lock (Gate) { return _history.Count; } }
    }
    // One process, one console: a single gate serializes every component.
    private static readonly object Gate = new();

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

            return true;
        }
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
            Writer.Write(text);
            Record(text);
        }
    }

    public void WriteLine(string text)
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
                    CompleteLine(parts[i]);
                }
                return;
            }

            ClearCurrentRow(Writer);
            Writer.WriteLine(text);
            CompleteLine(text);
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

    public void Line()
    {
        lock (Gate)
        {
            ClearCurrentRow(Writer);
            Writer.WriteLine();

            // Whatever was written without a newline is the row that just ended.
            CompleteLine(_pending.ToString());
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
        _history.Enqueue(line);
        while (_history.Count > HistoryCapacity)
        {
            _history.Dequeue();
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

            paint(Writer);

            if (SynchronizedOutput)
            {
                Writer.Write(SyncEnd);
            }

            Writer.Flush();
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
            ClearCurrentRow(writer);

            foreach (var line in lines)
            {
                var clipped = limit == int.MaxValue ? line : TuiText.Truncate(line, limit);
                writer.WriteLine(clipped);
                CompleteLine(line);
            }

            if (liveRow is not null && !SuppressRepaints)
            {
                writer.Write(limit == int.MaxValue ? liveRow : TuiText.Truncate(liveRow, limit));
            }
        });
    }
}
