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
    /// <summary>Lines kept for Up/Down navigation; older entries fall off the front.</summary>
    public const int MaxHistory = 100;

    /// <summary>Prompts typed while a turn streams wait here; beyond this depth they are dropped oldest-first.</summary>
    public const int MaxQueueDepth = 8;

    private const string SpinnerFrame = "⠋";
    private const string QueueHint = "enter queues · esc cancels";

    private readonly TgChatWindow _window;
    private readonly List<string> _history = [];
    private readonly Queue<string> _queued = new();
    private readonly object _queueGate = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Func<string, TranscriptStream, CancellationToken, Task> _promptHandler =
        (_, _, _) => Task.CompletedTask;
    private Func<string>? _attachmentLabelProvider;
    private Func<int>? _attachmentCountProvider;
    private Func<Task>? _pasteHandler;
    private Func<string, bool>? _dropHandler;
    private CancellationTokenSource? _turn;
    private int _historyIndex;
    private string _draft = string.Empty;
    private volatile bool _busy;

    public TgChatLoop(TgChatWindow window)
    {
        _window = window;
        window.Composer.Input.KeyDown += OnComposerKey;
        // Stop() is the explicit signal, but the window can also wind down on its own
        // (app stop/close); the runnable leaving its running state must release waiters.
        window.IsRunningChanged += (_, args) =>
        {
            if (!args.Value)
            {
                _completion.TrySetResult();
            }
        };
    }

    public Task Completion => _completion.Task;

    public void SetPromptHandler(Func<string, TranscriptStream, CancellationToken, Task> handler) =>
        _promptHandler = handler;

    /// <summary>Feeds the hint row's idle label; the provider owns the count/workspace wording.</summary>
    public void SetAttachmentLabelProvider(Func<string> provider) => _attachmentLabelProvider = provider;

    /// <summary>Pending attachment count, used to allow an attachment-only (empty) submit.</summary>
    public void SetAttachmentCountProvider(Func<int> provider) => _attachmentCountProvider = provider;

    /// <summary>Ctrl+V handler (clipboard image/text); a null handler leaves the key to the input.</summary>
    public void SetPasteHandler(Func<Task> handler) => _pasteHandler = handler;

    /// <summary>
    /// A dropped path in the composer text: returns true when the line was consumed as an
    /// attachment. The handler starts its own async attach; the loop just routes the line.
    /// </summary>
    public void SetDropHandler(Func<string, bool> handler) => _dropHandler = handler;

    /// <summary>Repaints the idle hint from the provider; a busy turn keeps its spinner.</summary>
    public void RefreshAttachmentLabel()
    {
        if (_attachmentLabelProvider is null || _busy)
        {
            return;
        }

        _window.Hints.SetIdle(_attachmentLabelProvider());
    }

    public void Submit()
    {
        var line = _window.Composer.Input.Text?.ToString() ?? string.Empty;
        if (_dropHandler is not null)
        {
            var candidate = line.Trim();
            if (candidate.Length > 0 && _dropHandler(candidate))
            {
                _window.Composer.Input.Text = string.Empty;
                return;
            }
        }

        var hasAttachments = (_attachmentCountProvider?.Invoke() ?? 0) > 0;
        if (string.IsNullOrWhiteSpace(line) && !hasAttachments)
        {
            return;
        }

        _window.Composer.Input.Text = string.Empty;
        Remember(line);
        _historyIndex = _history.Count;
        _draft = string.Empty;

        var queued = false;
        lock (_queueGate)
        {
            if (_busy)
            {
                while (_queued.Count >= MaxQueueDepth)
                {
                    _queued.Dequeue();
                }

                _queued.Enqueue(line);
                queued = true;
            }
        }

        if (queued)
        {
            _window.Hints.SetBusy(SpinnerFrame, QueueHint);
            return;
        }

        StartTurn(line);
    }

    public void Cancel() => _turn?.Cancel();

    public void Stop() => _completion.TrySetResult();

    /// <summary>Test seam for the bounded history, mirroring what <see cref="Submit"/> feeds.</summary>
    internal void RememberForTests(string line) => Remember(line);

    private void Remember(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (_history.Count == 0 || !string.Equals(_history[^1], line, StringComparison.Ordinal))
        {
            _history.Add(line);
            if (_history.Count > MaxHistory)
            {
                _history.RemoveAt(0);
            }
        }

        _historyIndex = _history.Count;
    }

    private bool HasQueued
    {
        get
        {
            lock (_queueGate)
            {
                return _queued.Count > 0;
            }
        }
    }

    private void StartTurn(string line)
    {
        _busy = true;
        _window.Transcript.Append(" ▌ " + line, TranscriptStyle.User);
        _turn = new CancellationTokenSource();
        var stream = new TranscriptStream(_window.Transcript);
        _ = RunTurnAsync(line, stream, _turn.Token);
    }

    private async Task RunTurnAsync(string line, TranscriptStream stream, CancellationToken ct)
    {
        try
        {
            _window.Hints.SetBusy(SpinnerFrame, HasQueued ? QueueHint : null);
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
            string? next;
            lock (_queueGate)
            {
                next = _queued.Count > 0 ? _queued.Dequeue() : null;
                if (next is null)
                {
                    _busy = false;
                }
            }

            if (next is not null)
            {
                StartTurn(next);
            }
            else
            {
                _window.Hints.SetIdle(_attachmentLabelProvider?.Invoke() ?? _window.Hints.Workspace);
            }
        }
    }

    private void OnComposerKey(object? sender, Terminal.Gui.Input.Key key)
    {
        if (TryHandleDropdownKey(key))
        {
            key.Handled = true;
            return;
        }

        if (key == Terminal.Gui.Input.Key.Enter)
        {
            Submit();
            key.Handled = true;
        }
        else if (key == Terminal.Gui.Input.Key.Esc && _busy)
        {
            Cancel();
            key.Handled = true;
        }
        else if (key == Terminal.Gui.Input.Key.CursorUp)
        {
            NavigateHistory(-1);
            key.Handled = true;
        }
        else if (key == Terminal.Gui.Input.Key.CursorDown)
        {
            NavigateHistory(1);
            key.Handled = true;
        }
        else if (key == Terminal.Gui.Input.Key.Tab)
        {
            CompleteCommand();
            key.Handled = true;
        }
        else if (key == Terminal.Gui.Input.Key.V.WithCtrl && _pasteHandler is not null)
        {
            Paste();
            key.Handled = true;
        }
        else if (key == Terminal.Gui.Input.Key.O.WithCtrl)
        {
            _window.Transcript.ToggleExpanded();
            key.Handled = true;
        }
    }

    /// <summary>
    /// Ctrl+V claims the key whenever a paste handler is wired: the handler reads the
    /// Windows clipboard itself (image or text), so the composer must not also paste.
    /// </summary>
    private void Paste()
    {
        var handler = _pasteHandler;
        if (handler is null)
        {
            return;
        }

        _ = RunHandlerAsync(handler);
    }

    /// <summary>Runs a fire-and-forget composer handler, landing failures on the transcript.</summary>
    private async Task RunHandlerAsync(Func<Task> handler)
    {
        try
        {
            await handler().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                _window.App?.Invoke(() => _window.Transcript.Append(" " + ex.Message, TranscriptStyle.Error));
            }
            catch (NotInitializedException)
            {
                // The session wound down before the failure could be reported.
            }
        }
    }

    /// <summary>
    /// While the slash dropdown is open it owns the navigation keys, but the composer keeps
    /// focus so typing still filters the list. Enter/Tab accept the selection into the
    /// composer; Escape dismisses the list without clearing what was typed.
    /// </summary>
    private bool TryHandleDropdownKey(Terminal.Gui.Input.Key key)
    {
        if (!_window.IsSlashDropdownOpen)
        {
            return false;
        }

        if (key == Terminal.Gui.Input.Key.CursorUp)
        {
            _window.MoveSlashDropdown(-1);
            return true;
        }

        if (key == Terminal.Gui.Input.Key.CursorDown)
        {
            _window.MoveSlashDropdown(1);
            return true;
        }

        if (key == Terminal.Gui.Input.Key.Enter || key == Terminal.Gui.Input.Key.Tab)
        {
            _window.AcceptSlashDropdown();
            return true;
        }

        if (key == Terminal.Gui.Input.Key.Esc)
        {
            _window.HideSlashDropdown();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Up walks towards older entries, Down back towards the in-progress draft; the
    /// draft typed before the first Up is restored when Down passes the newest entry.
    /// </summary>
    private void NavigateHistory(int delta)
    {
        if (_history.Count == 0)
        {
            return;
        }

        if (delta < 0)
        {
            if (_historyIndex <= 0)
            {
                return;
            }

            if (_historyIndex == _history.Count)
            {
                _draft = _window.Composer.Input.Text?.ToString() ?? string.Empty;
            }

            _historyIndex--;
            _window.Composer.Input.Text = _history[_historyIndex];
            return;
        }

        if (_historyIndex >= _history.Count)
        {
            return;
        }

        _historyIndex++;
        _window.Composer.Input.Text = _historyIndex == _history.Count ? _draft : _history[_historyIndex];
    }

    /// <summary>Tab completes a unique known command through the classic matcher.</summary>
    private void CompleteCommand()
    {
        var current = _window.Composer.Input.Text?.ToString() ?? string.Empty;
        var completion = InteractiveLineReader.AutoComplete(
            current,
            InteractiveLineReader.KnownCommands.Select(command => command.Command));
        if (completion is null)
        {
            return;
        }

        _window.Composer.Input.Text = completion + " ";
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
