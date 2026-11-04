using System.Text;
using RuAgent.Cli.Tui;
using RuAgent.Protocol;
using Terminal.Gui.App;

namespace RuAgent.Cli.TerminalGui;

/// <summary>
/// Projects semantic transcript rows onto Terminal.Gui on its UI thread. Interactive
/// Markdown, activities and outcomes use ITranscriptPresentationWriter directly.
/// TextWriter and the older string interfaces remain import adapters for slash-command
/// reports, diagnostics and existing callers; their ANSI parsing is outside that path.
/// </summary>
public sealed partial class TgTranscriptWriter(IApplication app, TgTranscriptView transcript) : TextWriter, ITranscriptLineWriter, ITranscriptStepRewriter, ITranscriptTurnRewriter, ITranscriptActivityWriter, ITranscriptPresentationWriter
{
    private readonly StringBuilder _pending = new();
    private bool _rowOpen;
    private int _projectedRows;
    private TgTranscriptRow[] _projectedNative = [];
    private string? _assistantStepId;
    private int _assistantStepMark = -1;
    private int _turnMark = -1;
    private long _turnRetentionVersion;
    private readonly BoundedInsertionMap<string, (int Row, long Version)> _toolRows = new(64, StringComparer.Ordinal);

    public void AppendToolRow(string activityId, string text)
    {
        var clean = TuiText.SanitizeUntrusted(text);
        try
        {
            app.Invoke(() =>
            {
                transcript.ClearLive();
                transcript.Append(clean, TranscriptStyle.Tool);
                _toolRows[activityId] = (transcript.CommittedRowCount - 1, transcript.RetentionVersion);
            });
        }
        catch (NotInitializedException) { }
    }

    public void UpdateToolRow(string activityId, string text)
    {
        var clean = TuiText.SanitizeUntrusted(text);
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
        // The renderer hands over markdown-rendered rows, so keep their paint as spans
        // the same way CommitLine does — a reconciled turn replaces the streamed block
        // with the durable text and must not flatten it back to single-attribute rows.
        var painted = prose.Select(row =>
        {
            var (clean, spans) = TgAnsiPaint.Parse(row);
            return new TgTranscriptRow(clean, TranscriptStyle.Prose, spans);
        }).ToArray();
        return await ReplaceNativeAssistantAsync(painted).ConfigureAwait(false);
    }

    private async Task<bool> ReplaceNativeAssistantAsync(IReadOnlyList<TgTranscriptRow> painted)
    {
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
                transcript.ReplaceProseSince(_turnMark, painted);
                _projectedRows = 0;
                _projectedNative = [];
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
                if (_assistantStepMark >= 0) transcript.RemoveProseSince(_assistantStepMark);
                _assistantStepMark = -1;
                _projectedRows = 0;
                _projectedNative = [];
            });
        }
        catch (NotInitializedException)
        {
        }
    }

    public void RetainDetails(IReadOnlyList<string> rows)
    {
        var clean = rows.Select(TuiText.SanitizeUntrusted).ToArray();
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

                TgTranscriptRow[] rows = clean is { Length: > 0 } || (complete && clean is not null)
                    ? [new(clean!, style, spans)] : [];
                var retained = transcript.ReplaceProjectedRows(_projectedRows, rows);
                _projectedRows = complete ? 0 : retained;
                _projectedNative = [];
            });
        }
        catch (NotInitializedException) { }
    }

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
    /// One completed line: parses the palette escapes into spans so rendered rows keep
    /// their paint. A literal <c>ctrl+o</c> in old output is ordinary text now; the key
    /// only expands model reasoning.
    /// </summary>
    private void EmitLine(string line)
    {
        var (text, spans) = TgAnsiPaint.Parse(line);
        Emit(text, spans, Classify(text), append: false);
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
    /// Legacy text-import classification only. Native transcript rows supply their role
    /// explicitly, so assistant prose is never classified from tool-like glyphs.
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
