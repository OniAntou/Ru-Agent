using System.Diagnostics;

namespace RuAgent.Protocol;

public sealed class ThreadOwnedMutex : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan SynchronousDisposeTimeout = TimeSpan.FromSeconds(5);

    private readonly ManualResetEventSlim _release = new();
    private readonly ManualResetEventSlim _readySignal = new();
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _releaseGate = new();
    private Exception? _acquisitionError;
    private Exception? _releaseError;
    private bool _releaseSignaled;
    private bool _resourcesDisposed;

    public bool IsAcquired { get; private set; }
    public bool IsAccessDenied { get; private set; }

    public static ThreadOwnedMutex Acquire(string name, Func<string, Mutex>? openMutex = null)
    {
        var lease = Start(name, openMutex);
        lease._readySignal.Wait();
        try
        {
            lease.ThrowIfAcquisitionFailed();
            return lease;
        }
        catch
        {
            _ = lease._ready.Task.Exception;
            lease.Dispose();
            throw;
        }
    }

    public static async Task<ThreadOwnedMutex> AcquireAsync(string name, Func<string, Mutex>? openMutex = null)
    {
        var lease = Start(name, openMutex);
        try
        {
            await lease._ready.Task.ConfigureAwait(false);
            return lease;
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void Dispose()
    {
        SignalRelease();
        if (!_closed.Task.Wait(SynchronousDisposeTimeout))
        {
            _ = _closed.Task.ContinueWith(
                static (_, state) => ((ThreadOwnedMutex)state!).CompleteDeferredCleanup(),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            Trace.TraceError("Thread-owned mutex release did not finish within {0} seconds.", SynchronousDisposeTimeout.TotalSeconds);
            throw new TimeoutException("thread_owned_mutex_release_timeout");
        }

        DisposeResources();
        ThrowIfReleaseFailed();
    }

    public async ValueTask DisposeAsync()
    {
        SignalRelease();
        await _closed.Task.ConfigureAwait(false);
        DisposeResources();
        ThrowIfReleaseFailed();
    }

    private static ThreadOwnedMutex Start(string name, Func<string, Mutex>? openMutex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var lease = new ThreadOwnedMutex();
        var factory = openMutex ?? (static mutexName => new Mutex(initiallyOwned: false, mutexName));
        var thread = new Thread(() => lease.OwnMutex(name, factory)) { IsBackground = true };
        try
        {
            thread.Start();
            return lease;
        }
        catch
        {
            lease.DisposeResources();
            throw;
        }
    }

    private void SignalRelease()
    {
        lock (_releaseGate)
        {
            if (_releaseSignaled) return;
            _releaseSignaled = true;
            if (!_closed.Task.IsCompleted) _release.Set();
        }
    }

    private void ThrowIfAcquisitionFailed()
    {
        if (_acquisitionError is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_acquisitionError).Throw();
        }
    }

    private void DisposeResources()
    {
        lock (_releaseGate)
        {
            if (_resourcesDisposed) return;
            _resourcesDisposed = true;
            _release.Dispose();
            _readySignal.Dispose();
        }
    }

    private void CompleteDeferredCleanup()
    {
        DisposeResources();
        if (_releaseError is { } error)
        {
            Trace.TraceError("Thread-owned mutex release failed after its synchronous wait expired ({0}).",
                error.GetType().Name);
        }
    }

    private void ThrowIfReleaseFailed()
    {
        if (_releaseError is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_releaseError).Throw();
        }
    }

    private void OwnMutex(string name, Func<string, Mutex> factory)
    {
        try
        {
            Mutex mutex;
            try
            {
                mutex = factory(name);
            }
            catch (UnauthorizedAccessException)
            {
                SetAccessDenied();
                return;
            }

            using (mutex)
            {
                bool isOwned;
                try
                {
                    try { isOwned = mutex.WaitOne(TimeSpan.Zero); }
                    catch (AbandonedMutexException) { isOwned = true; }
                }
                catch (UnauthorizedAccessException)
                {
                    SetAccessDenied();
                    return;
                }

                IsAcquired = isOwned;
                _ready.TrySetResult(isOwned);
                _readySignal.Set();
                if (isOwned)
                {
                    try { _release.Wait(); }
                    finally { mutex.ReleaseMutex(); }
                }
            }
        }
        catch (Exception error)
        {
            if (_ready.TrySetException(error))
            {
                _acquisitionError = error;
                _readySignal.Set();
            }
            else
            {
                _releaseError = error;
            }
        }
        finally
        {
            _closed.TrySetResult();
        }
    }

    private void SetAccessDenied()
    {
        IsAccessDenied = true;
        _ready.TrySetResult(false);
        _readySignal.Set();
    }
}
