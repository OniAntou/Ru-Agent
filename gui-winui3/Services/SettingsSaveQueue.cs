namespace RuApp.Services;

/// <summary>
/// Serializes settings saves, coalesces pending form snapshots to the newest one,
/// and completes every caller only after that newest snapshot has settled.
/// </summary>
internal sealed class SettingsSaveQueue
{
    private readonly object _gate = new();
    private readonly List<TaskCompletionSource<bool>> _pendingWaiters = new();
    private bool _saving;
    private Func<Task<bool>>? _pendingSave;

    internal bool HasPendingSave
    {
        get { lock (_gate) return _pendingSave is not null; }
    }

    internal async Task<bool> SaveAsync(Func<Task<bool>> save)
    {
        ArgumentNullException.ThrowIfNull(save);
        TaskCompletionSource<bool>? waiter = null;
        lock (_gate)
        {
            if (_saving)
            {
                _pendingSave = save;
                waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingWaiters.Add(waiter);
            }
            else
            {
                _saving = true;
            }
        }

        if (waiter is not null) return await waiter.Task.ConfigureAwait(true);

        bool ownerResult = false;
        Exception? ownerFailure = null;
        Func<Task<bool>>? next = save;
        bool isOwnerSave = true;
        while (next is not null)
        {
            Exception? failure = null;
            bool result;
            try
            {
                result = await next().ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                result = false;
                failure = exception;
            }

            if (isOwnerSave)
            {
                ownerResult = result;
                ownerFailure = failure;
                isOwnerSave = false;
            }

            List<TaskCompletionSource<bool>>? completedWaiters = null;
            lock (_gate)
            {
                next = _pendingSave;
                _pendingSave = null;
                if (next is null)
                {
                    _saving = false;
                    completedWaiters = new List<TaskCompletionSource<bool>>(_pendingWaiters);
                    _pendingWaiters.Clear();
                }
            }

            if (completedWaiters is not null)
            {
                foreach (var pending in completedWaiters)
                {
                    if (failure is null) pending.TrySetResult(result);
                    else pending.TrySetException(failure);
                }
            }
        }

        if (ownerFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ownerFailure).Throw();
        }

        return ownerResult;
    }
}
