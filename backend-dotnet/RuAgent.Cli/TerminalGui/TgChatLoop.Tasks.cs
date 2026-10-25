namespace RuAgent.Cli.TerminalGui;

public sealed partial class TgChatLoop
{
    private Func<string?, CancellationToken, Task>? _taskCommandHandler;
    private CancellationTokenSource? _taskControlCancellation;
    private TaskCompletionSource? _taskControlDone;

    internal void SetTaskCommandHandler(Func<string?, CancellationToken, Task> handler) => _taskCommandHandler = handler;

    private bool TrySubmitTaskControl(string line)
    {
        if (_taskCommandHandler is null) return false;
        var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var isTasks = parts.Length > 0 && parts[0].Equals("/tasks", StringComparison.OrdinalIgnoreCase);
        TaskCompletionSource done;
        CancellationTokenSource cancellation;
        lock (_queueGate)
        {
            if (_stopped) return true;
            if (_taskControlDone is { Task.IsCompleted: false })
            {
                _window.Hints.ShowFeedback("finish background task control first · esc closes the panel", true);
                return true;
            }
            if (!isTasks) return false;
            _taskControlDone = done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _taskControlCancellation = cancellation = CancellationTokenSource.CreateLinkedTokenSource(_outerToken);
        }
        // No submission snapshot/admission: pending attachments and the model turn are untouched.
        _window.Composer.Input.Text = "";
        Remember(line);
        _ = RunTaskControlAsync(parts.Length == 2 ? parts[1].Trim() : null, cancellation, done);
        return true;
    }

    private async Task RunTaskControlAsync(string? argument, CancellationTokenSource cancellation, TaskCompletionSource done)
    {
        try { await _taskCommandHandler!(argument, cancellation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Dispatch(() => _window.Hints.ShowFeedback("tasks_error: " + CliErrorText.ShortMessage(ex.Message), true));
        }
        finally
        {
            lock (_queueGate)
            {
                if (ReferenceEquals(_taskControlCancellation, cancellation)) _taskControlCancellation = null;
            }
            cancellation.Dispose();
            done.TrySetResult();
        }
    }
}
