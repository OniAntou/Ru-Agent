using RuApp.Models;

namespace RuApp.Services;

public sealed record GroupedProviderCatalogState
{
    public static GroupedProviderCatalogState Empty { get; } = new();

    public IReadOnlyDictionary<string, ProviderCatalogState> Catalogs { get; init; } =
        new Dictionary<string, ProviderCatalogState>(StringComparer.Ordinal);

    public string SelectedBackend { get; init; } = string.Empty;

    public string SelectedModel { get; init; } = string.Empty;

    public string SearchText { get; init; } = string.Empty;
}

public sealed class GroupedProviderCatalogController : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ProviderCatalogController> _children = new(StringComparer.Ordinal);
    private GroupedProviderCatalogState _state = GroupedProviderCatalogState.Empty;
    private Task<bool>? _flight;
    private bool _pollingActive;
    private bool _disposed;

    public GroupedProviderCatalogController(
        ProviderCatalogService service,
        IProviderCatalogClock? clock = null)
    {
        if (service is null) throw new ArgumentNullException(nameof(service));
        foreach (var definition in ProviderGroupDefinitions.All)
        {
            var child = new ProviderCatalogController(service, clock: clock);
            child.SetBackend(definition.Alias);
            child.StateChanged += OnChildStateChanged;
            _children[definition.Alias] = child;
        }
    }

    public GroupedProviderCatalogState State
    {
        get { lock (_gate) return _state; }
    }

    public event EventHandler<GroupedProviderCatalogState>? StateChanged;

    public void SetSelection(string backend, string model)
    {
        GroupedProviderCatalogState state;
        lock (_gate)
        {
            if (_disposed) return;
            _state = _state with
            {
                SelectedBackend = backend ?? string.Empty,
                SelectedModel = model ?? string.Empty,
            };
            state = _state;
        }

        Publish(state);
    }

    public void SetSearchText(string searchText)
    {
        GroupedProviderCatalogState state;
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
        ProviderCatalogController[] children;
        lock (_gate)
        {
            if (_disposed || _pollingActive == active) return;
            _pollingActive = active;
            children = [.. _children.Values];
        }

        foreach (var child in children)
        {
            child.SetPollingActive(active);
        }
    }

    public Task<bool> RefreshIfDueAsync()
    {
        lock (_gate)
        {
            if (!_pollingActive || _disposed) return Task.FromResult(false);
            if (_flight is not null) return _flight;
            return StartFlightLocked(force: false);
        }
    }

    public Task<bool> RefreshAsync(bool force = false)
    {
        lock (_gate)
        {
            if (_disposed) return Task.FromResult(false);
            if (_flight is not null) return _flight;
            return StartFlightLocked(force);
        }
    }

    public void Invalidate()
    {
        ProviderCatalogController[] children;
        lock (_gate)
        {
            if (_disposed) return;
            children = [.. _children.Values];
        }

        foreach (var child in children)
        {
            child.Invalidate();
        }
    }

    private Task<bool> StartFlightLocked(bool force)
    {
        var children = _children.Values.ToArray();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var childTasks = new List<Task<bool>>(children.Length);
        foreach (var child in children)
        {
            childTasks.Add(force ? child.RefreshAsync(force: true) : child.RefreshIfDueAsync());
        }

        _flight = completion.Task;
        _ = RunAsync(childTasks, completion);
        return completion.Task;
    }

    private async Task RunAsync(
        IReadOnlyList<Task<bool>> childTasks,
        TaskCompletionSource<bool> completion)
    {
        bool applied = false;
        try
        {
            var results = await Task.WhenAll(childTasks).ConfigureAwait(false);
            applied = results.Any(success => success);
        }
        catch (Exception ex)
        {
            // One child failure must not throw away another child result.
            System.Diagnostics.Debug.WriteLine($"[GroupedCatalog] Child refresh failed: {ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_flight, completion.Task)) _flight = null;
            }

            completion.TrySetResult(applied);
        }
    }

    private void OnChildStateChanged(object? sender, ProviderCatalogState state)
    {
        if (sender is not ProviderCatalogController child) return;
        GroupedProviderCatalogState? publish = null;
        lock (_gate)
        {
            if (_disposed) return;
            // Read the child's current state so a late event from a previous
            // grouped generation cannot resurrect stale data.
            var current = child.State;
            if (string.IsNullOrEmpty(current.Backend) ||
                !ProviderGroupDefinitions.ByAlias.ContainsKey(current.Backend))
            {
                return;
            }

            var catalogs = new Dictionary<string, ProviderCatalogState>(_state.Catalogs, StringComparer.Ordinal);
            catalogs[current.Backend] = current;
            _state = _state with { Catalogs = catalogs };
            publish = _state;
        }

        Publish(publish);
    }

    private void Publish(GroupedProviderCatalogState state) => StateChanged?.Invoke(this, state);

    public async ValueTask DisposeAsync()
    {
        ProviderCatalogController[] children;
        Task<bool>? flight;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pollingActive = false;
            children = [.. _children.Values];
            flight = _flight;
        }

        foreach (var child in children)
        {
            child.StateChanged -= OnChildStateChanged;
        }

        foreach (var child in children)
        {
            await child.DisposeAsync().ConfigureAwait(false);
        }

        if (flight is not null)
        {
            try { await flight.ConfigureAwait(false); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GroupedCatalog] Dispose wait failed: {ex.Message}");
            }
        }
    }
}
