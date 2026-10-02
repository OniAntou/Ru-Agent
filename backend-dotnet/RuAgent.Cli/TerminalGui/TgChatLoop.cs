using System.Text;
using RuAgent.Cli.Tui;
using Terminal.Gui.App;

namespace RuAgent.Cli.TerminalGui;

/// <summary>
/// Drives the Terminal.Gui chat window: Enter submits, Esc cancels, and the prompt
/// handler streams into the transcript. Command dispatch stays in
/// <c>AgentCommands.ChatLineDispatcher</c>; this class only owns input and rendering.
/// </summary>
public sealed class TgChatLoop
{
    private readonly TgChatWindow _window;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Func<string, TranscriptStream, CancellationToken, Task> _promptHandler =
        (_, _, _) => Task.CompletedTask;
    private CancellationTokenSource? _turn;

    public TgChatLoop(TgChatWindow window)
    {
        _window = window;
        window.Composer.Input.KeyDown += OnComposerKey;
    }

    public Task Completion => _completion.Task;

    public void SetPromptHandler(Func<string, TranscriptStream, CancellationToken, Task> handler) =>
        _promptHandler = handler;

    public void Submit()
    {
        var line = _window.Composer.Input.Text?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        _window.Composer.Input.Text = string.Empty;
        _window.Transcript.Append(" ▌ " + line, TranscriptStyle.User);
        _turn = new CancellationTokenSource();
        var stream = new TranscriptStream(_window.Transcript);
        _ = RunTurnAsync(line, stream, _turn.Token);
    }

    public void Cancel() => _turn?.Cancel();

    public void Stop() => _completion.TrySetResult();

    private async Task RunTurnAsync(string line, TranscriptStream stream, CancellationToken ct)
    {
        try
        {
            _window.Hints.SetBusy("⠋");
            await _promptHandler(line, stream, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            stream.Append(" esc interrupted", TranscriptStyle.Meta);
        }
        catch (Exception ex)
        {
            stream.Append(" " + ex.Message, TranscriptStyle.Error);
        }
        finally
        {
            _window.Hints.SetIdle(_window.Hints.Workspace);
        }
    }

    private void OnComposerKey(object? sender, Terminal.Gui.Input.Key key)
    {
        if (key == Terminal.Gui.Input.Key.Enter)
        {
            Submit();
            key.Handled = true;
        }
        else if (key == Terminal.Gui.Input.Key.Esc && _turn is { IsCancellationRequested: false })
        {
            Cancel();
            key.Handled = true;
        }
    }
}

/// <summary>Thin append facade handed to the prompt handler.</summary>
public sealed class TranscriptStream(TgTranscriptView transcript)
{
    public void Append(string text, TranscriptStyle style) => transcript.Append(text, style);
    public void AppendChunk(string text) => transcript.AppendChunk(text);
}

/// <summary>
/// Bridges the classic transcript renderer's <see cref="TextWriter"/> surface onto a
/// Terminal.Gui transcript view. Palette escape sequences are stripped, streamed
/// fragments grow the open row (<c>AppendChunk</c>) and completed lines commit rows;
/// appends are marshalled onto the UI loop so a background turn can never mutate the
/// view while the driver draws it.
/// </summary>
public sealed class TgTranscriptWriter(IApplication app, TgTranscriptView transcript) : TextWriter
{
    private readonly StringBuilder _pending = new();
    private bool _rowOpen;

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
            Emit(line, TranscriptStyle.Prose, append: false);
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

    private void Emit(string text, TranscriptStyle style, bool append)
    {
        try
        {
            app.Invoke(() =>
            {
                if (append)
                {
                    transcript.AppendChunk(text);
                }
                else
                {
                    transcript.Append(text, style);
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
