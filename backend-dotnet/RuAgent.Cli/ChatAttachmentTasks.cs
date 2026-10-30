namespace RuAgent.Cli;

/// <summary>Registers attachment work before it starts and drains it before session disposal.</summary>
internal sealed class ChatAttachmentTasks
{
    private readonly object _gate = new();
    private readonly HashSet<Task> _pending = [];
    private bool _stopped;

    internal bool HasPending { get { lock (_gate) return _pending.Count > 0; } }
    internal bool IsStopped { get { lock (_gate) return _stopped; } }

    internal Task RunAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_stopped) return Task.CompletedTask;
            _pending.Add(completion.Task);
        }
        return RunOwnedAsync(action, completion);
    }

    internal Task StopAsync()
    {
        lock (_gate)
        {
            _stopped = true;
            return Task.WhenAll(_pending);
        }
    }

    private async Task RunOwnedAsync(Func<Task> action, TaskCompletionSource completion)
    {
        try { await action().ConfigureAwait(false); }
        finally
        {
            lock (_gate)
            {
                _pending.Remove(completion.Task);
                completion.TrySetResult();
            }
        }
    }
}
