using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.ViewModels;

/// <summary>
/// State and decisions of the settings editor: theme and preset choices, the provider
/// picker, provider credentials, autosave and the explicit model fetch. The view renders
/// these values and forwards input; it decides nothing.
/// </summary>
/// <remarks>
/// Call members on the UI thread. Catalog state changes arrive on any thread and are
/// marshalled through the dispatch delegate. Credentials pass through the key
/// properties only until they are saved; a validated or removed key is cleared here.
/// </remarks>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    internal static readonly TimeSpan AutoSaveDelay = TimeSpan.FromMilliseconds(800);
    internal static readonly TimeSpan KeyProbeDelay = TimeSpan.FromSeconds(1);

    private const string DefaultCustomUrl = "http://localhost:8000/v1";
    private const string CloudflareChoice = "models.dev:cloudflare-workers-ai";
    private const string NoProviderLabel = "No provider";

    private readonly Action<Action> _dispatch;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SettingsPresenter _presenter = new();
    private readonly ProviderKeyProbe _keyProbe = new();
    private readonly Dictionary<string, string> _modelsDevNames = new(StringComparer.Ordinal);

    private ISettingsBackend? _backend;
    private IProviderCatalogSession? _catalog;
    private AppSettingsDto? _lastSettings;
    private IReadOnlyList<string> _modelsDevKeyIds = [];
    private bool _modelsDevLoadFailed;
    private CancellationTokenSource? _autoSaveDelay;
    private CancellationTokenSource? _keyProbeDelay;
    private Task? _keyProbeOperation;
    private bool _disposed;

    internal SettingsViewModel(Action<Action> dispatch, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Raised when the user picks a theme; the shell applies it.</summary>
    public event Action<UiTheme>? ThemeChanged;

    /// <summary>Raised when the user picks a default permission preset.</summary>
    public event Action<AgentPermissionPreset>? PresetChanged;

    /// <summary>Raised when a provider row is committed, so the view can close the picker.</summary>
    public event Action? ProviderPickerCommitted;

    public ObservableCollection<ProviderRow> ProviderRows { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProviderManagementEnabled))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty] public partial string ThemeChoice { get; private set; } = "dark";

    [ObservableProperty] public partial string PresetChoice { get; private set; } = "standard";

    [ObservableProperty] public partial string ProviderChoice { get; private set; } = string.Empty;

    [ObservableProperty] public partial string ProviderButtonText { get; private set; } = "No provider";

    [ObservableProperty] public partial string ProviderFilter { get; set; } = string.Empty;

    [ObservableProperty] public partial string ProviderHeaderText { get; private set; } = "PROVIDERS";

    [ObservableProperty] public partial bool HasNoProviderMatches { get; private set; }

    [ObservableProperty] public partial string CustomUrl { get; set; } = string.Empty;

    [ObservableProperty] public partial string CustomApiKey { get; set; } = string.Empty;

    [ObservableProperty] public partial string CustomApiKeyPlaceholder { get; private set; } = "sk-...";

    [ObservableProperty] public partial string CloudflareAccountId { get; set; } = string.Empty;

    [ObservableProperty] public partial string ModelsDevApiKey { get; set; } = string.Empty;

    [ObservableProperty] public partial string ModelsDevKeyLabel { get; private set; } = "API key";

    [ObservableProperty] public partial string ModelsDevKeyStatus { get; private set; } = string.Empty;

    [ObservableProperty] public partial string ModelsDevKeyStatusTone { get; private set; } = ProviderConnectionTest.NeutralTone;

    [ObservableProperty] public partial bool IsCustomProviderVisible { get; private set; }

    [ObservableProperty] public partial bool IsModelsDevKeyVisible { get; private set; }

    [ObservableProperty] public partial bool IsCloudflareAccountVisible { get; private set; }

    [ObservableProperty] public partial bool IsModelsDevSignOutVisible { get; private set; }

    /// <summary>Models returned by the explicit fetch; read-only, selection stays in chat.</summary>
    public ObservableCollection<ProviderModelRow> ProviderModels { get; } = new();

    [ObservableProperty] public partial string ProviderModelsStatus { get; private set; } = "No models fetched yet.";

    [ObservableProperty] public partial string ProviderModelsStatusTone { get; private set; } = ProviderConnectionTest.NeutralTone;

    /// <summary>The catalog's raw code for the last fetch, kept out of the reading path.</summary>
    [ObservableProperty] public partial string? ProviderModelsErrorCode { get; private set; }

    [ObservableProperty] public partial bool IsProviderModelsFetchEnabled { get; private set; } = true;

    public bool HasProviderModels => ProviderModels.Count > 0;

    [ObservableProperty] public partial bool IsSignOutEnabled { get; private set; } = true;

    /// <summary>
    /// Connects the editor to a Host. A previous connection's catalog subscription and
    /// pending work are released first, so a replaced backend cannot publish into it.
    /// </summary>
    internal Task AttachAsync(ISettingsBackend backend, IProviderCatalogSession catalog)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Detach();
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _catalog.StateChanged += OnCatalogStateChanged;
        return LoadAsync();
    }

    internal void SetCurrentTheme(UiTheme theme) => ThemeChoice = ThemeTag(theme);

    internal void SetCurrentPreset(AgentPermissionPreset preset) => PresetChoice = PresetTag(preset);

    /// <summary>
    /// Shows a stored provider choice. The stored value may be unset or name a provider
    /// the catalog no longer offers; that is resolved once the catalog is known.
    /// </summary>
    internal void SetCurrentProvider(string backendChoice)
    {
        var choice = _modelsDevNames.Count > 0
            ? ProviderSetupGuidance.ResolveUnsetChoice(backendChoice, _modelsDevNames.Select(pair => (pair.Key, pair.Value)))
            : ProviderSetupGuidance.NormalizeProviderChoice(backendChoice);
        ChangeProviderChoice(choice);
    }

    [RelayCommand]
    private void SelectTheme(string? tag)
    {
        if (IsLoading || tag is null) return;
        ThemeChoice = tag.ToLowerInvariant() switch
        {
            "dark" => "dark",
            "dark-meteor" => "dark-meteor",
            _ => "light",
        };
        ThemeChanged?.Invoke(ThemeChoice switch
        {
            "dark" => UiTheme.Dark,
            "dark-meteor" => UiTheme.DarkMeteorShower,
            _ => UiTheme.Light,
        });
    }

    [RelayCommand]
    private void SelectPreset(string? tag)
    {
        if (IsLoading || tag is null) return;
        PresetChoice = tag.ToLowerInvariant() switch
        {
            "plan" => "plan",
            "full_access" or "full" => "full_access",
            _ => "standard",
        };
        PresetChanged?.Invoke(PresetChoice switch
        {
            "plan" => AgentPermissionPreset.Plan,
            "full_access" => AgentPermissionPreset.FullAccess,
            _ => AgentPermissionPreset.Standard,
        });
    }

    /// <summary>Commits a picker row as the active provider.</summary>
    [RelayCommand]
    private void SelectProvider(ProviderRow? row)
    {
        if (IsLoading || row is null || !ProviderSetupGuidance.IsSelectableProvider(row.Value)) return;
        ChangeProviderChoice(ProviderSetupGuidance.NormalizeProviderChoice(row.Value));
        IsProviderEditorOpen = true;
        ProviderFilter = string.Empty;
        ResetProviderModels();
        ProviderPickerCommitted?.Invoke();
        _catalog?.SetBackend(ProviderChoice);
        _ = RefreshCatalogAsync();
        ScheduleAutoSave();
    }

    /// <summary>
    /// Fetches the selected provider's model list. Pending edits are saved first, then the
    /// catalog is refreshed without reusing a pre-save request; the returned list is
    /// read-only because model selection still belongs to the chat picker.
    /// </summary>
    [RelayCommand]
    private async Task FetchAvailableModelsAsync()
    {
        var catalog = _catalog;
        if (_backend is null || catalog is null)
        {
            ShowProviderModelsStatus(new ProviderConnectionTest(
                ProviderConnectionTest.ErrorTone, "The backend is not ready yet. Try again in a moment."));
            return;
        }

        CancelDelay(ref _autoSaveDelay);
        IsProviderModelsFetchEnabled = false;
        ProviderModels.Clear();
        OnPropertyChanged(nameof(HasProviderModels));
        ShowProviderModelsStatus(ProviderConnectionTestPresentation.Testing());
        try
        {
            // The Host answers from stored state. A key typed less than one debounce ago
            // is not stored yet, and an in-flight key check must finish before the catalog
            // is asked: otherwise the fetch proves the previous (or no) key.
            if (!await SettlePendingKeyAsync())
            {
                ShowProviderModelsStatus(new ProviderConnectionTest(
                    ProviderConnectionTest.ErrorTone,
                    "The API key has not been saved yet. Check the key, then fetch the list again."));
                return;
            }

            // An explicit fetch may not be answered by a catalog request that started
            // before the key or settings it is meant to prove.
            var run = await SettingsProviderPresenter.FetchProviderModelsAsync(
                SaveSettingsAsync,
                () => catalog.RefreshAsync(force: true, bypassManualThrottle: true),
                () => catalog.State);
            GuiDiagnosticWriter.AppendTraceLine(
                $"[provider-models] backend={ProviderChoice} refreshed={run.Refreshed} code={run.State?.ErrorCode ?? "none"}");
            if (run.State is null)
            {
                // Saving failed, so the Host would answer from settings that never landed.
                ShowProviderModelsStatus(ProviderConnectionTestPresentation.Project(null));
            }
            else if (!run.Refreshed)
            {
                ShowProviderModelsStatus(new ProviderConnectionTest(
                    ProviderConnectionTest.NeutralTone,
                    "Could not refresh the provider list. Try again in a moment."));
            }
            else if (!run.State.IsSuccess)
            {
                var outcome = ProviderConnectionTestPresentation.Project(run.State);
                if (ProviderConnectionTestPresentation.DiagnosticCode(outcome, run.State) is { } errorCode)
                {
                    // The message alone cannot tell a blocked network from a bad key next
                    // time: record the catalog code locally.
                    GuiDiagnosticWriter.WriteGuiError("provider_models", errorCode);
                }

                ShowProviderModelsStatus(outcome, run.State.ErrorCode);
            }
            else
            {
                ApplyFetchedModels(run.State);
                int count = ProviderModels.Count;
                ShowProviderModelsStatus(new ProviderConnectionTest(
                        count == 0 ? ProviderConnectionTest.NeutralTone : ProviderConnectionTest.SuccessTone,
                        count == 0
                            ? "Connected. The provider lists no models."
                            : $"Connected. {count:N0} model{(count == 1 ? string.Empty : "s")} available."),
                    run.State.ErrorCode);
            }
        }
        catch (Exception ex)
        {
            GuiDiagnosticWriter.WriteCrashEvent(ex);
            ShowProviderModelsStatus(new ProviderConnectionTest(
                ProviderConnectionTest.ErrorTone, "The fetch could not run. Open Tools → Activity Log to check."));
        }
        finally
        {
            IsProviderModelsFetchEnabled = true;
        }
    }

    private void ApplyFetchedModels(ProviderCatalogState state)
    {
        ProviderModels.Clear();
        if (state.Catalog.Count > 0)
        {
            foreach (var model in state.Catalog)
            {
                if (!string.IsNullOrWhiteSpace(model.Id))
                    ProviderModels.Add(new ProviderModelRow(model));
            }
        }
        else
        {
            foreach (string id in state.Models)
            {
                if (!string.IsNullOrWhiteSpace(id))
                    ProviderModels.Add(new ProviderModelRow(new ProviderCatalogModel { Id = id }));
            }
        }

        OnPropertyChanged(nameof(HasProviderModels));
    }

    /// <summary>
    /// Removes the active provider's stored key. Pasting a key again signs back in, so
    /// no confirmation is asked.
    /// </summary>
    [RelayCommand]
    private async Task SignOutAsync()
    {
        if (_backend is null || string.IsNullOrWhiteSpace(ProviderChoice)) return;
        IsSignOutEnabled = false;
        string name = ProviderButtonText.Trim().Length > 0 ? ProviderButtonText.Trim() : ProviderChoice;
        try
        {
            bool removed = await SettingsProviderPresenter.SignOutAsync(
                ProviderChoice, _backend.RemoveProviderAsync, ClearKeyInput, LoadAsync);
            // The reload clears the status line, so the confirmation lands after it.
            SetKeyStatus(removed
                    ? $"Signed out of {name}. Paste a new key to sign in again."
                    : "Could not sign out; the backend did not answer.",
                removed ? ProviderConnectionTest.SuccessTone : ProviderConnectionTest.ErrorTone);
        }
        catch (Exception ex)
        {
            GuiDiagnosticWriter.WriteCrashEvent(ex);
            SetKeyStatus("Could not sign out; try again.", ProviderConnectionTest.ErrorTone);
        }
        finally
        {
            IsSignOutEnabled = true;
        }
    }

    /// <summary>Removes the stored key of a fixed provider alias such as the custom endpoint.</summary>
    [RelayCommand]
    private async Task RemoveProviderAsync(string? alias)
    {
        if (_backend is null || string.IsNullOrWhiteSpace(alias)) return;
        try
        {
            if (await _backend.RemoveProviderAsync(alias))
            {
                ClearKeyInput(alias);
                await LoadAsync();
            }
        }
        catch (Exception ex)
        {
            GuiDiagnosticWriter.WriteCrashEvent(ex);
        }
    }

    internal Task RefreshCatalogAsync() =>
        _catalog?.RefreshAsync(force: true, bypassManualThrottle: true) ?? Task.FromResult(false);

    /// <summary>Reloads stored settings and the provider catalog. The latest load wins.</summary>
    internal async Task LoadAsync()
    {
        var backend = _backend;
        if (backend is null) return;
        long request = _presenter.BeginLoad();
        IsLoading = true;
        ProviderOverviewMessage = string.Empty;
        try
        {
            var load = await _presenter.LoadAsync(
                request, backend.GetSettingsAsync, () => RefreshModelsDevAsync(backend, request));
            if (load is null || !ReferenceEquals(backend, _backend)) return;
            var settings = load.Settings;
            _lastSettings = settings;
            CustomUrl = settings.CustomUrl ?? string.Empty;
            CloudflareAccountId = settings.CloudflareAccountId ?? string.Empty;
            CustomApiKey = string.Empty;
            _savedCustomKeyInBox = null;
            SetCustomKeyStatus(string.Empty, ProviderConnectionTest.NeutralTone);
            CustomApiKeyPlaceholder = settings.HasCustomKey ? "Saved (leave blank to keep unchanged)" : "sk-...";
            ModelsDevApiKey = string.Empty;
            _savedKeyInBox = null;
            SetKeyStatus(string.Empty, ProviderConnectionTest.NeutralTone);
            // The catalog is known by now, so the stored choice resolves against it.
            SetCurrentProvider(ProviderSetupGuidance.EffectiveStoredChoice(settings.BackendChoice, settings));
            ResetProviderModels();
            _catalog?.SetBackend(ProviderChoice);
            ProviderOverviewMessage = _modelsDevLoadFailed
                ? "The provider list is unavailable. Your saved providers are still shown. Choose Retry to reload."
                : string.Empty;
        }
        catch (Exception ex)
        {
            // Loads are fire-and-forget from the view: trace instead of faulting unobserved.
            GuiDiagnosticWriter.AppendTraceLine($"[SettingsViewModel] Settings load failed: {ex.Message}");
            ProviderOverviewMessage = "Could not load provider settings. Choose Retry to reload.";
        }
        finally
        {
            // A throw above must not leave the editor read-only until the next restart.
            if (_presenter.IsCurrentLoad(request)) IsLoading = false;
        }
    }

    private async Task RefreshModelsDevAsync(ISettingsBackend backend, long request)
    {
        bool IsCurrent() => _presenter.IsCurrentLoad(request) && ReferenceEquals(backend, _backend);
        try
        {
            var providers = await backend.GetModelsDevAsync();
            if (!IsCurrent()) return;
            _modelsDevLoadFailed = providers.Count == 0;
            RebuildProviderRows(providers);
            var keyIds = await backend.GetModelsDevKeysAsync();
            if (!IsCurrent()) return;
            _modelsDevKeyIds = keyIds;
            ApplyProviderKeys();
        }
        catch (Exception ex)
        {
            if (!IsCurrent()) return;
            _modelsDevLoadFailed = true;
            GuiDiagnosticWriter.AppendTraceLine(
                $"[provider-list] load-failed: type={ex.GetType().Name} hresult=0x{ex.HResult:X8} rows={ProviderRows.Count}");
            ApplyProviderFilter();
        }
    }

    /// <summary>
    /// Replaces the picker rows with the custom endpoint plus the Models.dev catalog and
    /// resolves an unset choice against it. Resolving changes the visible key row, which
    /// used to stay hidden on a fresh profile because only the load recomputed it.
    /// </summary>
    private void RebuildProviderRows(IReadOnlyList<ModelsDevEntryDto> providers)
    {
        var catalog = providers.Select(entry => (entry.Id, entry.Name)).ToArray();
        var projection = SettingsProviderPresenter.BuildProviderRows(catalog);
        _modelsDevNames.Clear();
        foreach (var (id, name) in projection.Names) _modelsDevNames[id] = name;

        ProviderRows.Clear();
        foreach (var row in projection.Rows) ProviderRows.Add(row);
        CatalogProviderRows.Clear();
        foreach (var row in projection.Rows.Where(row => row.Value != "custom")) CatalogProviderRows.Add(row);

        ChangeProviderChoice(ProviderSetupGuidance.ResolveUnsetChoice(ProviderChoice, catalog));
        ApplyProviderFilter();
        LogProviderListState("rebuild");
    }

    private void ApplyProviderKeys()
    {
        var keys = new HashSet<string>(_modelsDevKeyIds, StringComparer.OrdinalIgnoreCase);
        foreach (var row in ProviderRows)
        {
            row.HasKey = row.Value == "custom"
                ? _lastSettings?.HasCustomKey == true
                : keys.Contains(row.Value[ProviderSetupGuidance.ModelsDevPrefix.Length..]);
        }

        // Signed-in providers lead the picker; each group stays alphabetical.
        var ordered = SettingsProviderPresenter.OrderSignedInFirst(ProviderRows);
        for (int index = 0; index < ordered.Count; index++)
        {
            int current = ProviderRows.IndexOf(ordered[index]);
            if (current != index) ProviderRows.Move(current, index);
        }
        var catalogOrder = ordered.Where(row => row.Value != "custom").ToArray();
        for (int index = 0; index < catalogOrder.Length; index++)
        {
            int current = CatalogProviderRows.IndexOf(catalogOrder[index]);
            if (current != index) CatalogProviderRows.Move(current, index);
        }

        RefreshProviderPresentation();
    }

    partial void OnProviderFilterChanged(string value) => ApplyProviderFilter();

    private void ApplyProviderFilter()
    {
        var projection = SettingsProviderPresenter.Filter(CatalogProviderRows.ToArray(), ProviderFilter, _modelsDevLoadFailed);
        var visible = new HashSet<ProviderRow>(projection.VisibleRows);
        foreach (var row in ProviderRows) row.IsVisible = visible.Contains(row);
        // The count stays visible at zero matches: "PROVIDERS (0 of 191)" says the
        // catalog is there and the query is what is narrow.
        ProviderHeaderText = projection.HeaderText;
        HasNoProviderMatches = projection.HasNoMatches;
        RefreshProviderPresentation();
        LogProviderListState("filter");
    }

    private void ChangeProviderChoice(string choice)
    {
        if (!string.Equals(ProviderChoice, choice, StringComparison.OrdinalIgnoreCase))
        {
            // A key kept in the box after an unchecked save belongs to the provider it was saved for.
            if (!HasUnsavedKey) ClearSavedKeyInBox();
            _keyProbe.Invalidate();
            SetKeyStatus(string.Empty, ProviderConnectionTest.NeutralTone);
            ResetProviderModels();
        }

        ProviderChoice = choice;
        RefreshProviderPresentation();
    }

    /// <summary>
    /// Derives every provider-dependent value from the current choice, rows and keys
    /// in one place, so no path can update the choice without the rows that depend on it.
    /// </summary>
    private void RefreshProviderPresentation()
    {
        string label = string.Empty;
        foreach (var row in ProviderRows)
        {
            row.IsSelected = string.Equals(row.Value, ProviderChoice, StringComparison.OrdinalIgnoreCase);
            if (row.IsSelected) label = row.DisplayName;
        }

        bool isModelsDev = ProviderSetupGuidance.IsModelsDevChoice(ProviderChoice);
        if (isModelsDev)
        {
            // Before the rows load there is nothing to match; name it from the catalog or the raw id.
            var id = ProviderChoice[ProviderSetupGuidance.ModelsDevPrefix.Length..];
            label = _modelsDevNames.TryGetValue(id, out var name) ? name : id;
        }
        else if (ProviderChoice == ProviderSetupGuidance.UnsetChoice)
        {
            label = NoProviderLabel;
        }
        else if (label.Length == 0 && ProviderSetupGuidance.IsKnown(ProviderChoice))
        {
            // A stored legacy alias (OpenAI, Anthropic) has no picker row but is still
            // named; the previous view showed whatever label the last rebuild left behind.
            label = ProviderSetupGuidance.DisplayName(ProviderChoice);
        }

        ProviderButtonText = label;
        IsCustomProviderVisible = !isModelsDev && ProviderChoice == "custom";
        IsModelsDevKeyVisible = isModelsDev;
        string keyName = label.Length == 0 || label == NoProviderLabel
            ? ProviderSetupGuidance.DisplayName(ProviderChoice)
            : label;
        ModelsDevKeyLabel = isModelsDev ? $"{keyName} API key" : "API key";
        IsCloudflareAccountVisible = string.Equals(
            ProviderSetupGuidance.NormalizeProviderChoice(ProviderChoice), CloudflareChoice, StringComparison.OrdinalIgnoreCase);
        // Signing out only makes sense while a key is stored; the picker's dot reads the same source.
        IsModelsDevSignOutVisible = isModelsDev && SettingsProviderPresenter.IsSignedIn(
            ProviderChoice, _lastSettings?.HasCustomKey == true, _modelsDevKeyIds);
        RefreshProviderManagement();
    }

    partial void OnCustomUrlChanged(string value) => ScheduleAutoSave();

    /// <summary>
    /// The custom key is never part of the autosave: a quiet box first saves the URL,
    /// then sends the key alone to the checked key route, which asks that server.
    /// </summary>
    partial void OnCustomApiKeyChanged(string value)
    {
        if (IsLoading) return;
        CancelDelay(ref _customKeyProbeDelay);
        SetCustomKeyStatus(string.Empty, ProviderConnectionTest.NeutralTone);
        if (string.IsNullOrWhiteSpace(value) || value.Trim() == _savedCustomKeyInBox) return;
        _ = RunAfterDelayAsync(KeyProbeDelay, ProbeCustomKeyAsync, ref _customKeyProbeDelay);
    }

    /// <summary>The custom key an unchecked save stored while leaving it in the box, or null.</summary>
    private string? _savedCustomKeyInBox;
    private CancellationTokenSource? _customKeyProbeDelay;
    private bool _customKeyProbeRunning;
    private bool _customKeyProbeAgain;

    /// <summary>Whether the custom key box holds text that has not been stored.</summary>
    internal bool HasUnsavedCustomKey =>
        !string.IsNullOrWhiteSpace(CustomApiKey) && CustomApiKey.Trim() != _savedCustomKeyInBox;

    [ObservableProperty] public partial string CustomKeyStatus { get; private set; } = string.Empty;
    [ObservableProperty] public partial string CustomKeyStatusTone { get; private set; } = ProviderConnectionTest.NeutralTone;

    private void SetCustomKeyStatus(string message, string tone)
    {
        CustomKeyStatus = message;
        CustomKeyStatusTone = tone;
    }

    private async Task ProbeCustomKeyAsync()
    {
        var backend = _backend;
        string key = CustomApiKey.Trim();
        if (backend is null || IsLoading || key.Length == 0 || key == _savedCustomKeyInBox) return;
        if (_customKeyProbeRunning)
        {
            // The check in flight is for older text; it reschedules this edit when it ends.
            _customKeyProbeAgain = true;
            return;
        }
        _customKeyProbeRunning = true;
        _customKeyProbeAgain = false;
        try
        {
            // The key is checked against the saved URL, so a pending URL edit goes first.
            CancelDelay(ref _autoSaveDelay);
            if (!await SaveSettingsAsync())
            {
                SetCustomKeyStatus("Could not save the server URL; key not sent. Try again.", ProviderConnectionTest.ErrorTone);
                return;
            }

            SetCustomKeyStatus("Checking key…", ProviderConnectionTest.NeutralTone);
            var result = await backend.ValidateAndSaveCustomKeyAsync(key, CancellationToken.None);
            if (CustomApiKey.Trim() != key)
            {
                _customKeyProbeAgain = true; // edited meanwhile: check the newer text next
                return;
            }
            switch (result.Code)
            {
                case "valid":
                    CustomApiKey = string.Empty;
                    SetCustomKeyStatus("Saved. The server accepted the key.", ProviderConnectionTest.SuccessTone);
                    break;
                case "unchecked":
                    // Kept, so finishing a hand-typed key re-saves all of it.
                    _savedCustomKeyInBox = key;
                    SetCustomKeyStatus("Saved. The server could not confirm the key before use; the first request will.",
                        ProviderConnectionTest.NeutralTone);
                    break;
                case "invalid":
                    SetCustomKeyStatus("The server rejected this key (HTTP 401); saved key unchanged.", ProviderConnectionTest.ErrorTone);
                    return;
                case "insecure":
                    SetCustomKeyStatus("Key not sent: this URL uses plain http:// to another computer. Use https://.", ProviderConnectionTest.ErrorTone);
                    return;
                case "malformed":
                    SetCustomKeyStatus("Key not saved: it contains a line break or another control character.", ProviderConnectionTest.ErrorTone);
                    return;
                case "too_long":
                    SetCustomKeyStatus("Key not saved: it is longer than Windows Credential Manager holds.", ProviderConnectionTest.ErrorTone);
                    return;
                default:
                    SetCustomKeyStatus("Could not confirm the save. Refresh or try again.", ProviderConnectionTest.ErrorTone);
                    return;
            }

            if (_lastSettings is { } stored) stored.HasCustomKey = true;
            RefreshProviderManagement();
            _ = RefreshCatalogAsync();
        }
        catch (Exception ex)
        {
            GuiDiagnosticWriter.WriteCrashEvent(ex);
            SetCustomKeyStatus("Could not confirm the save. Refresh or try again.", ProviderConnectionTest.ErrorTone);
        }
        finally
        {
            _customKeyProbeRunning = false;
            if (_customKeyProbeAgain && HasUnsavedCustomKey && !_disposed)
            {
                _customKeyProbeAgain = false;
                _ = RunAfterDelayAsync(KeyProbeDelay, ProbeCustomKeyAsync, ref _customKeyProbeDelay);
            }
        }
    }

    partial void OnCloudflareAccountIdChanged(string value) => ScheduleAutoSave();

    /// <summary>A quiet key box is checked and saved one second after the last keystroke.</summary>
    partial void OnModelsDevApiKeyChanged(string value)
    {
        _keyProbe.Invalidate();
        if (IsLoading) return;
        CancelDelay(ref _keyProbeDelay);
        SetKeyStatus(string.Empty, ProviderConnectionTest.NeutralTone);
        if (string.IsNullOrEmpty(value)) return;
        _ = RunAfterDelayAsync(KeyProbeDelay, ProbeKeyAsync, ref _keyProbeDelay);
    }

    private void ScheduleAutoSave()
    {
        if (!_presenter.ScheduleAutoSave(IsLoading)) return;
        CancelDelay(ref _autoSaveDelay);
        _ = RunAfterDelayAsync(AutoSaveDelay, async () =>
        {
            if (_presenter.TryConsumeAutoSave()) await SaveSettingsAsync();
        }, ref _autoSaveDelay);
    }

    private Task RunAfterDelayAsync(TimeSpan delay, Func<Task> action, ref CancellationTokenSource? slot)
    {
        var cancellation = new CancellationTokenSource();
        slot = cancellation;
        return RunAsync();

        async Task RunAsync()
        {
            try
            {
                await _delay(delay, cancellation.Token);
                if (cancellation.IsCancellationRequested || _disposed) return;
                await action();
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                GuiDiagnosticWriter.WriteCrashEvent(ex);
            }
        }
    }

    private static void CancelDelay(ref CancellationTokenSource? slot)
    {
        var current = slot;
        slot = null;
        current?.Cancel();
        current?.Dispose();
    }

    /// <summary>
    /// The Host checks a candidate key through the provider's catalog before storing it.
    /// Rejection or an unavailable catalog keeps the saved credential, and a superseded
    /// edit cannot publish a stale verdict.
    /// </summary>
    private async Task ProbeKeyAsync()
    {
        if (_keyProbeOperation is { IsCompleted: false } running)
        {
            await running;
            return;
        }

        var operation = RunProbeKeySafeAsync();
        _keyProbeOperation = operation;
        try
        {
            await operation;
        }
        finally
        {
            if (ReferenceEquals(_keyProbeOperation, operation)) _keyProbeOperation = null;
        }
    }

    private async Task RunProbeKeySafeAsync()
    {
        try
        {
            await ProbeKeyCoreAsync();
        }
        catch (Exception ex)
        {
            GuiDiagnosticWriter.WriteCrashEvent(ex);
            SetKeyStatus("Could not verify the connection; saved key unchanged. Try again.", ProviderConnectionTest.NeutralTone);
        }
    }

    /// <summary>
    /// Makes the key box agree with the vault before an explicit action reads stored
    /// state: waits for an in-flight check, then runs the debounced one if the text is
    /// still unsaved. Returns false when the key could not be saved.
    /// </summary>
    private async Task<bool> SettlePendingKeyAsync()
    {
        CancelDelay(ref _keyProbeDelay);
        var failed = false;
        if (_keyProbeOperation is { } inFlight)
        {
            try
            {
                await inFlight;
            }
            catch (Exception ex)
            {
                failed = true;
                GuiDiagnosticWriter.WriteCrashEvent(ex);
            }
        }

        CancelDelay(ref _keyProbeDelay);
        if (!failed && HasUnsavedKey)
        {
            await ProbeKeyAsync();
        }

        return !failed && !HasUnsavedKey;
    }

    private async Task ProbeKeyCoreAsync()
    {
        var backend = _backend;
        if (IsLoading || _keyProbe.IsRunning || backend is null || _catalog is null) return;
        if (!ProviderSetupGuidance.IsModelsDevChoice(ProviderChoice)) return;
        // A copied key often brings a space or line break with it, which the provider
        // then refuses; no key contains one. The terminal trims the same way.
        string key = ModelsDevApiKey.Trim();
        if (key.Length == 0 || key == _savedKeyInBox) return;

        var pending = _keyProbe.RunAsync(ProviderChoice, key, backend.ValidateAndSaveModelsDevKeyAsync);
        if (_keyProbe.IsRunning) SetKeyStatus("Checking key…", ProviderConnectionTest.NeutralTone);
        var completion = await pending;
        if (completion is null) return;
        if (completion.RetryRequested && !string.IsNullOrWhiteSpace(ModelsDevApiKey))
        {
            _ = RunAfterDelayAsync(KeyProbeDelay, ProbeKeyAsync, ref _keyProbeDelay);
        }

        switch (completion.Result.Code)
        {
            case "superseded":
                return;
            case "valid":
            case "unchecked":
                bool isChecked = completion.Result.Code == "valid";
                // An unchecked save keeps the box: a pause while typing by hand saved what
                // was typed so far, and the next keystroke must re-save the whole key, not
                // start a new one from an empty box.
                if (isChecked) ModelsDevApiKey = string.Empty;
                else _savedKeyInBox = key;
                _modelsDevKeyIds = _modelsDevKeyIds.Append(completion.Target.ModelsDevId!)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                ApplyProviderKeys();
                SetKeyStatus(isChecked
                        ? $"Saved key for {completion.Target.DisplayName}."
                        : $"Saved key for {completion.Target.DisplayName}. This provider cannot check a key before use; the first request will.",
                    isChecked ? ProviderConnectionTest.SuccessTone : ProviderConnectionTest.NeutralTone);
                await AdoptShownProviderAsync();
                _ = RefreshCatalogAsync();
                break;
            case "invalid":
                SetKeyStatus("Invalid key; saved key unchanged.", ProviderConnectionTest.ErrorTone);
                break;
            case "malformed":
                SetKeyStatus("Key not saved: it contains a line break or another control character. Copy it again.", ProviderConnectionTest.ErrorTone);
                break;
            case "too_long":
                SetKeyStatus("Key not saved: it is longer than Windows Credential Manager holds. Check that only the key was pasted.", ProviderConnectionTest.ErrorTone);
                break;
            case "save_failed":
                SetKeyStatus("Could not confirm the save. Refresh or try again.", ProviderConnectionTest.ErrorTone);
                break;
            default:
                SetKeyStatus("Could not verify the connection; saved key unchanged. Try again.", ProviderConnectionTest.NeutralTone);
                break;
        }
    }

    /// <summary>
    /// Stores the provider the editor shows when the Host still holds another one. The
    /// key route stores only the key, so after a first run resolved the Host's keyless
    /// default to a catalog provider, saving a key for it left the Host on the old
    /// default and the chat still reported a missing key. A key typed for the provider
    /// on screen is the user choosing it.
    /// </summary>
    private async Task AdoptShownProviderAsync()
    {
        if (_lastSettings is not { } stored
            || string.Equals(ProviderSetupGuidance.NormalizeProviderChoice(stored.BackendChoice), ProviderChoice, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (await SaveSettingsAsync()) stored.BackendChoice = ProviderChoice;
    }

    internal async Task<bool> SaveSettingsAsync()
    {
        var backend = _backend;
        if (backend is null) return false;
        try
        {
            bool saved = await _presenter.SaveAsync(() => backend.SaveProviderSettingsAsync(new ProviderSettingsUpdateDto
            {
                BackendChoice = ProviderChoice,
                CustomUrl = string.IsNullOrWhiteSpace(CustomUrl) ? DefaultCustomUrl : CustomUrl.Trim(),
                // The key travels only through the checked key route (ProbeCustomKeyAsync).
                CustomApiKey = string.Empty,
                CloudflareAccountId = CloudflareAccountId.Trim(),
            }));
            if (!saved) GuiDiagnosticWriter.WriteGuiError("settings_autosave", "save_failed");
            return saved;
        }
        catch (Exception ex)
        {
            GuiDiagnosticWriter.WriteCrashEvent(ex);
            return false;
        }
    }

    /// <summary>
    /// The key an unchecked save stored while leaving it in the box, or null. Keeping it
    /// lets a hand-typed key be finished and re-saved whole; this field keeps that saved
    /// text from counting as a pending, unsaved edit.
    /// </summary>
    private string? _savedKeyInBox;

    /// <summary>Whether the key box holds text that has not been stored.</summary>
    internal bool HasUnsavedKey =>
        !string.IsNullOrWhiteSpace(ModelsDevApiKey) && ModelsDevApiKey.Trim() != _savedKeyInBox;

    private void ClearSavedKeyInBox()
    {
        if (_savedKeyInBox is null) return;
        _savedKeyInBox = null;
        ModelsDevApiKey = string.Empty;
    }

    private void ClearKeyInput(string alias)
    {
        if (ProviderSetupGuidance.IsModelsDevChoice(alias)) { ModelsDevApiKey = string.Empty; _savedKeyInBox = null; }
        else if (alias == "custom") { CustomApiKey = string.Empty; _savedCustomKeyInBox = null; }
    }

    private void SetKeyStatus(string message, string tone)
    {
        ModelsDevKeyStatus = message;
        ModelsDevKeyStatusTone = tone;
    }

    private void ShowProviderModelsStatus(ProviderConnectionTest result, string? errorCode = null)
    {
        ProviderModelsStatus = result.Message;
        ProviderModelsStatusTone = result.Tone;
        string code = errorCode?.Trim() ?? string.Empty;
        ProviderModelsErrorCode = code.Length is > 0 and <= 64 ? code : null;
    }

    /// <summary>The list starts empty on every provider/open; only an explicit fetch fills it.</summary>
    private void ResetProviderModels()
    {
        ProviderModels.Clear();
        OnPropertyChanged(nameof(HasProviderModels));
        ShowProviderModelsStatus(new ProviderConnectionTest(
            ProviderConnectionTest.NeutralTone, "No models fetched yet."));
    }

    /// <summary>
    /// Shows a background catalog failure, but never a background success: the catalog
    /// also refreshes on a timer, and a result appearing unasked would read as the user's own.
    /// </summary>
    private void OnCatalogStateChanged(object? sender, ProviderCatalogState state)
    {
        var outcome = ProviderConnectionTestPresentation.Project(state);
        if (!ProviderConnectionTestPresentation.ReportsFailure(outcome)) return;
        var catalog = sender as IProviderCatalogSession ?? _catalog;
        _dispatch(() =>
        {
            if (_disposed || (catalog is not null && !ReferenceEquals(catalog, _catalog))) return;
            // A provider with no key cannot list its models; saying "could not reach the
            // provider" sent a new user to check their network instead of the key box.
            if (IsCurrentProviderKeyMissing() && IsMissingKeyShapedFailure(state.ErrorCode))
            {
                ShowProviderModelsStatus(new ProviderConnectionTest(
                    ProviderConnectionTest.NeutralTone, $"Add a {ProviderButtonText} API key above to load its models."),
                    state.ErrorCode);
                return;
            }

            ShowProviderModelsStatus(outcome, state.ErrorCode);
        });
    }

    /// <summary>
    /// Failures a missing key produces. A timeout, a rate limit or an unreadable answer
    /// is a different problem and keeps its own message, even for a provider without a
    /// stored key.
    /// </summary>
    private static bool IsMissingKeyShapedFailure(string? errorCode) =>
        (errorCode?.Trim() ?? string.Empty) is "" or "catalog_unavailable" or "catalog_configuration_error" or "catalog_unauthorized";

    /// <summary>Whether the selected provider needs a key and none is stored.</summary>
    private bool IsCurrentProviderKeyMissing()
    {
        if (ProviderSetupGuidance.IsModelsDevChoice(ProviderChoice))
        {
            return !SettingsProviderPresenter.IsSignedIn(ProviderChoice, _lastSettings?.HasCustomKey == true, _modelsDevKeyIds);
        }

        return ProviderSetupGuidance.IsMissingKey(_lastSettings, ProviderChoice);
    }

    private void LogProviderListState(string stage)
    {
        int visible = ProviderRows.Count(row => row.IsVisible);
        GuiDiagnosticWriter.AppendTraceLine(
            $"[provider-list] {stage}: rows={ProviderRows.Count} visible={visible} filter='{ProviderFilter}' loadFailed={_modelsDevLoadFailed}");
    }

    private void Detach()
    {
        _keyProbe.Invalidate();
        CancelDelay(ref _autoSaveDelay);
        CancelDelay(ref _keyProbeDelay);
        CancelDelay(ref _customKeyProbeDelay);
        if (_catalog is not null) _catalog.StateChanged -= OnCatalogStateChanged;
        _catalog = null;
        _backend = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Detach();
        _keyProbe.Dispose();
    }

    private static string ThemeTag(UiTheme theme) => theme switch
    {
        UiTheme.Dark => "dark",
        UiTheme.DarkMeteorShower => "dark-meteor",
        _ => "light",
    };

    private static string PresetTag(AgentPermissionPreset preset) => preset switch
    {
        AgentPermissionPreset.Plan => "plan",
        AgentPermissionPreset.FullAccess => "full_access",
        _ => "standard",
    };
}
