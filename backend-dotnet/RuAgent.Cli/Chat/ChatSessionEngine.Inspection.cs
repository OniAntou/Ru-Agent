namespace RuAgent.Cli.Chat;

public partial class ChatSessionEngine
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
                _view.ShowFeedback("finish the current inspection first · esc closes the panel", true);
                return true;
            }
            if (!isTasks) return false;
            _taskControlDone = done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _taskControlCancellation = cancellation = CancellationTokenSource.CreateLinkedTokenSource(_outerToken);
        }
        // No submission snapshot/admission: pending attachments and the model turn are untouched.
        _view.InputText = "";
        Remember(line);
        _ = RunTaskControlAsync(_taskCommandHandler, parts.Length == 2 ? parts[1].Trim() : null, cancellation, done);
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
            }
            cancellation.Dispose();
            done.TrySetResult();
        }
    }
}
