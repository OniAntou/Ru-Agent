using RuAgent.Cli.Tui;

namespace RuAgent.Cli.Chat;

/// <summary>
/// The sole conversation controller: submission admission, queueing, history, turns,
/// cancellation and inspection lifetime. Presentation and input are adapters.
/// </summary>
public partial class ChatSessionEngine
{
    /// <summary>Lines kept for Up/Down navigation; older entries fall off the front.</summary>
    public const int MaxHistory = 100;

    /// <summary>Prompts typed while a turn streams wait here; a full queue refuses the new prompt.</summary>
    public const int MaxQueueDepth = 8;

    private const string QueueFullHint = "queue full · enter retries";
    private const string SubmissionTooLargeHint = "submission too large · shorten or detach files";
    private const string SubmissionUnavailableHint = "cannot queue this command with pending attachments";
    private const string StateChangeBusyHint = "finish queued prompts before changing session or model";
    private const long MaxQueuedSubmissionBytes = 32L * 1024 * 1024;
    private const int MaxSubmissionPromptChars = 16_000;

    /// <summary>How long <see cref="Stop"/> lets an in-flight turn finish before releasing UI waiters.</summary>
    private static readonly TimeSpan StopDrainTimeout = TimeSpan.FromSeconds(2);

    private readonly IChatSessionView _view;
    private readonly List<string> _history = [];
    private readonly Queue<ChatSubmissionSnapshot> _queued = new();
    private readonly object _queueGate = new();
    private readonly object _historyGate = new();
    private readonly CancellationToken _outerToken;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Func<string, CancellationToken, Task> _promptHandler =
        (_, _) => Task.CompletedTask;
    private Func<ChatSubmissionSnapshot, CancellationToken, Task> _submissionHandler =
        (submission, ct) => Task.CompletedTask;
    private Func<string, ChatSubmissionSnapshot?>? _snapshotFactory;
    private Func<string, bool>? _stateChangingCommandClassifier;
    private Action<ChatSubmissionSnapshot>? _submissionAccepted;
    private Action<ChatSubmissionSnapshot>? _submissionDiscarded;
    private Func<Task>? _shutdownSettlement;
    private long _queuedSubmissionBytes;
    private Func<string>? _attachmentLabelProvider;
    private Func<int>? _attachmentCountProvider;
    private Func<int>? _imageCountProvider;
    private Func<int>? _retryImageCountProvider;
    private Func<Task>? _pasteHandler;
    private Func<string, Task<bool>>? _copyHandler;
    private Func<string, bool>? _dropHandler;
    private CancellationTokenSource? _turn;
    private TaskCompletionSource? _turnDone;
    private int _historyIndex;
    private string _draft = string.Empty;
    private int _draftCursor;
    private volatile bool _busy;
    private volatile bool _localCommandActive;
    private volatile bool _stopped;
    private bool _queuePaused;
    private Func<string, bool>? _sessionCommand;
    internal bool IsBusy => _busy;
    internal bool HasCancelableWork
    {
        get { lock (_queueGate) return _busy || _taskControlCancellation is not null; }
    }
    internal bool CanCloseSession
    {
        get { lock (_queueGate) return !_busy && !_queuePaused && _queued.Count == 0 && _taskControlCancellation is null; }
    }
    internal void SetSessionCommandHandler(Func<string, bool> handler) => _sessionCommand = handler;

    /// <summary>
    /// A cancel that arrived while no turn source was live (between a settled
    /// turn and the next queued one): <see cref="StartTurn"/> consumes it so the
    /// Escape cannot be lost, and it is cleared whenever the loop goes idle.
    /// </summary>
    private bool _cancelRequested;
    private Func<string, string?>? _submissionGuard;
    internal void SetSubmissionGuard(Func<string, string?> guard) => _submissionGuard = guard;

    internal ChatSessionEngine(IChatSessionView view, CancellationToken cancellationToken = default)
    {
        _view = view;
        _outerToken = cancellationToken;
    }

    internal bool IsStopped => _stopped;

    internal async Task WaitForIdleAsync(CancellationToken ct)
    {
        while (true)
        {
            Task pending;
            lock (_queueGate)
            {
                pending = Task.WhenAll(_turnDone?.Task ?? Task.CompletedTask, _taskControlDone?.Task ?? Task.CompletedTask);
                if (pending.IsCompleted) return;
            }
            await pending.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    public Task Completion => _completion.Task;

    /// <summary>
    /// Adds session-owned work that must settle before its state can be disposed after
    /// the UI has stopped. This does not extend the UI completion bound.
    /// </summary>
    internal void SetShutdownSettlement(Func<Task> settlement) => _shutdownSettlement = settlement;

    internal Task WaitForShutdownSettlementAsync() => _shutdownSettlement?.Invoke() ?? Task.CompletedTask;

    /// <summary>
    /// A snapshot of the bounded prompt history (newest last),
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

    public void SetPromptHandler(Func<string, CancellationToken, Task> handler)
    {
        _promptHandler = handler;
        _submissionHandler = (submission, ct) => _promptHandler(submission.Prompt, ct);
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
        Dispatch(RefreshRunStatus);
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
        else Dispatch(RefreshRunStatus);
    }

    internal void SetSubmissionHandler(Func<ChatSubmissionSnapshot, CancellationToken, Task> handler) =>
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

    public void SetImageCountProvider(Func<int> provider, Func<int>? retryProvider = null)
    {
        _imageCountProvider = provider;
        _retryImageCountProvider = retryProvider;
    }

    /// <summary>Ctrl+V handler (clipboard image/text); wiring happens in the chat command, and the key stays claimed either way.</summary>
    public void SetPasteHandler(Func<Task> handler) => _pasteHandler = handler;
    public void SetCopyHandler(Func<string, Task<bool>> handler) => _copyHandler = handler;

    /// <summary>
    /// A dropped path in the composer text: returns true when the line was consumed as an
    /// attachment. The handler starts its own async attach; the loop just routes the line.
    /// </summary>
    public void SetDropHandler(Func<string, bool> handler) => _dropHandler = handler;

    /// <summary>Refreshes composer images even while busy; a busy turn keeps its spinner.</summary>
    public void RefreshAttachmentLabel() =>
        Dispatch(() =>
        {
            _view.SetImages(_imageCountProvider?.Invoke() ?? 0, _retryImageCountProvider?.Invoke() ?? 0);
            if (_attachmentLabelProvider is null || _busy)
            {
                return;
            }

            RefreshRunStatus();
        });

    public void Submit()
    {
        if (_stopped) return;
        var line = _view.InputText;
        // Navigation belongs to the session host, outside this session's turn and
        // attachment queue. Switching views must never cancel the active worker.
        if (_sessionCommand?.Invoke(line) == true) return;
        if (ChatCommandCatalog.IsRemovedCommand(line))
        {
            var command = line.TrimStart().Split(' ', 2)[0].ToLowerInvariant();
            _view.ShowFeedback($"unknown_command: {command}. Type /help for assistance.", true);
            return;
        }
        if (_submissionGuard?.Invoke(line) is { } guardMessage)
        {
            _view.ShowFeedback(guardMessage, true);
            return;
        }
        if (TrySubmitTaskControl(line)) return;
        lock (_queueGate)
        {
            if (_historyControlActive)
            {
                _view.ShowFeedback("finish choosing history before sending · esc closes the panel", true);
                return;
            }
            if (_busy && _historyBranchPending?.Invoke() is true)
            {
                _view.ShowFeedback("wait for chat branch creation before sending another request", true);
                return;
            }
        }
        if (_dropHandler is not null)
        {
            var candidate = line.Trim();
            if (candidate.Length > 0 && _dropHandler(candidate))
            {
                _view.InputText = string.Empty;
                return;
            }
        }

        var hasAttachments = (_attachmentCountProvider?.Invoke() ?? 0) > 0;
        if (string.IsNullOrWhiteSpace(line) && !hasAttachments)
        {
            TuiDebug.Log("submit", "ignored: empty prompt and no attachments");
            return;
        }

        var isRepairCommandWhilePaused = false;
        lock (_queueGate)
        {
            var historyRecovery = _historyRecoveryCommand?.Invoke(line) is true;
            if (historyRecovery && _queued.Count > 0)
            {
                _view.ShowFeedback("queued drafts are held · use /exit and reopen the session", true);
                return;
            }
            isRepairCommandWhilePaused = _queuePaused && !_busy && (ChatSubmissionSnapshot.IsPausedControlCommand(line) || historyRecovery);
            if ((_busy || _queued.Count > 0 || _queuePaused)
                && !isRepairCommandWhilePaused
                && _stateChangingCommandClassifier?.Invoke(line) is true)
            {
                _view.ShowFeedback(StateChangeBusyHint, true);
                return;
            }
        }

        var submission = _snapshotFactory?.Invoke(line);
        if (_snapshotFactory is not null && submission is null)
        {
            // The factory refuses for two different reasons: a command carrying pending
            // attachments, and a composed prompt over the 16k budget. Reporting the
            // attachment wording for an oversized prompt sent the operator looking in the
            // wrong place.
            var isCommandLine = line.TrimStart().StartsWith('/');
            _view.ShowFeedback(isCommandLine ? SubmissionUnavailableHint : SubmissionTooLargeHint, true);
            return;
        }

        submission ??= ChatSubmissionSnapshot.Create(line, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, [], []);
        if (submission.Prompt.Length > MaxSubmissionPromptChars
            || submission.OwnedBytes > MaxQueuedSubmissionBytes)
        {
            TuiDebug.Log("submit", "refused: submission exceeds the bounded prompt or attachment cap");
            _view.ShowFeedback(SubmissionTooLargeHint, true);
            return;
        }

        var queued = false;
        var refused = false;
        lock (_queueGate)
        {
            // Stop may arrive while snapshot validation runs. Until admission the
            // draft and attachments still belong to their existing owner.
            if (_stopped) return;
            var mustQueue = _busy || _queuePaused || _queued.Count > 0;
            if (mustQueue && !isRepairCommandWhilePaused)
            {
                if (_queued.Count >= MaxQueueDepth)
                {
                    // Refuse the new prompt instead of discarding accepted work; the
                    // input adapter retains the draft for an explicit retry.
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
            _view.ShowFeedback(QueueFullHint, true);
            return;
        }

        _view.InputText = string.Empty;
        _submissionAccepted?.Invoke(submission);
        Remember(line);
        _historyIndex = _history.Count;
        _draft = string.Empty;

        if (queued)
        {
            TuiDebug.Log("submit", $"queued ({_queued.Count})");
            _view.SetQueueCount(QueuedCount);
            _view.ShowFeedback("prompt queued");
            return;
        }

        TuiDebug.Log("submit", $"start turn len={line.Length} attachments={hasAttachments}");
        StartTurn(submission);
    }

    public void Cancel()
    {
        // The turn's CTS is disposed under the same gate once its handler settles, so a
        // late cancellation can never hit a disposed source.
        lock (_queueGate)
        {
            if (_taskControlCancellation is { } taskControl)
            {
                taskControl.Cancel();
                return;
            }
            if (_turn is { } turn)
            {
                turn.Cancel();
                return;
            }

            if (_busy)
            {
                // Between a settled turn and the next queued one the source is
                // momentarily null; remembering the request keeps the Escape
                // from being swallowed, and StartTurn applies it immediately.
                _cancelRequested = true;
            }
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
        var discardedSubmissions = new List<ChatSubmissionSnapshot>();
        lock (_queueGate)
        {
            _stopped = true;
            // A wind-down must cancel the in-flight turn: the runtime keeps
            // executing the run server-side until the run itself is cancelled,
            // and the caller exits the process right after this returns.
            _turn?.Cancel();
            _taskControlCancellation?.Cancel();
            while (_queued.TryDequeue(out var submission))
            {
                _queuedSubmissionBytes -= submission.OwnedBytes;
                discardedSubmissions.Add(submission);
            }

            turnDone = Task.WhenAll(_turnDone?.Task ?? Task.CompletedTask, _taskControlDone?.Task ?? Task.CompletedTask);
        }

        // Release runs temp-file I/O; doing it under the lock blocked the UI thread's key
        // handling (Stop is called from the window's IsRunningChanged handler) on a slow or
        // network filesystem.
        foreach (var discardedSubmission in discardedSubmissions)
        {
            _submissionDiscarded?.Invoke(discardedSubmission);
        }

        var discarded = discardedSubmissions.Count;

        if (discarded > 0)
        {
            var message = discarded == 1
                ? " discarded 1 queued prompt on exit"
                : $" discarded {discarded} queued prompts on exit";
            Dispatch(() => _view.Append(message, ChatMessageKind.Meta));
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

    private void StartTurn(ChatSubmissionSnapshot submission)
    {
        TaskCompletionSource turnDone;
        CancellationToken turnToken;
        var stoppedBeforeStart = false;
        lock (_queueGate)
        {
            stoppedBeforeStart = _stopped;
            if (stoppedBeforeStart)
            {
                _busy = false;
                _turnDone = null;
            }

            turnDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!stoppedBeforeStart)
            {
                _busy = true;
                var command = submission.Prompt.TrimStart().Split(' ', 2)[0];
                _localCommandActive = command.Equals("/backend", StringComparison.OrdinalIgnoreCase) || command.Equals("/key", StringComparison.OrdinalIgnoreCase)
                    || !command.Equals("/follow", StringComparison.OrdinalIgnoreCase) && ChatCommandCatalog.KnownCommands.Any(item => item.Command.Equals(command, StringComparison.OrdinalIgnoreCase));
                // Link the session's token: a shutdown outside the input adapter
                // must stop the in-flight turn too, not only the local Escape path.
                _turn = CancellationTokenSource.CreateLinkedTokenSource(_outerToken);
                if (_cancelRequested)
                {
                    // An Escape that arrived between turns cancels this one now
                    // instead of being lost.
                    _cancelRequested = false;
                    _turn.Cancel();
                }

                turnToken = _turn.Token;
                _turnDone = turnDone;
            }
            else
            {
                turnToken = CancellationToken.None;
            }
        }

        if (stoppedBeforeStart)
        {
            // Release does temp-file I/O; it must not run under the queue lock.
            _submissionDiscarded?.Invoke(submission);
            return;
        }

        Dispatch(() => _view.EchoPrompt(submission.Prompt, _localCommandActive));
        _ = RunTurnAsync(submission, turnToken, turnDone);
    }

    private async Task RunTurnAsync(
        ChatSubmissionSnapshot submission,
        CancellationToken ct,
        TaskCompletionSource turnDone)
    {
        string? failure = null;
        try
        {
            if (!_localCommandActive) Dispatch(() => _view.SetRunning(QueuedCount));
            await _submissionHandler(submission, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _submissionDiscarded?.Invoke(submission);
            Dispatch(() => _view.Append(" esc interrupted", ChatMessageKind.Meta));
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            _submissionDiscarded?.Invoke(submission);
            Dispatch(() => _view.Append(" " + ex.Message, ChatMessageKind.Error));
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
                    // Nothing is left to cancel between turns: a latch kept past
                    // the idle transition would cancel an unrelated later turn.
                    _cancelRequested = false;
                }
            }

            if (next is not null)
            {
                StartTurn(next);
            }
            else
            {
                Dispatch(() =>
                {
                    // The turn is over: the live activity row must not outlive it.
                    _view.ClearLive();
                    RefreshRunStatus();
                    if (failure is not null) _view.ShowFeedback(failure, true);
                });
            }

            turnDone.TrySetResult();
        }
    }

    private int QueuedCount
    {
        get { lock (_queueGate) return _queued.Count; }
    }

    private void RefreshRunStatus()
    {
        bool paused;
        int queued;
        lock (_queueGate) { paused = _queuePaused; queued = _queued.Count; }
        if (_busy && !_localCommandActive)
            _view.SetQueueCount(queued);
        else if (paused)
            _view.SetPaused(queued);
        else
            _view.SetIdle(_attachmentLabelProvider?.Invoke() ?? _view.Workspace);
    }

    /// <summary>
    /// Releases the completion once the captured turn settles; Stop is called from the UI
    /// adapter and from the turn itself (/exit), so it never blocks the caller. A
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
    /// Lets the presentation adapter marshal notifications to its owning thread.
    /// </summary>
    private void Dispatch(Action action) => _view.Dispatch(action);

    /// <summary>
    /// Ctrl+V claims the key whenever a paste handler is wired: the handler reads the
    /// Windows clipboard itself (image or text), so the composer must not also paste.
    /// An unwired handler still claims the key but tells the user why nothing pasted.
    /// </summary>
    private protected void Paste()
    {
        var handler = _pasteHandler;
        if (handler is null)
        {
            TuiDebug.Log("paste", "ctrl+v reached the loop but no paste handler is wired");
            _view.ShowFeedback("paste unavailable", true);
            return;
        }

        TuiDebug.Log("paste", "ctrl+v routed to the paste handler");
        _ = RunHandlerAsync(handler);
    }

    private protected void Copy()
    {
        var text = _view.CopyText();
        var handler = _copyHandler;
        if (handler is null || string.IsNullOrEmpty(text))
        {
            _view.ShowFeedback("nothing to copy");
            return;
        }
        _ = RunHandlerAsync(async () =>
        {
            var copied = await handler(text).ConfigureAwait(false);
            Dispatch(() => _view.ShowFeedback(copied ? "copied to clipboard" : "clipboard unavailable", !copied));
        });
    }

    /// <summary>Runs a fire-and-forget composer handler, landing failures on the transcript.</summary>
    private async Task RunHandlerAsync(Func<Task> handler)
    {
        try { await handler().ConfigureAwait(false); }
        catch (Exception ex) { Dispatch(() => _view.Append(" " + ex.Message, ChatMessageKind.Error)); }
    }

    /// <summary>
    /// Up walks towards older entries, Down back towards the in-progress draft; the
    /// draft typed before the first Up is restored when Down passes the newest entry.
    /// </summary>
    private protected void NavigateHistory(int delta)
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
                _draft = _view.InputText;
                _draftCursor = _view.InsertionPoint;
            }

            _historyIndex--;
            _view.InputText = _history[_historyIndex];
            _view.InsertionPoint = _view.InputText.Length;
            return;
        }

        if (_historyIndex >= _history.Count)
        {
            return;
        }

        _historyIndex++;
        _view.InputText = _historyIndex == _history.Count ? _draft : _history[_historyIndex];
        _view.InsertionPoint = _historyIndex == _history.Count ? _draftCursor : _view.InputText.Length;
    }

    /// <summary>Tab completes a unique known command through the shared matcher.</summary>
    private protected void CompleteCommand()
    {
        var current = _view.InputText;
        var completion = ChatCommandCatalog.AutoComplete(
            current,
            ChatCommandCatalog.KnownCommands.Select(command => command.Command));
        if (completion is null)
        {
            return;
        }

        _view.InputText = completion + " ";
        _view.InsertionPoint = _view.InputText.Length;
    }
}
