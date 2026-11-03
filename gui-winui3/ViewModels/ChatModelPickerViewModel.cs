using CommunityToolkit.Mvvm.ComponentModel;
using RuAgent.Protocol.Harness;
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

    void SynchronizeSelection(string backend, string model);

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

    public void SynchronizeSelection(string backend, string model) => controller.SynchronizeSelection(backend, model);

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
    private int _catalogRefreshes;
    private long _settingsReadGeneration;
    private long _selectionVersion;
    private long _appliedGroupedSelectionVersion;

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

    /// <summary>
    /// No model is chosen yet, typically a first run with no provider key. The empty
    /// chat offers the way to Models &amp; Providers only then; it is false until the
    /// stored selection has been read, so the hint never flashes on a configured app.
    /// </summary>
    [ObservableProperty] public partial bool NeedsProviderSetup { get; private set; }

    private bool _selectionRead;

    private void ShowSelection(string backend, string model)
    {
        ModelLabel = ProviderModelPickerProjection.TriggerDisplay(backend, model);
        NeedsProviderSetup = _selectionRead && string.IsNullOrWhiteSpace(EffectiveModel);
    }

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

    private string EffectiveBackend => Backend;

    private string EffectiveModel => Model;

    /// <summary>Connects to a Host's settings and catalogs, releasing a previous connection first.</summary>
    internal void Attach(IChatModelBackend backend, IProviderCatalogSession catalog, IGroupedCatalogSession grouped)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Detach();
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _grouped = grouped ?? throw new ArgumentNullException(nameof(grouped));
        _appliedGroupedSelectionVersion = grouped.State.SelectionVersion;
        _catalog.StateChanged += OnCatalogStateChanged;
        _grouped.StateChanged += OnGroupedStateChanged;
    }

    /// <summary>Reads the stored selection and refreshes the catalogs it depends on.</summary>
    internal async Task LoadAsync()
    {
        var backend = _backend;
        if (backend is null) return;
        long readGeneration = ++_settingsReadGeneration;
        long selectionVersion = _selectionVersion;
        var grouped = _grouped;
        long groupedSelectionVersion = grouped?.State.SelectionVersion ?? 0;
        _catalogRefreshes++;
        try
        {
            var settings = await backend.GetSettingsAsync();
            if (settings is null || !CanApplySettings(backend, grouped, readGeneration, selectionVersion, groupedSelectionVersion)) return;
            CurrentSettings = settings;
            _appliedGroupedSelectionVersion = groupedSelectionVersion;
            _selectionRead = true;
            _selection.Adopt(settings);
            Backend = _selection.Backend;
            Model = _selection.Model;
            _grouped?.SynchronizeSelection(Backend, Model);
            ShowSelection(Backend, Model);
            Task selectedRefresh = Task.CompletedTask;
            if (_catalog is { } catalog)
            {
                catalog.SetBackend(Backend);
                selectedRefresh = catalog.RefreshAsync();
            }

            // The selected provider is useful before the directory or other providers finish.
            await Task.WhenAll(selectedRefresh, RefreshModelsDevGroupsAsync());
            if (!CanApplySettings(backend, grouped, readGeneration, selectionVersion, groupedSelectionVersion)) return;

            if (_grouped is { } currentGrouped) await currentGrouped.RefreshAsync(force: false);
            RefreshEffort();
        }
        catch (Exception ex)
        {
            GuiDiagnosticWriter.AppendTraceLine($"[ChatModelPicker] Error loading models: {ex.Message}");
        }
        finally
        {
            _catalogRefreshes--;
            if (!_disposed && IsPickerOpen && _grouped is { } current) PublishPicker(current.State);
        }
    }

    private bool CanApplySettings(
        IChatModelBackend backend,
        IGroupedCatalogSession? grouped,
        long readGeneration,
        long selectionVersion,
        long groupedSelectionVersion) =>
        !_disposed
        && ReferenceEquals(backend, _backend)
        && ReferenceEquals(grouped, _grouped)
        && (grouped?.State.SelectionVersion ?? 0) == groupedSelectionVersion
        && readGeneration == _settingsReadGeneration
        && selectionVersion == _selectionVersion;

    /// <summary>Pulls the Models.dev provider list so the picker grows a group per generic provider.</summary>
    private async Task RefreshModelsDevGroupsAsync()
    {
        var backend = _backend;
        var grouped = _grouped;
        if (backend is null || grouped is null) return;
        try
        {
            var providers = await backend.GetModelsDevAsync();
            if (!ReferenceEquals(backend, _backend) || !ReferenceEquals(grouped, _grouped)) return;
            // Named custom providers (ADR 0034) come first, under the names the user gave them.
            var custom = CurrentSettings?.CustomProviders?
                .Select(provider => new ModelsDevEntry(CustomProviderPolicy.Backend(provider.Id), provider.DisplayName))
                ?? [];
            await grouped.SetModelsDevProvidersAsync(custom
                .Concat(providers.Select(entry => new ModelsDevEntry(entry.Id, entry.Name)))
                .ToArray());
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
        _isPickerDirty = true;
        _ = RefreshGroupedAsync();
        if (_isPickerDirty && _grouped is not null) PublishPicker(_grouped.State);
    }

    internal void OnPickerClosed() => IsPickerOpen = false;

    private async Task RefreshGroupedAsync()
    {
        var grouped = _grouped;
        if (grouped is null) return;
        _catalogRefreshes++;
        try
        {
            await RefreshModelsDevGroupsAsync();
            if (ReferenceEquals(grouped, _grouped)) await grouped.RefreshAsync(force: false);
        }
        catch (Exception ex)
        {
            GuiDiagnosticWriter.AppendTraceLine($"[ChatModelPicker] Catalog refresh failed: {ex.GetType().Name}");
        }
        finally
        {
            _catalogRefreshes--;
            if (!_disposed && IsPickerOpen && ReferenceEquals(grouped, _grouped)) PublishPicker(grouped.State);
        }
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

            // Reads begun before or during this save must not replace the saved choice.
            _selectionVersion++;
            var result = await _selection.SelectAsync(model, backend, api.SaveProviderModelSelectionAsync);
            if (result == ModelSelectionSaveResult.Superseded || _disposed) return;
            if (result != ModelSelectionSaveResult.Saved)
            {
                ShowModelSaveFailure();
                return;
            }

            Model = _selection.Model;
            Backend = _selection.Backend;
            _selectionRead = true;
            _selectionVersion++;
            _appliedGroupedSelectionVersion = _grouped?.State.SelectionVersion ?? _appliedGroupedSelectionVersion;
            // A stale effort must not stick when the new model has other levels; the
            // stored one arrives with the catalog refresh below.
            SelectedEffort = "default";
            _grouped?.SynchronizeSelection(backend, model);
            ShowSelection(backend, model);
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

    /// <summary>Captures the visible selection for a new run; empty/unloaded state fails closed.</summary>
    internal bool TryCaptureRunSelection(
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CreateRunProviderSelection? selection)
    {
        string backend = EffectiveBackend;
        string model = EffectiveModel;
        if (_disposed || _backend is null || !_selectionRead
            || string.IsNullOrWhiteSpace(backend) || string.IsNullOrWhiteSpace(model))
        {
            selection = null;
            return false;
        }

        selection = new CreateRunProviderSelection(backend, model);
        return true;
    }

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
        _settingsReadGeneration++;
        _selectionVersion++;
        _selectionRead = false;
        CurrentSettings = null;
        Backend = string.Empty;
        Model = string.Empty;
        _selection.Adopt(Backend, Model);
        ShowSelection(Backend, Model);
        _selection.Invalidate();
        _reasoningSaves.Invalidate();
        Cancel(ref _searchDelay);
        Cancel(ref _publishDelay);
        _pendingGroupedState = null;
        _isPickerDirty = true;
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
