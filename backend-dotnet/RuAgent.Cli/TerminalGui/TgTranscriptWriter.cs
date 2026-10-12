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
public sealed class TgTranscriptWriter(IApplication app, TgTranscriptView transcript) : TextWriter, ITranscriptLineWriter
{
    private readonly StringBuilder _pending = new();
    private bool _rowOpen;
    private bool _projectedRow;

    public void PreviewLine(string text) => ProjectLine(text.Length == 0 ? null : text, complete: false);

    public void CommitLine(IReadOnlyList<string> rows)
    {
        ProjectLine(rows.Count == 0 ? null : rows[0], complete: true);
        foreach (var row in rows.Skip(1)) EmitLine(row);
    }

    private void ProjectLine(string? text, bool complete)
    {
        var clean = text is null ? null : TuiText.Strip(text);
        try
        {
            app.Invoke(() =>
            {
                if (_projectedRow) transcript.ReplaceTail(clean, Classify(clean ?? string.Empty));
                else if (clean is { Length: > 0 } || (complete && clean is not null))
                    transcript.Append(clean!, Classify(clean!));
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
            Emit(string.Empty, TranscriptStyle.Prose, append: false);
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

        var text = TuiText.Strip(_pending.ToString());
        _pending.Clear();
        if (text.Length == 0)
        {
            return false;
        }

        Emit(text, Classify(text), append: _rowOpen);
        _rowOpen = true;
        return true;
    }

    /// <summary>
    /// One completed line: strips the palette escapes every path shares, and when the line
    /// carries the classic <c>ctrl+o</c> affordance, bridges the renderer's retained rows
    /// as a collapsed block so <see cref="TgTranscriptView.ToggleExpanded"/> reveals them.
    /// </summary>
    private void EmitLine(string line)
    {
        var text = TuiText.Strip(line);
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

        Emit(text, Classify(text), append: false);
    }

    private void EmitCollapsed(string summary, IReadOnlyList<string> hidden, TranscriptStyle style)
    {
        try
        {
            app.Invoke(() => transcript.AppendCollapsed(summary, hidden, style));
        }
        catch (NotInitializedException)
        {
            // The TG session has already shut down; late renderer output is dropped
            // instead of crashing the wind-down.
        }
    }

    private void Emit(string text, TranscriptStyle style, bool append)
    {
        // Defensive strip: streamed fragments are stripped in CommitStream, but every
        // full-line path (WriteBlock, Notice, tool rows) also lands here.
        var clean = TuiText.Strip(text);
        try
        {
            app.Invoke(() =>
            {
                if (append)
                {
                    transcript.AppendChunk(clean);
                }
                else
                {
                    transcript.Append(clean, style);
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
            && head[0] is '→' or '✱' or '←' or '$' or '%' or '⚙' or '✓' or '✗' or '▸'
            && head[1] == ' '
                ? TranscriptStyle.Tool
                : TranscriptStyle.Prose;
    }
}
