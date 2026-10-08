namespace RuAgent.Protocol;

public sealed class ThreadOwnedMutex : IDisposable, IAsyncDisposable
{
    private readonly ManualResetEventSlim _release = new();
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _isDisposed;

    public bool IsAcquired { get; private set; }
    public bool IsAccessDenied { get; private set; }

    public static ThreadOwnedMutex Acquire(string name, Func<string, Mutex>? openMutex = null) =>
        AcquireAsync(name, openMutex).GetAwaiter().GetResult();

    public static async Task<ThreadOwnedMutex> AcquireAsync(string name, Func<string, Mutex>? openMutex = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var lease = new ThreadOwnedMutex();
        var factory = openMutex ?? (static mutexName => new Mutex(initiallyOwned: false, mutexName));
        var thread = new Thread(() => lease.OwnMutex(name, factory)) { IsBackground = true };
        try
        {
            thread.Start();
        }
        catch
        {
            lease._release.Dispose();
            throw;
        }
        try
        {
            lease.IsAcquired = await lease._ready.Task.ConfigureAwait(false);
            return lease;
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        var shouldDispose = Interlocked.Exchange(ref _isDisposed, 1) == 0;
        if (shouldDispose && !_closed.Task.IsCompleted) _release.Set();
        await _closed.Task.ConfigureAwait(false);
        if (shouldDispose) _release.Dispose();
    }

    private void OwnMutex(string name, Func<string, Mutex> factory)
    {
        try
        {
            using var mutex = factory(name);
            var isOwned = false;
            try
            {
                try { isOwned = mutex.WaitOne(TimeSpan.Zero); }
                catch (AbandonedMutexException) { isOwned = true; }
                _ready.TrySetResult(isOwned);
                if (isOwned) _release.Wait();
            }
            finally
            {
                if (isOwned) mutex.ReleaseMutex();
            }
        }
        catch (UnauthorizedAccessException)
        {
            IsAccessDenied = true;
            _ready.TrySetResult(false);
        }
        catch (Exception error)
        {
            _ready.TrySetException(error);
        }
        finally
        {
            _closed.TrySetResult();
        }
    }
}
