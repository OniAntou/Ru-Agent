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
/// <para>Writes to a sink are serialized: a background stream continuation and the key loop both
/// append here. Transcript history and retention belong to the selected view.</para>
/// </summary>
public sealed class TuiSurface
{
    private readonly object Gate = new();

    public TuiSurface(TextWriter writer)
    {
        Writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public TextWriter Writer { get; }

    public bool HasLineProjection => Writer is ITranscriptLineWriter;

    public ITranscriptPresentationWriter? Presentation => Writer as ITranscriptPresentationWriter;

    public void CommitPresentation(IReadOnlyList<TranscriptLine> rows)
    {
        lock (Gate)
        {
            Presentation!.CommitPresentation(rows);
        }
    }

    public void AppendPresentation(TranscriptLine row)
    {
        lock (Gate) { Presentation!.AppendPresentation(row); }
    }

    public void AppendPresentationChunk(TranscriptLine row)
    {
        lock (Gate) { Presentation!.AppendPresentationChunk(row); }
    }

    public void AppendActivityPresentation(string activityId, TranscriptLine row, IReadOnlyList<string>? details)
    {
        lock (Gate)
        {
            Presentation!.AppendActivityPresentation(activityId, row, details);
        }
    }

    public void WriteActivityLine(string activityId, string text)
    {
        lock (Gate)
        {
            if (Writer is ITranscriptActivityWriter screen)
            {
                screen.AppendToolRow(activityId, text);
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
            }
            else
            {
                foreach (var row in rows) { Writer.WriteLine(row); }
            }
        }
    }

    public void Write(string text)
    {
        lock (Gate)
        {
            Writer.Write(text);
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
                }

                return;
            }

            Writer.WriteLine(text);
        }
    }

    /// <summary>Ends the current row and moves down. Never erases.</summary>
    public void Line()
    {
        lock (Gate)
        {
            Writer.WriteLine();
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
}
