using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuApp.Services;

public sealed class RefreshController<T> : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<T>> _fetch;
    private readonly Action<T> _apply;
    private readonly Action<Action> _dispatch;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _admission = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task<T>? _inFlight;
    private long _generation;
    private bool _active;
    private bool _disposed;

    public RefreshController(
        Func<CancellationToken, Task<T>> fetch,
        Action<T> apply,
        Action<Action> dispatch)
    {
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
    }

    public void SetActive(bool active)
    {
        CancellationTokenSource? toCancel = null;
        lock (_gate)
        {
            if (_disposed) return;
            if (_active == active) return;
            _active = active;
            if (!active)
            {
                _generation++;
                toCancel = _cts;
                _cts = null;
            }
        }
        toCancel?.Cancel();
        toCancel?.Dispose();
    }

    public async Task<bool> RefreshAsync()
    {
        CancellationToken token;
        long capturedGeneration;
        Task<T> fetchTask;

        lock (_gate)
        {
            if (_disposed || !_active) return false;
            if (_inFlight is not null) return false;
            // Try to acquire admission without blocking; if busy, return false
            if (!_admission.Wait(0)) return false;
            capturedGeneration = _generation;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            token = _cts.Token;
            _inFlight = _fetch(token);
            fetchTask = _inFlight;
        }

        T result;
        try
        {
            result = await fetchTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            lock (_gate) { _inFlight = null; }
            _admission.Release();
            return false;
        }
        catch (Exception)
        {
            lock (_gate) { _inFlight = null; }
            _admission.Release();
            throw;
        }

        // If we got here, fetch succeeded. Need to apply if still active and generation matches.
        var shouldApply = false;
        lock (_gate)
        {
            if (!_disposed && _active && _generation == capturedGeneration && !token.IsCancellationRequested)
            {
                shouldApply = true;
            }
            _inFlight = null;
        }

        if (shouldApply)
        {
            // Apply inside dispatched callback, checking again inside dispatch
            _dispatch(() =>
            {
                lock (_gate)
                {
                    if (_disposed || !_active || _generation != capturedGeneration || token.IsCancellationRequested)
                    {
                        return;
                    }
                    _apply(result);
                }
            });
            _admission.Release();
            return true;
        }
        else
        {
            _admission.Release();
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? toCancel;
        Task<T>? inFlight;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _active = false;
            _generation++;
            toCancel = _cts;
            _cts = null;
            inFlight = _inFlight;
        }
        toCancel?.Cancel();
        if (inFlight is not null)
        {
            try { await inFlight.ConfigureAwait(false); } catch { }
        }
        toCancel?.Dispose();
        // Wait for admission to be free (if someone is in RefreshAsync's critical section)
        // Not strictly needed, but ensure no outstanding apply
        await _admission.WaitAsync().ConfigureAwait(false);
        _admission.Release();
        _admission.Dispose();
    }
}
