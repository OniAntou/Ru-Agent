namespace RuBackend.Harness.Workspace;

internal sealed class WorkspaceMembershipHeartbeat : IAsyncDisposable
{
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(50);
    private readonly Func<CancellationToken, Task<bool>> _renewAsync;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _lifetime;
    private readonly TaskCompletionSource _lost = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _runner = Task.CompletedTask;
    private int _disposed;

    private WorkspaceMembershipHeartbeat(
        Func<CancellationToken, Task<bool>> renewAsync,
        TimeSpan leaseDuration,
        CancellationToken ownerToken)
    {
        _renewAsync = renewAsync;
        _interval = leaseDuration / 3 > MinimumInterval
            ? leaseDuration / 3
            : MinimumInterval;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ownerToken);
    }

    public Task Lost => _lost.Task;

    public static WorkspaceMembershipHeartbeat Start(
        Func<CancellationToken, Task<bool>> renewAsync,
        TimeSpan leaseDuration,
        CancellationToken ownerToken)
    {
        ArgumentNullException.ThrowIfNull(renewAsync);
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        var heartbeat = new WorkspaceMembershipHeartbeat(
            renewAsync,
            leaseDuration,
            ownerToken);
        heartbeat._runner = heartbeat.RunAsync();
        return heartbeat;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await _runner.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _lifetime.Dispose();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                if (!await _renewAsync(_lifetime.Token).ConfigureAwait(false))
                {
                    _lost.TrySetResult();
                    return;
                }

                await Task.Delay(_interval, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            _lost.TrySetResult();
        }
    }
}
