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
    private readonly ProviderCatalogService _service;
    private readonly IProviderCatalogClock? _clock;
    private readonly Dictionary<string, ProviderCatalogController> _children = new(StringComparer.Ordinal);
    private readonly HashSet<string> _modelsDevAliases = new(StringComparer.Ordinal);
    private IReadOnlyList<ProviderGroupDefinition> _definitions = ProviderGroupDefinitions.All;
    private GroupedProviderCatalogState _state = GroupedProviderCatalogState.Empty;
    private Task<bool>? _flight;
    private bool _pollingActive;
    private bool _disposed;

    public GroupedProviderCatalogController(
        ProviderCatalogService service,
        IProviderCatalogClock? clock = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _clock = clock;
        foreach (var definition in ProviderGroupDefinitions.All)
        {
            var child = new ProviderCatalogController(service, clock: clock);
            child.SetBackend(definition.Alias);
            child.StateChanged += OnChildStateChanged;
            _children[definition.Alias] = child;
        }
    }

    public IReadOnlyList<ProviderGroupDefinition> CurrentDefinitions
    {
        get { lock (_gate) return _definitions; }
    }

    /// <summary>
    /// Replaces the dynamic Models.dev children: providers that disappear lose
    /// their child (and its state), new ones get a child. The fixed five stay.
    /// </summary>
    public async Task SetModelsDevProvidersAsync(IReadOnlyList<ModelsDevEntry> entries)
    {
        List<ProviderCatalogController> removed = [];
        lock (_gate)
        {
            if (_disposed) return;
            var definitions = ProviderGroupDefinitions.WithModelsDev(entries);
            var wanted = definitions.Select(definition => definition.Alias).ToHashSet(StringComparer.Ordinal);
            foreach (var alias in _children.Keys.Where(alias => !wanted.Contains(alias)).ToArray())
            {
                if (_children.Remove(alias, out var child))
                {
                    removed.Add(child);
                }
            }

            foreach (var definition in definitions)
            {
                if (!_children.ContainsKey(definition.Alias))
                {
                    var child = new ProviderCatalogController(_service, clock: _clock);
                    child.SetBackend(definition.Alias);
                    child.StateChanged += OnChildStateChanged;
                    _children[definition.Alias] = child;
                }
            }

            _modelsDevAliases.Clear();
            foreach (var definition in definitions)
            {
                if (!ProviderGroupDefinitions.ByAlias.ContainsKey(definition.Alias))
                {
                    _modelsDevAliases.Add(definition.Alias);
                }
            }

            _definitions = definitions;
        }

        foreach (var child in removed)
        {
            await child.DisposeAsync().ConfigureAwait(false);
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
                (!ProviderGroupDefinitions.ByAlias.ContainsKey(current.Backend) && !_modelsDevAliases.Contains(current.Backend)))
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
