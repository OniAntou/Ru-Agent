using System.Text;
using RuAgent.Cli.Tui;
using Terminal.Gui.App;

namespace RuAgent.Cli.TerminalGui;

/// <summary>
/// Bridges the classic transcript renderer's <see cref="TextWriter"/> surface onto a
/// Terminal.Gui transcript view. Palette escape sequences are stripped, streamed
/// fragments grow the open row (<c>AppendChunk</c>) and completed lines commit rows;
/// appends are marshalled onto the UI loop so a background turn can never mutate the
/// view while the driver draws it.
/// </summary>
public sealed class TgTranscriptWriter(IApplication app, TgTranscriptView transcript) : TextWriter, ITranscriptLineWriter, ITranscriptStepRewriter, ITranscriptTurnRewriter, ITranscriptActivityWriter
{
    private readonly StringBuilder _pending = new();
    private bool _rowOpen;
    private bool _projectedRow;
    private string? _assistantStepId;
    private int _assistantStepMark = -1;
    private int _turnMark = -1;
    private long _turnRetentionVersion;
    private readonly Dictionary<string, (int Row, long Version)> _toolRows = new(StringComparer.Ordinal);

    public void AppendToolRow(string activityId, string text)
    {
        var clean = TuiText.Strip(text);
        var hidden = clean.Contains("ctrl+o", StringComparison.Ordinal)
            ? RetainedLinesProvider?.Invoke().Select(TuiText.Strip).ToArray() : null;
        try
        {
            app.Invoke(() =>
            {
                transcript.ClearLive();
                if (hidden is { Length: > 0 }) transcript.AppendCollapsed(clean, hidden, TranscriptStyle.Tool);
                else transcript.Append(clean, TranscriptStyle.Tool);
                _toolRows[activityId] = (transcript.CommittedRowCount - 1, transcript.RetentionVersion);
                while (_toolRows.Count > 64) _toolRows.Remove(_toolRows.Keys.First());
            });
        }
        catch (NotInitializedException) { }
    }

    public void UpdateToolRow(string activityId, string text)
    {
        var clean = TuiText.Strip(text);
        try
        {
            app.Invoke(() =>
            {
                if (_toolRows.TryGetValue(activityId, out var mark) && mark.Version == transcript.RetentionVersion)
                    transcript.ReplaceToolRow(mark.Row, clean);
            });
        }
        catch (NotInitializedException) { }
    }

    public void BeginAssistantTurn()
    {
        try
        {
            app.Invoke(() =>
            {
                _turnMark = transcript.CommittedRowCount;
                _turnRetentionVersion = transcript.RetentionVersion;
            });
        }
        catch (NotInitializedException) { _turnMark = -1; }
    }

    public async Task<bool> ReplaceAssistantTurnAsync(IReadOnlyList<string> prose)
    {
        var clean = prose.Select(TuiText.Strip).ToArray();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            app.Invoke(() =>
            {
                if (completion.Task.IsCompleted) return;
                if (_turnMark < 0 || _turnMark > transcript.CommittedRowCount
                    || _turnRetentionVersion != transcript.RetentionVersion)
                {
                    completion.TrySetResult(false);
                    return;
                }
                transcript.ReplaceProseSince(_turnMark, clean);
                _projectedRow = false;
                completion.TrySetResult(true);
            });
        }
        catch (NotInitializedException) { completion.TrySetResult(false); }
        try { return await completion.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { completion.TrySetResult(false); return false; }
    }

    public void PreviewLine(string text) => ProjectLine(text.Length == 0 ? null : text, complete: false);

    /// <summary>
    /// Names the model step the next streamed prose belongs to. The first fragment of a
    /// step records where that step's rows begin, so a superseded step can leave the
    /// screen instead of stacking a duplicate answer under the one that replaced it.
    /// </summary>
    public void BeginAssistantStep(string modelStepId)
    {
        if (string.Equals(_assistantStepId, modelStepId, StringComparison.Ordinal))
        {
            return;
        }

        _assistantStepId = modelStepId;
        try
        {
            app.Invoke(() => _assistantStepMark = transcript.CommittedRowCount);
        }
        catch (NotInitializedException)
        {
            _assistantStepMark = -1;
        }
    }

    /// <summary>
    /// Drops the rows painted since the named step began, when it is the step still
    /// current. A null name means the current step: the durable suppression event
    /// carries no step identity, and it always follows the step that just streamed.
    /// </summary>
    public void RewindAssistantStep(string? modelStepId = null)
    {
        if (modelStepId is not null && !string.Equals(_assistantStepId, modelStepId, StringComparison.Ordinal))
        {
            return;
        }

        _assistantStepId = null;
        _pending.Clear();
        _rowOpen = false;
        try
        {
            app.Invoke(() =>
            {
                // Invoke queues worker-thread calls. Read the mark here, after
                // BeginAssistantStep has executed, rather than before that queued
                // callback has had a chance to record it.
                if (_assistantStepMark >= 0) transcript.DropRowsSince(_assistantStepMark);
                _assistantStepMark = -1;
                _projectedRow = false;
            });
        }
        catch (NotInitializedException)
        {
        }
    }

    public void RetainDetails(IReadOnlyList<string> rows)
    {
        var clean = rows.Select(TuiText.Strip).ToArray();
        try { app.Invoke(() => transcript.SetCollapsedDetails(clean, TranscriptStyle.Meta)); }
        catch (NotInitializedException) { }
    }

    public void CommitLine(IReadOnlyList<string> rows)
    {
        ProjectLine(rows.Count == 0 ? null : rows[0], complete: true);
        foreach (var row in rows.Skip(1)) EmitLine(row);
    }

    private void ProjectLine(string? text, bool complete)
    {
        var (clean, spans) = text is null ? (null, (IReadOnlyList<TgSpan>?)null) : TgAnsiPaint.Parse(text);
        try
        {
            app.Invoke(() =>
            {
                var style = Classify(clean ?? string.Empty);
                if (style == TranscriptStyle.Tool)
                {
                    // A settled tool row replaces the live activity row, including the
                    // single-line path that never reaches Emit.
                    transcript.ClearLive();
                }

                if (_projectedRow) transcript.ReplaceTail(clean, style, spans);
                else if (clean is { Length: > 0 } || (complete && clean is not null))
                    transcript.Append(clean!, style, spans);
                _projectedRow = !complete && clean is { Length: > 0 };
            });
        }
        catch (NotInitializedException) { }
    }

    /// <summary>
    /// The classic renderer's retained <c>ctrl+o</c> rows. When a committed line advertises
    /// expansion and this supplies rows, the line is bridged as a collapsed block instead
    /// of a plain row, so Ctrl+O reveals the hidden diff/thinking lines end to end.
    /// </summary>
    public Func<IReadOnlyList<string>>? RetainedLinesProvider { get; set; }

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        if (value == '\r')
        {
            return;
        }

        if (value == '\n')
        {
            CommitStream();
            _rowOpen = false;
            return;
        }

        _pending.Append(value);
    }

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        var start = 0;
        while (true)
        {
            var newline = value.IndexOf('\n', start);
            if (newline < 0)
            {
                AppendPending(value.AsSpan(start));
                return;
            }

            AppendPending(value.AsSpan(start, newline - start));
            CommitStream();
            _rowOpen = false;
            start = newline + 1;
            if (start >= value.Length)
            {
                return;
            }
        }
    }

    public override void WriteLine(string? value)
    {
        // A full-line write is its own row: whatever was streamed so far is committed
        // first, then the newline-held line lands below it.
        CommitStream();
        _rowOpen = false;
        var text = (value ?? string.Empty).Replace("\r", string.Empty);
        foreach (var line in text.Split('\n'))
        {
            EmitLine(line);
        }
    }

    public override void WriteLine()
    {
        var committed = CommitStream();
        if (!committed && !_rowOpen)
        {
            Emit(string.Empty, null, TranscriptStyle.Prose, append: false);
        }

        _rowOpen = false;
    }

    public override void Flush() => CommitStream();

    private void AppendPending(ReadOnlySpan<char> span)
    {
        foreach (var c in span)
        {
            if (c != '\r')
            {
                _pending.Append(c);
            }
        }
    }

    /// <summary>Commits the pending fragment onto the open row; returns true when text landed.</summary>
    private bool CommitStream()
    {
        if (_pending.Length == 0)
        {
            return false;
        }

        var (text, spans) = TgAnsiPaint.Parse(_pending.ToString());
        _pending.Clear();
        if (text.Length == 0)
        {
            return false;
        }

        Emit(text, spans, Classify(text), append: _rowOpen);
        _rowOpen = true;
        return true;
    }

    /// <summary>
    /// One completed line: parses the palette escapes into spans (markdown rows keep their
    /// paint), and when the line carries the classic <c>ctrl+o</c> affordance, bridges the
    /// renderer's retained rows as a collapsed block so <see cref="TgTranscriptView.ToggleExpanded"/>
    /// reveals them.
    /// </summary>
    private void EmitLine(string line)
    {
        var (text, spans) = TgAnsiPaint.Parse(line);
        if (text.EndsWith("ctrl+o", StringComparison.Ordinal)
            && RetainedLinesProvider?.Invoke() is { Count: > 0 } hidden)
        {
            var stripped = hidden.Select(TuiText.Strip).ToList();
            stripped.Add("    │ · end of expanded block");
            var style = Classify(text);
            // A collapsed supplementary block is dim like the affordance that opened it,
            // never prose-bright, even when the summary row itself classifies as prose.
            EmitCollapsed(text, stripped, style == TranscriptStyle.Prose ? TranscriptStyle.Tool : style);
            return;
        }

        Emit(text, spans, Classify(text), append: false);
    }

    private void EmitCollapsed(string summary, IReadOnlyList<string> hidden, TranscriptStyle style)
    {
        try
        {
            app.Invoke(() =>
            {
                // A collapsed tool/diff block is the settle of a running activity: the
                // live row must leave before the permanent rows land in its place.
                transcript.ClearLive();
                transcript.AppendCollapsed(summary, hidden, style);
            });
        }
        catch (NotInitializedException)
        {
            // The TG session has already shut down; late renderer output is dropped
            // instead of crashing the wind-down.
        }
    }

    private void Emit(string text, IReadOnlyList<TgSpan>? spans, TranscriptStyle style, bool append)
    {
        // Callers pre-parse the paint, so no defensive strip here. Unknown-escape rows and
        // plain rows both arrive with null spans and keep their old single-attribute path.
        try
        {
            app.Invoke(() =>
            {
                if (style == TranscriptStyle.Tool)
                {
                    // A settled tool row replaces the live activity row: the block was
                    // the running form of exactly this row.
                    transcript.ClearLive();
                }

                if (append)
                {
                    transcript.AppendChunk(text, spans);
                }
                else
                {
                    transcript.Append(text, style, spans);
                }
            });
        }
        catch (NotInitializedException)
        {
            // The TG session has already shut down; late renderer output is dropped
            // instead of crashing the wind-down.
        }
    }

    /// <summary>
    /// Minimal role split for bridged rows: tool rows keep their glyph vocabulary, the
    /// classic notice marker stays meta, everything else is prose. Task 15's parity pass
    /// replaces this with a structured renderer.
    /// </summary>
    private static TranscriptStyle Classify(string text)
    {
        if (text.StartsWith("  · ", StringComparison.Ordinal))
        {
            return TranscriptStyle.Meta;
        }

        var head = text.TrimStart(' ');
        return head.Length >= 2
            && head[0] is '→' or '✱' or '←' or '$' or '%' or '⚙' or '✓' or '✗' or '▸' or '·'
            && head[1] == ' '
                ? TranscriptStyle.Tool
                : TranscriptStyle.Prose;
    }
}
