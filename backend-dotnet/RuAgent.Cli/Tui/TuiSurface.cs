using System.Text;

namespace RuAgent.Cli.Tui;

/// <summary>
/// The single write path for transcript rows. Two sinks use it: the Terminal.Gui bridge
/// (which receives semantic presentation rows) and the append-only text writer used when
/// no terminal is available (<c>--classic</c>, redirected output, NO_COLOR, TERM=dumb).
///
/// The text sink is a printer, not a second renderer: it never positions the cursor,
/// repaints a row, or rebuilds the screen. Those capabilities were unreachable in
/// production (the CLI always suppressed repaints) and have been removed, so the
/// interactive engine is Terminal.Gui alone.
///
/// <para>Writes are serialized: a background stream continuation and the key loop both
/// append here, and a torn line would corrupt both sinks. The retained history is bounded
/// by rows and characters, matching the Terminal.Gui transcript.</para>
/// </summary>
public sealed class TuiSurface
{
    /// <summary>Lines kept so a rebuilt screen can show what it had on it.</summary>
    public const int HistoryCapacity = 2000;

    /// <summary>Character budget for the retained history and the cap for one retained line.</summary>
    public const int HistoryCharacterCapacity = 200_000;

    /// <summary>Longest single line kept in the history, in UTF-16 units.</summary>
    public const int MaxLineCharacters = 16_000;

    // One process, one console: a single gate serializes every component.
    private static readonly object Gate = new();

    private readonly Queue<string> _history = new();
    private readonly StringBuilder _pending = new();
    private int _historyCharacters;

    public TuiSurface(TextWriter writer)
    {
        Writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public TextWriter Writer { get; }

    public bool HasLineProjection => Writer is ITranscriptLineWriter;

    public ITranscriptPresentationWriter? Presentation => Writer as ITranscriptPresentationWriter;

    /// <summary>Recorded lines, for diagnostics and tests.</summary>
    public int HistoryCount
    {
        get { lock (Gate) { return _history.Count; } }
    }

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

    /// <summary>Names the model step the next streamed prose belongs to (row-marking writers only).</summary>
    public void BeginAssistantStep(string modelStepId)
    {
        lock (Gate)
        {
            if (Writer is ITranscriptStepRewriter rewriter) rewriter.BeginAssistantStep(modelStepId);
        }
    }

    /// <summary>Drops the rows a superseded step already painted (row-marking writers only).</summary>
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
                foreach (var row in rows) { Writer.WriteLine(row); CompleteLine(row); }
            }
        }
    }

    /// <summary>The last <paramref name="maxLines"/> finished lines, oldest first, clipped to width.</summary>
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
                foreach (var part in parts)
                {
                    Writer.WriteLine(part);
                    CompleteLine(part);
                }

                return;
            }

            Writer.WriteLine(text);
            CompleteLine(text);
        }
    }

    /// <summary>Ends the current row and moves down. Never erases.</summary>
    public void Line()
    {
        lock (Gate)
        {
            Writer.WriteLine();
            // Whatever was written without a newline is the row that just ended.
            CompleteLine(_pending.ToString());
        }
    }

    /// <summary>Writes finished rows and then flushes the sink.</summary>
    public void WriteBlock(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        lock (Gate)
        {
            foreach (var line in lines)
            {
                Writer.WriteLine(line);
                CompleteLine(line);
            }

            Writer.Flush();
        }
    }

    public void Flush()
    {
        lock (Gate)
        {
            Writer.Flush();
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
}
