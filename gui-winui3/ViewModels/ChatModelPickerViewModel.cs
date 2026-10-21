using CommunityToolkit.Mvvm.ComponentModel;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.ViewModels;

/// <summary>The Host calls the composer's model and reasoning controls make.</summary>
internal interface IChatModelBackend
{
    Task<AppSettingsDto?> GetSettingsAsync();

    Task<IReadOnlyList<ModelsDevEntryDto>> GetModelsDevAsync();

    Task<bool> SaveProviderModelSelectionAsync(string backend, string model);

    Task<bool> SaveReasoningSettingsAsync(string effort, string model, string identity);
}

/// <summary>The per-provider catalogs behind the grouped model picker.</summary>
internal interface IGroupedCatalogSession
{
    event EventHandler<GroupedProviderCatalogState>? StateChanged;

    GroupedProviderCatalogState State { get; }

    IReadOnlyList<ProviderGroupDefinition> CurrentDefinitions { get; }

    void SetSelection(string backend, string model);

    void SetSearchText(string searchText);

    Task SetModelsDevProvidersAsync(IReadOnlyList<ModelsDevEntry> entries);

    Task<bool> RefreshAsync(bool force = false);
}

internal sealed class ApiChatModelBackend(ApiService api) : IChatModelBackend
{
    public Task<AppSettingsDto?> GetSettingsAsync() => api.GetSettingsAsync();

    public Task<IReadOnlyList<ModelsDevEntryDto>> GetModelsDevAsync() => api.GetModelsDevAsync();

    public Task<bool> SaveProviderModelSelectionAsync(string backend, string model) =>
        api.SaveProviderModelSelectionAsync(backend, model);

    public Task<bool> SaveReasoningSettingsAsync(string effort, string model, string identity) =>
        api.SaveReasoningSettingsAsync(effort, model, identity);
}

internal sealed class GroupedCatalogSession(GroupedProviderCatalogController controller) : IGroupedCatalogSession
{
    public event EventHandler<GroupedProviderCatalogState>? StateChanged
    {
        add => controller.StateChanged += value;
        remove => controller.StateChanged -= value;
    }

    public GroupedProviderCatalogState State => controller.State;

    public IReadOnlyList<ProviderGroupDefinition> CurrentDefinitions => controller.CurrentDefinitions;

    public void SetSelection(string backend, string model) => controller.SetSelection(backend, model);

    public void SetSearchText(string searchText) => controller.SetSearchText(searchText);

    public Task SetModelsDevProvidersAsync(IReadOnlyList<ModelsDevEntry> entries) =>
        controller.SetModelsDevProvidersAsync(entries);

    public Task<bool> RefreshAsync(bool force = false) => controller.RefreshAsync(force);
}

/// <summary>
/// The composer's provider/model choice and reasoning effort: what is selected, what
/// the trigger buttons say, which efforts the selected model offers, and the saves
/// behind a change. A choice is persisted before it is published, and a superseded
/// save never overwrites a newer one.
/// </summary>
/// <remarks>
/// Call on the UI thread; catalog publishes arrive on any thread and are marshalled
/// through the dispatch delegate. The grouped catalog publishes once per provider, so
/// bursts are coalesced and the picker tree is only rebuilt while it is open.
/// </remarks>
public sealed partial class ChatModelPickerViewModel : ObservableObject, IDisposable
{
    private readonly Action<Action> _dispatch;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ChatModelSelectionPresenter _selection = new();
    private readonly ReasoningSelectionSaveQueue _reasoningSaves = new();

    private IChatModelBackend? _backend;
    private IProviderCatalogSession? _catalog;
    private IGroupedCatalogSession? _grouped;
    private CancellationTokenSource? _searchDelay;
    private CancellationTokenSource? _publishDelay;
    private CancellationTokenSource? _saveFailureDelay;
    private GroupedProviderCatalogState? _pendingGroupedState;
    private bool _isPickerDirty = true;
    private bool _disposed;

    internal ChatModelPickerViewModel(Action<Action> dispatch, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Raised with a fresh picker tree while the picker is open.</summary>
    internal event Action<ProviderModelPickerProjection>? PickerProjectionReady;

    /// <summary>Raised when the selection or its catalog changed, so the context meter can repaint.</summary>
    internal event Action? SelectionChanged;

    [ObservableProperty] public partial string ModelLabel { get; private set; } = ProviderModelPickerProjection.TriggerDisplay(string.Empty, string.Empty);

    [ObservableProperty] public partial string EffortLabel { get; private set; } = "default";

    [ObservableProperty] public partial string EffortAutomationName { get; private set; } = "Reasoning effort: default";

    /// <summary>A short-lived notice after a model save failed; empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModelSaveFailure))]
    public partial string ModelSaveFailure { get; private set; } = string.Empty;

    public bool HasModelSaveFailure => ModelSaveFailure.Length > 0;

    [ObservableProperty] public partial bool IsPickerOpen { get; set; }

    internal string Backend { get; private set; } = string.Empty;

    internal string Model { get; private set; } = string.Empty;

    /// <summary>The effort the composer shows and sends.</summary>
    internal string SelectedEffort { get; private set; } = "default";

    /// <summary>
    /// The last settings read, kept so a failed send can name the provider missing a
    /// key without asking the Host again.
    /// </summary>
    internal AppSettingsDto? CurrentSettings { get; private set; }

    /// <summary>
    /// The catalog for the selected backend, or null when none has arrived. Null rather
    /// than an empty catalog: a usage panel priced against nothing would call every run free.
    /// </summary>
    internal ProviderCatalogState? SelectedCatalog => TryGetSelectedCatalog(out var catalog) ? catalog : null;

    private string EffectiveBackend => !string.IsNullOrWhiteSpace(_grouped?.State.SelectedBackend)
        ? _grouped!.State.SelectedBackend
        : Backend;

    private string EffectiveModel => !string.IsNullOrWhiteSpace(_grouped?.State.SelectedModel)
        ? _grouped!.State.SelectedModel
        : Model;

    /// <summary>Connects to a Host's settings and catalogs, releasing a previous connection first.</summary>
    internal void Attach(IChatModelBackend backend, IProviderCatalogSession catalog, IGroupedCatalogSession grouped)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Detach();
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _grouped = grouped ?? throw new ArgumentNullException(nameof(grouped));
        _catalog.StateChanged += OnCatalogStateChanged;
        _grouped.StateChanged += OnGroupedStateChanged;
    }

    /// <summary>Reads the stored selection and refreshes the catalogs it depends on.</summary>
    internal async Task LoadAsync()
    {
        var backend = _backend;
        if (backend is null) return;
        try
        {
            var settings = await backend.GetSettingsAsync();
            if (settings is null || !ReferenceEquals(backend, _backend)) return;
            CurrentSettings = settings;
            _selection.Adopt(settings);
            Backend = _selection.Backend;
            Model = _selection.Model;
            _grouped?.SetSelection(Backend, Model);
            await RefreshModelsDevGroupsAsync();
            ModelLabel = ProviderModelPickerProjection.TriggerDisplay(Backend, Model);
            if (_catalog is { } catalog)
            {
                catalog.SetBackend(Backend);
                await catalog.RefreshAsync();
            }

            if (_grouped is { } grouped) await grouped.RefreshAsync(force: false);
            RefreshEffort();
        }
        catch (Exception ex)
        {
            GuiDiagnosticWriter.AppendTraceLine($"[ChatModelPicker] Error loading models: {ex.Message}");
        }
    }

    /// <summary>Pulls the Models.dev provider list so the picker grows a group per generic provider.</summary>
    private async Task RefreshModelsDevGroupsAsync()
    {
        var backend = _backend;
        var grouped = _grouped;
        if (backend is null || grouped is null) return;
        try
        {
            var providers = await backend.GetModelsDevAsync();
            await grouped.SetModelsDevProvidersAsync(providers.Select(entry => new ModelsDevEntry(entry.Id, entry.Name)).ToArray());
        }
        catch (Exception ex)
        {
            GuiDiagnosticWriter.AppendTraceLine($"[ChatModelPicker] Error loading Models.dev providers: {ex.Message}");
        }
    }

    /// <summary>
    /// The picker opened: paint a tree that background publishes marked stale, then
    /// refresh due catalogs only. A forced refresh fanned out one request per provider.
    /// </summary>
    internal void OnPickerOpened()
    {
        IsPickerOpen = true;
        if (_isPickerDirty && _grouped is not null) PublishPicker(_grouped.State);
        _ = RefreshGroupedAsync();
    }

    internal void OnPickerClosed() => IsPickerOpen = false;

    private async Task RefreshGroupedAsync()
    {
        if (_grouped is null) return;
        await RefreshModelsDevGroupsAsync();
        if (_grouped is { } grouped) await grouped.RefreshAsync(force: false);
    }

    /// <summary>Coalesces a burst of typing into one catalog filter.</summary>
    internal void SetSearchText(string searchText)
    {
        Restart(ref _searchDelay, UiTiming.ModelSearchDebounce, () =>
        {
            _grouped?.SetSearchText(searchText);
            return Task.CompletedTask;
        });
    }

    private void OnGroupedStateChanged(object? sender, GroupedProviderCatalogState state) => _dispatch(() =>
    {
        if (_disposed) return;
        // One publish per provider: apply only the newest state after the burst settles.
        _pendingGroupedState = state;
        Restart(ref _publishDelay, UiTiming.GroupedPublishCoalesce, () =>
        {
            var pending = _pendingGroupedState;
            _pendingGroupedState = null;
            if (pending is not null && _grouped is not null && ReferenceEquals(_grouped.State, pending))
            {
                ApplyGroupedState(pending);
            }

            return Task.CompletedTask;
        });
    });

    internal void ApplyGroupedState(GroupedProviderCatalogState state)
    {
        if (!string.IsNullOrWhiteSpace(state.SelectedModel)) Model = state.SelectedModel;
        if (!string.IsNullOrWhiteSpace(state.SelectedBackend)) Backend = state.SelectedBackend;
        _selection.Adopt(Backend, Model);
        ModelLabel = ProviderModelPickerProjection.TriggerDisplay(state.SelectedBackend, state.SelectedModel);
        // The tree costs a full rebuild per publish; nobody sees it while closed.
        if (IsPickerOpen) PublishPicker(state);
        else _isPickerDirty = true;
        SelectionChanged?.Invoke();
        RefreshEffort();
    }

    private void PublishPicker(GroupedProviderCatalogState state)
    {
        PickerProjectionReady?.Invoke(ProviderModelPickerProjection.Create(
            state.Catalogs.Values, state.SelectedBackend, state.SelectedModel, state.SearchText, _grouped?.CurrentDefinitions));
        _isPickerDirty = false;
    }

    /// <summary>
    /// The single catalog for the selected backend landed: the stored effort and its
    /// levels may have arrived. Publishes for another backend are ignored, because the
    /// shared controller is repointed by Settings too.
    /// </summary>
    private void OnCatalogStateChanged(object? sender, ProviderCatalogState state)
    {
        _dispatch(() =>
        {
            if (!_disposed && string.Equals(state.Backend, EffectiveBackend, StringComparison.Ordinal)) RefreshEffort();
        });
    }

    /// <summary>Persists a model choice, then publishes it. A superseded save changes nothing.</summary>
    internal async Task SelectModelAsync(string model, string backend)
    {
        try
        {
            if (_backend is not { } api)
            {
                ShowModelSaveFailure();
                return;
            }

            var result = await _selection.SelectAsync(model, backend, api.SaveProviderModelSelectionAsync);
            if (result == ModelSelectionSaveResult.Superseded || _disposed) return;
            if (result != ModelSelectionSaveResult.Saved)
            {
                ShowModelSaveFailure();
                return;
            }

            Model = _selection.Model;
            Backend = _selection.Backend;
            // A stale effort must not stick when the new model has other levels; the
            // stored one arrives with the catalog refresh below.
            SelectedEffort = "default";
            _grouped?.SetSelection(backend, model);
            ModelLabel = ProviderModelPickerProjection.TriggerDisplay(backend, model);
            SelectionChanged?.Invoke();
            if (_catalog is { } catalog)
            {
                catalog.SetBackend(backend);
                _ = catalog.RefreshAsync();
            }

            _ = _grouped?.RefreshAsync(force: false);
            RefreshEffort();
        }
        catch (Exception ex)
        {
            GuiDiagnosticWriter.AppendTraceLine($"[ChatModelPicker] Error saving model: {ex.Message}");
            ShowModelSaveFailure();
        }
    }

    private void ShowModelSaveFailure()
    {
        ModelSaveFailure = "Could not save selected model.";
        Restart(ref _saveFailureDelay, UiTiming.ModelSaveFailure, () =>
        {
            ModelSaveFailure = string.Empty;
            return Task.CompletedTask;
        });
    }

    /// <summary>The efforts the selected model offers; just "default" until its catalog arrives.</summary>
    internal IReadOnlyList<ProviderReasoningOption> ReasoningOptions()
    {
        SyncEffortFromCatalog();
        return TryGetSelectedCatalog(out var catalog) && catalog is not null
            ? ProviderReasoningOptions.For(catalog, EffectiveModel)
            : [new ProviderReasoningOption("default", "default")];
    }

    /// <summary>Re-derives the shown effort from the catalog, clamping it to the levels on offer.</summary>
    internal void RefreshEffort()
    {
        var options = ReasoningOptions();
        // Until the catalog arrives nothing can be concluded from "default" alone.
        SetEffort(ProviderReasoningOptions.Clamp(SelectedEffort, options, TryGetSelectedCatalog(out _)));
    }

    private void SyncEffortFromCatalog()
    {
        if (!TryGetSelectedCatalog(out var catalog) || catalog is null) return;
        _reasoningSaves.Synchronize($"{Backend}\n{Model}\n{catalog.Identity}", catalog.SelectedEffort ?? "default");
        if (_reasoningSaves.IsPending) return;
        if (!string.IsNullOrWhiteSpace(catalog.SelectedEffort)
            && string.Equals(SelectedEffort, "default", StringComparison.OrdinalIgnoreCase))
        {
            SelectedEffort = _reasoningSaves.ConfirmedEffort;
        }
    }

    /// <summary>
    /// Shows a chosen effort immediately and saves it bound to the catalog identity (a
    /// credential hash, never the backend name). A failed save falls back to the last
    /// confirmed effort; a save for a selection that changed meanwhile is dropped.
    /// </summary>
    internal async Task ApplyEffortAsync(string effort)
    {
        string previous = SelectedEffort;
        SetEffort(effort);
        var api = _backend;
        if (api is null) return;

        string backend = EffectiveBackend;
        string model = Model;
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(backend)) return;
        string identity = TryGetSelectedCatalog(out var catalog) && !string.IsNullOrWhiteSpace(catalog?.Identity)
            ? catalog!.Identity
            : backend;

        var settled = await _reasoningSaves.SaveAsync($"{backend}\n{model}\n{identity}", effort, previous,
            () => api.SaveReasoningSettingsAsync(effort, model, identity));
        if (settled is null || !ReferenceEquals(api, _backend) || backend != Backend || model != Model) return;
        SetEffort(settled, saveFailed: settled != effort);
    }

    private void SetEffort(string effort, bool saveFailed = false)
    {
        SelectedEffort = effort;
        EffortLabel = effort;
        EffortAutomationName = $"Reasoning effort: {effort}{(saveFailed ? " (Save failed)" : string.Empty)}";
    }

    /// <summary>The selected model's published context length, when its catalog lists one.</summary>
    internal int? ContextLength()
    {
        if (_grouped is null || string.IsNullOrWhiteSpace(Model)) return null;
        var state = _grouped.State;
        string backend = EffectiveBackend;
        if (string.IsNullOrWhiteSpace(backend) || !state.Catalogs.TryGetValue(backend, out var catalog)) return null;
        string lookup = EffectiveModel;
        var match = catalog.Catalog.FirstOrDefault(entry => string.Equals(entry.Id, lookup, StringComparison.OrdinalIgnoreCase));
        return match?.ContextLength is int length && length > 0 ? length : null;
    }

    /// <summary>The model the composer reports, preferring the grouped catalog's view.</summary>
    internal string DisplayModel => EffectiveModel;

    /// <summary>
    /// The catalog for the selected backend. The single controller is preferred: the
    /// grouped one holds hundreds of children behind polling and is empty long after
    /// startup, while the single refresh carries the effort, identity and levels.
    /// </summary>
    private bool TryGetSelectedCatalog(out ProviderCatalogState? catalog) =>
        ProviderSelectedCatalog.TryResolve(_catalog?.State, _grouped?.State.Catalogs, EffectiveBackend, out catalog);

    private void Restart(ref CancellationTokenSource? slot, TimeSpan delay, Func<Task> action)
    {
        var previous = slot;
        previous?.Cancel();
        previous?.Dispose();
        var cancellation = new CancellationTokenSource();
        slot = cancellation;
        _ = RunAsync();

        async Task RunAsync()
        {
            try
            {
                await _delay(delay, cancellation.Token);
                if (!cancellation.IsCancellationRequested && !_disposed) await action();
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                GuiDiagnosticWriter.AppendTraceLine($"[ChatModelPicker] Deferred update failed: {ex.Message}");
            }
        }
    }

    private static void Cancel(ref CancellationTokenSource? slot)
    {
        var current = slot;
        slot = null;
        current?.Cancel();
        current?.Dispose();
    }

    private void Detach()
    {
        _selection.Invalidate();
        _reasoningSaves.Invalidate();
        Cancel(ref _searchDelay);
        Cancel(ref _publishDelay);
        _pendingGroupedState = null;
        if (_catalog is not null) _catalog.StateChanged -= OnCatalogStateChanged;
        if (_grouped is not null) _grouped.StateChanged -= OnGroupedStateChanged;
        _catalog = null;
        _grouped = null;
        _backend = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Detach();
        Cancel(ref _saveFailureDelay);
    }
}
