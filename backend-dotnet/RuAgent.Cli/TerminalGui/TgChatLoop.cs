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

    /// <summary>Prompts typed while a turn streams wait here; a full queue refuses the new prompt.</summary>
    public const int MaxQueueDepth = 8;

    private const string SpinnerFrame = "⠋";
    private const string QueueHint = "enter queues · esc cancels";
    private const string QueueFullHint = "queue full · enter retries";
    private const string SubmissionTooLargeHint = "submission too large · shorten or detach files";
    private const string SubmissionUnavailableHint = "cannot queue this command with pending attachments";
    private const string StateChangeBusyHint = "finish queued prompts before changing session or model";
    private const long MaxQueuedSubmissionBytes = 32L * 1024 * 1024;
    private const int MaxSubmissionPromptChars = 16_000;

    /// <summary>How long <see cref="Stop"/> lets an in-flight turn finish before releasing waiters.</summary>
    private static readonly TimeSpan StopDrainTimeout = TimeSpan.FromSeconds(2);

    private readonly TgChatWindow _window;
    private readonly List<string> _history = [];
    private readonly Queue<ChatSubmissionSnapshot> _queued = new();
    private readonly object _queueGate = new();
    private readonly object _historyGate = new();
    private readonly CancellationToken _outerToken;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TgChatKeyboardBindings _keyboardBindings;
    private Func<string, TranscriptStream, CancellationToken, Task> _promptHandler =
        (_, _, _) => Task.CompletedTask;
    private Func<ChatSubmissionSnapshot, TranscriptStream, CancellationToken, Task> _submissionHandler =
        (submission, stream, ct) => Task.CompletedTask;
    private Func<string, ChatSubmissionSnapshot?>? _snapshotFactory;
    private Func<string, bool>? _stateChangingCommandClassifier;
    private Action<ChatSubmissionSnapshot>? _submissionAccepted;
    private Action<ChatSubmissionSnapshot>? _submissionDiscarded;
    private long _queuedSubmissionBytes;
    private Func<string>? _attachmentLabelProvider;
    private Func<int>? _attachmentCountProvider;
    private Func<Task>? _pasteHandler;
    private Func<string, bool>? _dropHandler;
    private CancellationTokenSource? _turn;
    private TaskCompletionSource? _turnDone;
    private int _historyIndex;
    private string _draft = string.Empty;
    private volatile bool _busy;
    private volatile bool _stopped;
    private bool _queuePaused;

    public TgChatLoop(TgChatWindow window, CancellationToken cancellationToken = default)
    {
        _window = window;
        _outerToken = cancellationToken;
        _keyboardBindings = new TgChatKeyboardBindings(
            window,
            () => _busy,
            Submit,
            Cancel,
            NavigateHistory,
            CompleteCommand,
            Paste);
        // Stop() is the explicit signal, but the window can also wind down on its own
        // (app stop/close); the runnable leaving its running state must release waiters.
        window.IsRunningChanged += (_, args) =>
        {
            if (!args.Value)
            {
                Stop();
            }
        };
    }

    public Task Completion => _completion.Task;

    /// <summary>
    /// A snapshot of the bounded prompt history (newest last, matching the classic reader),
    /// safe to read from a slash handler that may have resumed off the UI thread.
    /// </summary>
    public IReadOnlyList<string> History
    {
        get
        {
            lock (_historyGate)
            {
                return [.. _history];
            }
        }
    }

    public void SetPromptHandler(Func<string, TranscriptStream, CancellationToken, Task> handler)
    {
        _promptHandler = handler;
        _submissionHandler = (submission, stream, ct) => _promptHandler(submission.Prompt, stream, ct);
    }

    internal void SetSubmissionSnapshotFactory(Func<string, ChatSubmissionSnapshot?> factory) =>
        _snapshotFactory = factory;

    internal void SetStateChangingCommandClassifier(Func<string, bool> classifier) =>
        _stateChangingCommandClassifier = classifier;

    internal void PauseQueuedSubmissions()
    {
        lock (_queueGate)
        {
            _queuePaused = true;
        }
    }

    internal void ResumeQueuedSubmissions()
    {
        ChatSubmissionSnapshot? next = null;
        lock (_queueGate)
        {
            _queuePaused = false;
            if (!_busy && !_stopped && _queued.Count > 0)
            {
                next = _queued.Dequeue();
                _queuedSubmissionBytes -= next.OwnedBytes;
            }
        }

        if (next is not null)
        {
            StartTurn(next);
        }
    }

    internal void SetSubmissionHandler(Func<ChatSubmissionSnapshot, TranscriptStream, CancellationToken, Task> handler) =>
        _submissionHandler = handler;

    internal void SetSubmissionOwnershipHandlers(
        Action<ChatSubmissionSnapshot> accepted,
        Action<ChatSubmissionSnapshot> discarded)
    {
        _submissionAccepted = accepted;
        _submissionDiscarded = discarded;
    }

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
    public void RefreshAttachmentLabel() =>
        Dispatch(() =>
        {
            if (_attachmentLabelProvider is null || _busy)
            {
                return;
            }

            _window.Hints.SetIdle(_attachmentLabelProvider());
        });

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
            TuiDebug.Log("submit", "ignored: empty prompt and no attachments");
            return;
        }

        var isRetryWhilePaused = false;
        var isRepairCommandWhilePaused = false;
        lock (_queueGate)
        {
            isRetryWhilePaused = _queuePaused && !_busy && IsRetryCommand(line);
            isRepairCommandWhilePaused = _queuePaused && !_busy && ChatSubmissionSnapshot.IsPausedControlCommand(line);
            if ((_busy || _queued.Count > 0 || _queuePaused)
                && !isRetryWhilePaused
                && !isRepairCommandWhilePaused
                && _stateChangingCommandClassifier?.Invoke(line) is true)
            {
                _window.Hints.SetBusy(SpinnerFrame, StateChangeBusyHint);
                return;
            }
        }

        var submission = _snapshotFactory?.Invoke(line);
        if (_snapshotFactory is not null && submission is null)
        {
            _window.Hints.SetBusy(SpinnerFrame, SubmissionUnavailableHint);
            return;
        }

        submission ??= ChatSubmissionSnapshot.Create(line, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, [], []);
        if (submission.Prompt.Length > MaxSubmissionPromptChars
            || submission.OwnedBytes > MaxQueuedSubmissionBytes)
        {
            TuiDebug.Log("submit", "refused: submission exceeds the bounded prompt or attachment cap");
            _window.Hints.SetBusy(SpinnerFrame, SubmissionTooLargeHint);
            return;
        }

        var queued = false;
        var refused = false;
        lock (_queueGate)
        {
            var mustQueue = _busy || _queuePaused || _queued.Count > 0;
            if (mustQueue && !isRetryWhilePaused && !isRepairCommandWhilePaused)
            {
                if (_queued.Count >= MaxQueueDepth)
                {
                    // The classic type-ahead refuses the new prompt instead of discarding
                    // an already-queued one, so the composer keeps the line for a retry.
                    refused = true;
                }
                else
                {
                    if (_queuedSubmissionBytes + submission.OwnedBytes > MaxQueuedSubmissionBytes)
                    {
                        refused = true;
                    }
                    else
                    {
                        _queued.Enqueue(submission);
                        _queuedSubmissionBytes += submission.OwnedBytes;
                        queued = true;
                    }
                }
            }
        }

        if (refused)
        {
            TuiDebug.Log("submit", "refused: queue full");
            _window.Hints.SetBusy(SpinnerFrame, QueueFullHint);
            return;
        }

        _window.Composer.Input.Text = string.Empty;
        _submissionAccepted?.Invoke(submission);
        Remember(line);
        _historyIndex = _history.Count;
        _draft = string.Empty;

        if (queued)
        {
            TuiDebug.Log("submit", $"queued ({_queued.Count})");
            _window.Hints.SetBusy(SpinnerFrame, QueueHint);
            return;
        }

        TuiDebug.Log("submit", $"start turn len={line.Length} attachments={hasAttachments}");
        StartTurn(submission);
    }

    public void Cancel()
    {
        // The turn's CTS is disposed under the same gate once its handler settles, so a
        // late Escape (or the handover's Cancel) can never hit a disposed source.
        lock (_queueGate)
        {
            _turn?.Cancel();
        }
    }

    /// <summary>
    /// Terminal: stops the loop from starting any further turn, abandons the queue, and
    /// releases waiters only once the in-flight turn's teardown has finished (bounded), so
    /// the caller cannot dispose the app under a still-running cancellation continuation.
    /// </summary>
    public void Stop()
    {
        Task turnDone;
        lock (_queueGate)
        {
            _stopped = true;
            while (_queued.TryDequeue(out var submission))
            {
                _queuedSubmissionBytes -= submission.OwnedBytes;
                _submissionDiscarded?.Invoke(submission);
            }
            turnDone = _turnDone?.Task ?? Task.CompletedTask;
        }

        _ = ReleaseCompletionWhenIdleAsync(turnDone);
    }

    /// <summary>Test seam for the bounded history, mirroring what <see cref="Submit"/> feeds.</summary>
    internal void RememberForTests(string line) => Remember(line);

    private void Remember(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (_historyGate)
        {
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

    private static bool IsRetryCommand(string line) =>
        line.Trim().Equals("/retry", StringComparison.OrdinalIgnoreCase);

    private void StartTurn(ChatSubmissionSnapshot submission)
    {
        TaskCompletionSource turnDone;
        lock (_queueGate)
        {
            if (_stopped)
            {
                _busy = false;
                _turnDone = null;
                _submissionDiscarded?.Invoke(submission);
                return;
            }

            _busy = true;
            // Link the session's token: a shutdown outside the window (or a handover) must
            // stop the in-flight turn too, not only the local Escape path.
            _turn = CancellationTokenSource.CreateLinkedTokenSource(_outerToken);
            turnDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _turnDone = turnDone;
        }

        Dispatch(() => _window.Transcript.Append(" ▌ " + submission.Prompt, TranscriptStyle.User));
        var stream = new TranscriptStream(_window.Transcript, Dispatch);
        _ = RunTurnAsync(submission, stream, _turn.Token, turnDone);
    }

    private async Task RunTurnAsync(
        ChatSubmissionSnapshot submission,
        TranscriptStream stream,
        CancellationToken ct,
        TaskCompletionSource turnDone)
    {
        try
        {
            Dispatch(() => _window.Hints.SetBusy(SpinnerFrame, HasQueued ? QueueHint : null));
            await _submissionHandler(submission, stream, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _submissionDiscarded?.Invoke(submission);
            stream.Append(" esc interrupted", TranscriptStyle.Meta);
        }
        catch (Exception ex)
        {
            _submissionDiscarded?.Invoke(submission);
            stream.Append(" " + ex.Message, TranscriptStyle.Error);
        }
        finally
        {
            ChatSubmissionSnapshot? next;
            lock (_queueGate)
            {
                // The handler settled: the per-turn source (and its outer-token link) has no
                // further use. Disposing under the gate keeps Cancel from racing the dispose.
                _turn?.Dispose();
                _turn = null;
                next = _stopped || _queuePaused || _queued.Count == 0 ? null : _queued.Dequeue();
                if (next is not null)
                {
                    _queuedSubmissionBytes -= next.OwnedBytes;
                }
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
                Dispatch(() => _window.Hints.SetIdle(_attachmentLabelProvider?.Invoke() ?? _window.Hints.Workspace));
            }

            turnDone.TrySetResult();
        }
    }

    /// <summary>
    /// Releases the completion once the captured turn settles; Stop is called from the UI
    /// loop (handover) and from the turn itself (/exit), so it never blocks the caller. A
    /// turn that ignores cancellation cannot pin the session forever: the wait is bounded.
    /// </summary>
    private async Task ReleaseCompletionWhenIdleAsync(Task turnDone)
    {
        if (!turnDone.IsCompleted)
        {
            await Task.WhenAny(turnDone, Task.Delay(StopDrainTimeout)).ConfigureAwait(false);
        }

        _completion.TrySetResult();
    }

    /// <summary>
    /// Runs a view mutation on the Terminal.Gui loop: the turn resumes on thread-pool
    /// continuations after <c>ConfigureAwait(false)</c>, so transcript, hint and stream
    /// writes must be marshalled. A mutation that arrives after the session disposed the
    /// app is dropped instead of crashing the wind-down.
    /// </summary>
    private void Dispatch(Action action)
    {
        var app = _window.App;
        if (app is null)
        {
            action();
            return;
        }

        try
        {
            app.Invoke(action);
        }
        catch (NotInitializedException)
        {
            // The TG session wound down; late view mutations are dropped.
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

/// <summary>Thin append facade handed to the prompt handler; every write is marshalled.</summary>
public sealed class TranscriptStream(TgTranscriptView transcript, Action<Action> dispatch)
{
    public void Append(string text, TranscriptStyle style) => dispatch(() => transcript.Append(text, style));

    public void AppendChunk(string text) => dispatch(() => transcript.AppendChunk(text));
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
