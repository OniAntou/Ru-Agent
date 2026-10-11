namespace RuApp.Services;

/// <summary>Checks ownership when a queued UI update executes, rather than when it is queued.</summary>
internal static class OwnedUiUpdate
{
    internal static Task<bool> ApplyAsync(Func<Action, bool>? enqueue, Func<bool> stillOwns, Action apply)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Execute()
        {
            try
            {
                if (!stillOwns()) { completion.TrySetResult(false); return; }
                apply();
                completion.TrySetResult(true);
            }
            catch (Exception error) { completion.TrySetException(error); }
        }
        if (enqueue is null) Execute();
        else if (!enqueue(Execute)) completion.TrySetResult(false);
        return completion.Task;
    }
}
