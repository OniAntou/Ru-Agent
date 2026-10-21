using System;
using System.Threading;
using System.Threading.Tasks;

using RuApp.Services;

namespace RuApp.Services;

public sealed class RefreshController<T> : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<T>> _fetch;
    private readonly Action<T> _apply;
    private readonly Action<Action> _dispatch;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task<bool>? _operation;
    private PendingApply? _pendingApply;
    private long _generation;
    private bool _active;
    private bool _disposed;

    private sealed class PendingApply
    {
        private readonly object _stateGate = new();
        private bool _started;
        private bool _cancelled;

        public TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TryStart()
        {
            lock (_stateGate)
            {
                if (_started || _cancelled)
                {
                    return false;
                }

                _started = true;
                return true;
            }
        }

        public bool TryCancelIfQueued()
        {
            lock (_stateGate)
            {
                if (_started)
                {
                    return false;
                }

                _cancelled = true;
                return true;
            }
        }
    }

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
        PendingApply? pendingApply = null;
        lock (_gate)
        {
            if (_disposed || _active == active)
            {
                return;
            }

            _active = active;
            if (!active)
            {
                _generation++;
                toCancel = _cts;
                pendingApply = _pendingApply;
            }
        }

        try { toCancel?.Cancel(); } catch (ObjectDisposedException) { }
        if (pendingApply?.TryCancelIfQueued() == true)
        {
            pendingApply.Completion.TrySetResult(false);
        }
    }

    public Task<bool> RefreshAsync()
    {
        CancellationTokenSource requestCts;
        TaskCompletionSource<bool> operationSource;
        long capturedGeneration;

        lock (_gate)
        {
            if (_disposed || !_active || _operation is not null)
            {
                return Task.FromResult(false);
            }

            capturedGeneration = _generation;
            requestCts = new CancellationTokenSource();
            _cts = requestCts;
            operationSource = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _operation = operationSource.Task;
        }

        _ = RunRefreshAsync(requestCts, capturedGeneration, operationSource);
        return operationSource.Task;
    }

    /// <summary>
    /// Refreshes after a local mutation. An in-flight refresh may have read the state
    /// from before the mutation, so it is superseded: its result is discarded and a
    /// new fetch starts. Plain <see cref="RefreshAsync"/> would instead return false
    /// and leave the stale read to be applied.
    /// </summary>
    public Task<bool> RefreshLatestAsync()
    {
        CancellationTokenSource? toCancel;
        PendingApply? pendingApply;
        lock (_gate)
        {
            if (_disposed || !_active)
            {
                return Task.FromResult(false);
            }

            _generation++;
            toCancel = _cts;
            pendingApply = _pendingApply;
            // The superseded run still clears only its own operation and token.
            _operation = null;
            _cts = null;
            _pendingApply = null;
        }

        try { toCancel?.Cancel(); } catch (ObjectDisposedException) { }
        if (pendingApply?.TryCancelIfQueued() == true)
        {
            pendingApply.Completion.TrySetResult(false);
        }

        return RefreshAsync();
    }

    private async Task RunRefreshAsync(
        CancellationTokenSource requestCts,
        long capturedGeneration,
        TaskCompletionSource<bool> operationSource)
    {
        var requestToken = requestCts.Token;
        PendingApply? pendingApply = null;
        bool? result = null;
        Exception? failure = null;
        try
        {
            T fetched;
            try
            {
                fetched = await _fetch(requestToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            pendingApply = new PendingApply();
            lock (_gate)
            {
                if (!IsCurrentUnderLock(capturedGeneration, requestToken))
                {
                    return;
                }

                _pendingApply = pendingApply;
            }

            try
            {
                _dispatch(() => ApplyDispatchedResult(
                    fetched,
                    capturedGeneration,
                    requestToken,
                    pendingApply));
            }
            catch (Exception exception)
            {
                pendingApply.Completion.TrySetException(exception);
            }

            // Completion represents the actual UI application, not merely dispatch.
            result = await pendingApply.Completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = false;
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            lock (_gate)
            {
                // A superseding refresh may already own these slots.
                if (ReferenceEquals(_pendingApply, pendingApply))
                {
                    _pendingApply = null;
                }

                if (ReferenceEquals(_operation, operationSource.Task))
                {
                    _operation = null;
                }

                if (ReferenceEquals(_cts, requestCts))
                {
                    _cts = null;
                }
            }

            // The token was captured before this source is disposed. A deferred
            // callback can therefore still perform its generation/cancellation check.
            try { requestCts.Dispose(); } catch (ObjectDisposedException) { }
            if (failure is not null)
            {
                operationSource.TrySetException(failure);
            }
            else
            {
                operationSource.TrySetResult(result ?? false);
            }
        }
    }

    private void ApplyDispatchedResult(
        T fetched,
        long capturedGeneration,
        CancellationToken requestToken,
        PendingApply pendingApply)
    {
        if (!pendingApply.TryStart())
        {
            pendingApply.Completion.TrySetResult(false);
            return;
        }

        try
        {
            if (!IsCurrent(capturedGeneration, requestToken))
            {
                pendingApply.Completion.TrySetResult(false);
                return;
            }

            _apply(fetched);
            pendingApply.Completion.TrySetResult(true);
        }
        catch (Exception exception)
        {
            pendingApply.Completion.TrySetException(exception);
        }
    }

    private bool IsCurrent(long capturedGeneration, CancellationToken token)
    {
        lock (_gate)
        {
            return IsCurrentUnderLock(capturedGeneration, token);
        }
    }

    private bool IsCurrentUnderLock(long capturedGeneration, CancellationToken token)
    {
        return !_disposed
            && _active
            && _generation == capturedGeneration
            && !token.IsCancellationRequested;
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? toCancel;
        Task<bool>? operation;
        PendingApply? pendingApply;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _active = false;
            _generation++;
            toCancel = _cts;
            operation = _operation;
            pendingApply = _pendingApply;
        }

        try { toCancel?.Cancel(); } catch (ObjectDisposedException) { }
        if (pendingApply?.TryCancelIfQueued() == true)
        {
            pendingApply.Completion.TrySetResult(false);
        }

        if (operation is not null)
        {
            try { await operation.ConfigureAwait(false); }
            catch (Exception ex)
            {
                GuiDiagnosticWriter.AppendTraceLine($"[RefreshController] Cancel wait failed: {ex.Message}");
            }
        }

        // RunRefreshAsync owns disposal after its final operation completion. This
        // await keeps DisposeAsync from racing that cleanup or any dispatched work.
        if (operation is null)
        {
            try { toCancel?.Dispose(); } catch (ObjectDisposedException) { }
        }
    }
}
