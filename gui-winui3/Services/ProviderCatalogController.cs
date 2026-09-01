using RuApp.Models;

namespace RuApp.Services;

public interface IProviderCatalogClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class ProviderCatalogController : IAsyncDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ManualRefreshThrottle = TimeSpan.FromSeconds(30);

    private readonly ProviderCatalogService _service;
    private readonly Action<ProviderCatalogState>? _apply;
    private readonly IProviderCatalogClock _clock;
    private readonly object _gate = new();
    private CancellationTokenSource? _requestCts;
    private Task<bool>? _flight;
    private long _generation;
    private DateTimeOffset? _lastForcedRefreshUtc;
    private Task<bool>? _restartAfterCancellation;
    private string _backend = string.Empty;
    private bool _pollingActive;
    private bool _disposed;
    private ProviderCatalogState _state = ProviderCatalogState.Empty(string.Empty);

    public ProviderCatalogController(
        ProviderCatalogService service,
        Action<ProviderCatalogState>? apply = null,
        IProviderCatalogClock? clock = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _apply = apply;
        _clock = clock ?? SystemProviderCatalogClock.Instance;
    }

    public event EventHandler<ProviderCatalogState>? StateChanged;

    public ProviderCatalogState State
    {
        get { lock (_gate) return _state; }
    }

    public void SetBackend(string backend)
    {
        backend = string.IsNullOrWhiteSpace(backend) ? string.Empty : backend.Trim();
        CancellationTokenSource? toCancel;
        ProviderCatalogState state;
        lock (_gate)
        {
            if (_disposed || string.Equals(_backend, backend, StringComparison.Ordinal)) return;
            _backend = backend;
            _generation++;
            _lastForcedRefreshUtc = null;
            toCancel = _requestCts;
            _state = ProviderCatalogState.Empty(backend, _state.SearchText);
            state = _state;
        }
        Cancel(toCancel);
        Publish(state);
    }

    public void SetSearchText(string searchText)
    {
        ProviderCatalogState state;
        lock (_gate)
        {
            if (_disposed) return;
            _state = _state with { SearchText = searchText ?? string.Empty };
            state = _state;
        }
        Publish(state);
    }

    public void SetPollingActive(bool active)
    {
        CancellationTokenSource? toCancel = null;
        lock (_gate)
        {
            if (_disposed || _pollingActive == active) return;
            _pollingActive = active;
            if (!active)
            {
                _generation++;
                toCancel = _requestCts;
            }
        }
        Cancel(toCancel);
    }

    public Task<bool> RefreshIfDueAsync()
    {
        lock (_gate)
        {
            if (!_pollingActive || _disposed || string.IsNullOrEmpty(_backend))
                return Task.FromResult(false);

            if (_state.LastSuccessUtc.HasValue &&
                _clock.UtcNow - _state.LastSuccessUtc.Value < PollInterval)
                return Task.FromResult(false);
        }

        return RefreshAsync();
    }

    public Task<bool> RefreshAsync(bool force = false)
    {
        CancellationTokenSource requestCts;
        TaskCompletionSource<bool> completion;
        string backend;
        long generation;

        lock (_gate)
        {
            if (_disposed || string.IsNullOrEmpty(_backend)) return Task.FromResult(false);
            if (_flight is not null)
            {
                if (_requestCts?.IsCancellationRequested != true) return _flight;
                if (_restartAfterCancellation is not null) return _restartAfterCancellation;
                _restartAfterCancellation = RestartAfterCancellationAsync(_flight, force);
                return _restartAfterCancellation;
            }
            if (force && _lastForcedRefreshUtc.HasValue &&
                _clock.UtcNow - _lastForcedRefreshUtc.Value < ManualRefreshThrottle)
                return Task.FromResult(false);

            if (force) _lastForcedRefreshUtc = _clock.UtcNow;
            backend = _backend;
            generation = _generation;
            requestCts = new CancellationTokenSource();
            _requestCts = requestCts;
            completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _flight = completion.Task;
        }

        _ = RunAsync(backend, generation, force, requestCts, completion);
        return completion.Task;
    }

    private async Task<bool> RestartAfterCancellationAsync(Task<bool> previous, bool force)
    {
        try { await previous.ConfigureAwait(false); } catch { }
        lock (_gate)
        {
            _restartAfterCancellation = null;
        }
        return await RefreshAsync(force).ConfigureAwait(false);
    }

    public void Invalidate()
    {
        CancellationTokenSource? toCancel;
        ProviderCatalogState state;
        lock (_gate)
        {
            if (_disposed) return;
            _generation++;
            _lastForcedRefreshUtc = null;
            toCancel = _requestCts;
            _state = ProviderCatalogState.Empty(_backend, _state.SearchText);
            state = _state;
        }
        Cancel(toCancel);
        Publish(state);
    }

    private async Task RunAsync(
        string backend,
        long generation,
        bool force,
        CancellationTokenSource requestCts,
        TaskCompletionSource<bool> completion)
    {
        bool applied = false;
        try
        {
            ProviderCatalogSnapshot? snapshot;
            try
            {
                snapshot = await _service.GetAsync(backend, force, requestCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (requestCts.IsCancellationRequested)
            {
                return;
            }

            ProviderCatalogState? stateToPublish = null;
            lock (_gate)
            {
                if (!IsCurrentUnderLock(backend, generation, requestCts.Token)) return;

                if (snapshot is null)
                {
                    _state = _state with
                    {
                        Status = "error",
                        Stale = _state.Models.Count > 0,
                        ErrorCode = "catalog_unavailable"
                    };
                }
                else if (!snapshot.IsSuccess && string.IsNullOrWhiteSpace(snapshot.Backend))
                {
                    _state = _state with
                    {
                        Status = "error",
                        Stale = _state.Models.Count > 0,
                        ErrorCode = snapshot.ErrorCode ?? "catalog_unavailable",
                        LastSuccessUtc = snapshot.LastSuccessUtc ?? _state.LastSuccessUtc
                    };
                }
                else if (!string.Equals(snapshot.Backend, backend, StringComparison.Ordinal))
                {
                    return;
                }
                else if (!snapshot.IsSuccess &&
                         snapshot.Models.Count == 0 &&
                         snapshot.Identity.Length > 0 &&
                         string.Equals(snapshot.Identity, _state.Identity, StringComparison.Ordinal))
                {
                    _state = _state with
                    {
                        Status = snapshot.Status,
                        Stale = true,
                        ErrorCode = snapshot.ErrorCode,
                        LastSuccessUtc = snapshot.LastSuccessUtc ?? _state.LastSuccessUtc
                    };
                }
                else
                {
                    var nextState = ProviderCatalogState.FromSnapshot(snapshot, _state.SearchText);
                    if (string.IsNullOrWhiteSpace(nextState.SelectedModel) &&
                        !string.IsNullOrWhiteSpace(_state.SelectedModel))
                    {
                        nextState = nextState with { SelectedModel = _state.SelectedModel };
                    }
                    _state = nextState;
                }

                stateToPublish = _state;
                applied = true;
            }

            if (stateToPublish is not null) Publish(stateToPublish);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ProviderCatalogController] Refresh failed: {ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_flight, completion.Task)) _flight = null;
                if (ReferenceEquals(_requestCts, requestCts)) _requestCts = null;
            }
            requestCts.Dispose();
            completion.TrySetResult(applied);
        }
    }

    private bool IsCurrentUnderLock(string backend, long generation, CancellationToken token) =>
        !_disposed &&
        string.Equals(_backend, backend, StringComparison.Ordinal) &&
        _generation == generation &&
        !token.IsCancellationRequested;

    private void Publish(ProviderCatalogState state)
    {
        _apply?.Invoke(state);
        StateChanged?.Invoke(this, state);
    }

    private static void Cancel(CancellationTokenSource? cts)
    {
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        Task<bool>? flight;
        CancellationTokenSource? toCancel;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pollingActive = false;
            _generation++;
            flight = _flight;
            toCancel = _requestCts;
        }
        Cancel(toCancel);
        if (flight is not null)
        {
            try { await flight.ConfigureAwait(false); } catch { }
        }
    }

    private sealed class SystemProviderCatalogClock : IProviderCatalogClock
    {
        public static readonly SystemProviderCatalogClock Instance = new();
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
