namespace RuAgent.Cli.Chat;

public partial class ChatSessionEngine
{
    private Func<string?, CancellationToken, Task>? _taskCommandHandler;
    private Func<string?, CancellationToken, Task>? _changesCommandHandler;
    private Func<string?, CancellationToken, Task>? _rewindCommandHandler;
    private CancellationTokenSource? _taskControlCancellation;
    private TaskCompletionSource? _taskControlDone;
    private bool _historyControlActive;
    private Func<bool>? _historyBranchPending;
    private Func<string, bool>? _historyRecoveryCommand;

    internal void SetHistoryStateProviders(Func<bool> pending, Func<string, bool> recoveryCommand)
    { _historyBranchPending = pending; _historyRecoveryCommand = recoveryCommand; }

    internal void FinishHistoryReload()
    {
        lock (_queueGate)
        {
            // Reload authorizes no queued replay. Only an empty queue can leave recovery.
            if (_queued.Count == 0) _queuePaused = false;
        }
        Dispatch(RefreshRunStatus);
    }

    internal void SetTaskCommandHandler(Func<string?, CancellationToken, Task> handler) => _taskCommandHandler = handler;
    internal void SetChangesCommandHandler(Func<string?, CancellationToken, Task> handler) => _changesCommandHandler = handler;
    internal void SetRewindCommandHandler(Func<string?, CancellationToken, Task> handler) => _rewindCommandHandler = handler;

    private bool TrySubmitTaskControl(string line)
    {
        if (_taskCommandHandler is null && _changesCommandHandler is null && _rewindCommandHandler is null) return false;
        var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var isTasks = parts.Length > 0 && parts[0].Equals("/tasks", StringComparison.OrdinalIgnoreCase);
        var isChanges = parts.Length > 0 && parts[0].Equals("/changes", StringComparison.OrdinalIgnoreCase);
        var isRewind = parts.Length > 0 && (parts[0].Equals("/rewind", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("/history", StringComparison.OrdinalIgnoreCase));
        var handler = isTasks ? _taskCommandHandler : isChanges ? _changesCommandHandler : isRewind ? _rewindCommandHandler : null;
        TaskCompletionSource done;
        CancellationTokenSource cancellation;
        lock (_queueGate)
        {
            if (_stopped) return true;
            if (isRewind && (_busy || _queuePaused || _queued.Count > 0))
            {
                _view.ShowFeedback("finish pending/queued work before rewinding history", true);
                return true;
            }
            if (_taskControlDone is { Task.IsCompleted: false })
            {
                _view.ShowFeedback("finish the current inspection first · esc closes the panel", true);
                return true;
            }
            if (handler is null) return false;
            _taskControlDone = done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _taskControlCancellation = cancellation = CancellationTokenSource.CreateLinkedTokenSource(_outerToken);
            _historyControlActive = isRewind;
        }
        // No submission snapshot/admission: pending attachments and the model turn are untouched.
        _view.InputText = "";
        Remember(line);
        _ = RunTaskControlAsync(handler, parts.Length == 2 ? parts[1].Trim() : null, cancellation, done);
        return true;
    }

    private async Task RunTaskControlAsync(Func<string?, CancellationToken, Task> handler, string? argument, CancellationTokenSource cancellation, TaskCompletionSource done)
    {
        try { await handler(argument, cancellation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Dispatch(() => _view.ShowFeedback("inspection_error: " + CliErrorText.ShortMessage(ex.Message), true));
        }
        finally
        {
            lock (_queueGate)
            {
                if (ReferenceEquals(_taskControlCancellation, cancellation)) _taskControlCancellation = null;
                _historyControlActive = false;
            }
            cancellation.Dispose();
            done.TrySetResult();
        }
    }
}
