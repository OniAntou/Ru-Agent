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
/// picker, provider credentials, autosave and the connection test. The view renders
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

    [ObservableProperty] public partial string ProviderTestMessage { get; private set; } = "Not tested yet. The test checks the key and loads the model list.";

    [ObservableProperty] public partial string ProviderTestTone { get; private set; } = ProviderConnectionTest.NeutralTone;

    /// <summary>The catalog's raw code for the last test, kept out of the reading path.</summary>
    [ObservableProperty] public partial string? ProviderTestErrorCode { get; private set; }

    [ObservableProperty] public partial bool IsProviderTestEnabled { get; private set; } = true;

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
        ProviderPickerCommitted?.Invoke();
        _catalog?.SetBackend(ProviderChoice);
        _ = RefreshCatalogAsync();
        ScheduleAutoSave();
    }

    /// <summary>
    /// Asks the selected provider whether it answers. Pending edits are saved first:
    /// the Host answers from stored settings, so testing before a debounced save
    /// lands would prove the previous key.
    /// </summary>
    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        var catalog = _catalog;
        if (_backend is null || catalog is null)
        {
            ShowProviderTest(new ProviderConnectionTest(
                ProviderConnectionTest.ErrorTone, "The backend is not ready yet. Try again in a moment."));
            return;
        }

        CancelDelay(ref _autoSaveDelay);
        CancelDelay(ref _keyProbeDelay);
        IsProviderTestEnabled = false;
        ShowProviderTest(ProviderConnectionTestPresentation.Testing());
        try
        {
            // The test is an explicit question about the provider shown, so it may not be
            // answered from a catalog the editor already had: the manual throttle is
            // bypassed and a suppressed refresh is reported instead of a stale verdict.
            var run = await SettingsProviderPresenter.TestConnectionAsync(
                SaveSettingsAsync,
                () => catalog.RefreshAsync(force: true, bypassManualThrottle: true),
                () => catalog.State);
            GuiDiagnosticWriter.AppendTraceLine(
                $"[provider-test] backend={ProviderChoice} refreshed={run.Refreshed} code={run.State?.ErrorCode ?? "none"}");
            if (run.State is null)
            {
                // Saving failed, so the Host would answer from settings that never landed.
                ShowProviderTest(ProviderConnectionTestPresentation.Project(null));
            }
            else if (!run.Refreshed)
            {
                ShowProviderTest(new ProviderConnectionTest(
                    ProviderConnectionTest.NeutralTone,
                    "Could not refresh the provider list. Try again in a moment."));
            }
            else
            {
                var outcome = NameTheProvider(ProviderConnectionTestPresentation.Project(run.State));
                if (ProviderConnectionTestPresentation.DiagnosticCode(outcome, run.State) is { } errorCode)
                {
                    // The message alone cannot tell a blocked network from a bad key next
                    // time: record the catalog code locally.
                    GuiDiagnosticWriter.WriteGuiError("provider_test", errorCode);
                }

                ShowProviderTest(outcome, run.State.ErrorCode);
            }
        }
        catch (Exception ex)
        {
            GuiDiagnosticWriter.WriteCrashEvent(ex);
            ShowProviderTest(new ProviderConnectionTest(
                ProviderConnectionTest.ErrorTone, "The test could not run. Open Tools → Activity Log to check."));
        }
        finally
        {
            IsProviderTestEnabled = true;
        }
    }

    /// <summary>
    /// Names the provider the verdict is about. The editor shows several Models.dev
    /// providers whose names differ by a suffix ("OpenCode" and "OpenCode Go"), so a
    /// bare "the provider rejected the key" left the reader guessing which one was tested.
    /// </summary>
    private ProviderConnectionTest NameTheProvider(ProviderConnectionTest test)
    {
        var name = ProviderButtonText.Trim();
        return name.Length == 0 || name == NoProviderLabel
            ? test
            : test with { Message = $"{name}: {test.Message}" };
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
            CustomApiKeyPlaceholder = settings.HasCustomKey ? "Saved (leave blank to keep unchanged)" : "sk-...";
            ModelsDevApiKey = string.Empty;
            SetKeyStatus(string.Empty, ProviderConnectionTest.NeutralTone);
            // The catalog is known by now, so the stored choice resolves against it.
            SetCurrentProvider(ProviderSetupGuidance.EffectiveStoredChoice(settings.BackendChoice, settings));
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
            _keyProbe.Invalidate();
            SetKeyStatus(string.Empty, ProviderConnectionTest.NeutralTone);
            ShowProviderTest(new ProviderConnectionTest(ProviderConnectionTest.NeutralTone,
                "Not tested yet. The test checks the key and loads the model list."));
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

    partial void OnCustomApiKeyChanged(string value) => ScheduleAutoSave();

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

    private async Task ProbeKeyCoreAsync()
    {
        var backend = _backend;
        if (IsLoading || _keyProbe.IsRunning || backend is null || _catalog is null) return;
        if (!ProviderSetupGuidance.IsModelsDevChoice(ProviderChoice)) return;
        string key = ModelsDevApiKey;
        if (string.IsNullOrWhiteSpace(key)) return;

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
                ModelsDevApiKey = string.Empty;
                _modelsDevKeyIds = _modelsDevKeyIds.Append(completion.Target.ModelsDevId!)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                ApplyProviderKeys();
                SetKeyStatus($"Saved key for {completion.Target.DisplayName}.", ProviderConnectionTest.SuccessTone);
                await AdoptShownProviderAsync();
                _ = RefreshCatalogAsync();
                break;
            case "invalid":
                SetKeyStatus("Invalid key; saved key unchanged.", ProviderConnectionTest.ErrorTone);
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
                CustomApiKey = CustomApiKey,
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

    private void ClearKeyInput(string alias)
    {
        if (ProviderSetupGuidance.IsModelsDevChoice(alias)) ModelsDevApiKey = string.Empty;
        else if (alias == "custom") CustomApiKey = string.Empty;
    }

    private void SetKeyStatus(string message, string tone)
    {
        ModelsDevKeyStatus = message;
        ModelsDevKeyStatusTone = tone;
    }

    private void ShowProviderTest(ProviderConnectionTest result, string? errorCode = null)
    {
        ProviderTestMessage = result.Message;
        ProviderTestTone = result.Tone;
        string code = errorCode?.Trim() ?? string.Empty;
        ProviderTestErrorCode = code.Length is > 0 and <= 64 ? code : null;
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
                ShowProviderTest(new ProviderConnectionTest(
                    ProviderConnectionTest.NeutralTone, $"Add a {ProviderButtonText} API key above to load its models."),
                    state.ErrorCode);
                return;
            }

            ShowProviderTest(outcome, state.ErrorCode);
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
