using CommunityToolkit.Mvvm.ComponentModel;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.ViewModels;

/// <summary>How the status line reads; the view maps it to a brush.</summary>
public enum ShellStatusTone
{
    Pending,
    Ready,
    Warning,
    Danger,
}

/// <summary>Where startup stands, for the welcome overlay.</summary>
public enum ShellStartupPhase
{
    NotStarted,
    Starting,
    RootMissing,
    Ready,
    Failed,
}

/// <summary>What one navigation asks of the window.</summary>
internal sealed record ShellNavigation(
    ShellDestination Destination,
    bool LeavesChat,
    bool KeepsCatalogPolling,
    bool IsLogActive,
    bool IsDiagnosticsActive);

/// <summary>
/// The window shell's state and decisions: Host startup, reconnect and health, the
/// status line, navigation and the Settings modal, the Settings ↔ chat preset and
/// theme sync, and the title bar's update and side-panel entries.
/// </summary>
/// <remarks>
/// Call members on the UI thread; Host exits and health results arrive through the
/// dispatch delegate. The window keeps windowing, the title bar region, composition
/// of the Host-bound services and views, dialogs and every animation.
/// The status line has one writer: everything that reports into it goes through here.
/// </remarks>
public sealed partial class ShellViewModel : ObservableObject, IAsyncDisposable
{
    internal static readonly TimeSpan ReconnectAttemptTimeout = TimeSpan.FromSeconds(6);

    private readonly IShellBackend _backend;
    private readonly Action<Action> _dispatch;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly UiThemePreferenceStore _themes;
    private readonly RefreshController<HealthStatusSnapshot?> _health;
    private CancellationTokenSource? _reconnect;
    private long _selectionReload;
    private bool _pollInFlight;
    private bool _hasStarted;
    private bool _closed;

    internal ShellViewModel(
        IShellBackend backend,
        Action<Action> dispatch,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        UiThemePreferenceStore? themes = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        _delay = delay ?? Task.Delay;
        _themes = themes ?? new UiThemePreferenceStore();
        _health = new RefreshController<HealthStatusSnapshot?>(
            token => _backend.GatherHealthAsync(token),
            snapshot => { if (snapshot is not null) ApplyHealth(snapshot); },
            dispatch);
        Theme = _themes.Load();
        _backend.BackendExited += OnBackendExited;
    }

    /// <summary>The project root was resolved; raised before the Host is started with it.</summary>
    public event Action<string>? ProjectRootResolved;

    /// <summary>Settings chose a preset; the chat applies it.</summary>
    public event Action<AgentPermissionPreset>? ChatPresetChosen;

    [ObservableProperty] public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty] public partial ShellStatusTone StatusTone { get; private set; } = ShellStatusTone.Pending;

    [ObservableProperty] public partial ShellStartupPhase StartupPhase { get; private set; } = ShellStartupPhase.NotStarted;

    [ObservableProperty] public partial string WelcomeText { get; private set; } = string.Empty;

    /// <summary>A Host session is up and the window's polls may run.</summary>
    [ObservableProperty] public partial bool IsBackendLive { get; private set; }

    [ObservableProperty] public partial ShellDestination Destination { get; private set; } = ShellDestination.Chat;

    [ObservableProperty] public partial bool IsSettingsOpen { get; private set; }

    /// <summary>Settings exists, so its title-bar entry can open it.</summary>
    [ObservableProperty] public partial bool IsSettingsReady { get; private set; }

    /// <summary>The preset the Settings dropdown shows.</summary>
    [ObservableProperty] public partial AgentPermissionPreset SettingsPreset { get; private set; } = AgentPermissionPreset.Standard;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDarkTheme), nameof(ThemeToggleLabel), nameof(ThemeToggleGlyph))]
    public partial UiTheme Theme { get; private set; }

    [ObservableProperty] public partial bool IsUpdateAvailable { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SidePanelToggleLabel))]
    public partial bool IsSidePanelHidden { get; private set; } = true;

    [ObservableProperty] public partial bool CanToggleSidePanel { get; private set; }

    public bool IsDarkTheme => Theme is UiTheme.Dark or UiTheme.DarkMeteorShower or UiTheme.StarryNight;

    public string ThemeToggleLabel => IsDarkTheme ? "Switch to light mode" : "Switch to dark mode";

    public string ThemeToggleGlyph => IsDarkTheme ? "\uE793" : "\uE706";

    public string SidePanelToggleLabel => IsSidePanelHidden ? "Show side panel" : "Hide side panel";

    internal bool IsReconnectScheduled => _reconnect is not null;

    /// <summary>
    /// Resolves the project root, publishes it, then starts the Host. Returns the
    /// session, or null when there is no root or the Host did not start.
    /// </summary>
    internal async Task<BackendSession?> StartAsync(CancellationToken cancellationToken)
    {
        string name = _backend.DisplayName;
        SetStatus($"Starting {name} Backend...", ShellStatusTone.Pending);
        SetStartup(ShellStartupPhase.Starting, $"Starting {name}...");

        string? projectRoot = _backend.ResolveProjectRoot();
        if (projectRoot is null)
        {
            SetStatus("Project root not found. Please place RuApp.exe inside or under D:\\LLM.", ShellStatusTone.Danger);
            SetStartup(ShellStartupPhase.RootMissing, "Project root not found. Click to continue.");
            return null;
        }

        // Section chrome and workspace pins do not wait for the Host.
        ProjectRootResolved?.Invoke(projectRoot);

        var session = await _backend.StartAsync(projectRoot, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (session is null)
        {
            SetStatus($"Could not connect to {name} Backend!", ShellStatusTone.Danger);
            SetStartup(ShellStartupPhase.Failed, WelcomeText);
            return null;
        }

        ReportReady(session);
        SetStartup(ShellStartupPhase.Ready, WelcomeText);
        _hasStarted = true;
        IsBackendLive = true;
        return session;
    }

    /// <summary>The Settings surface was composed; the title bar's update entry may open it.</summary>
    internal void MarkSettingsReady() => IsSettingsReady = true;

    internal void SetHealthActive(bool active) => _health.SetActive(active);

    internal Task<bool> RefreshHealthAsync() => _health.RefreshAsync();

    /// <summary>
    /// One sessions-and-health poll. A tick that lands while the previous poll is still
    /// running is skipped instead of stacking. Failures are traced, not thrown: the
    /// poll is driven by a timer.
    /// </summary>
    internal async Task<bool> PollAsync(Func<Task> refreshSessions)
    {
        if (_pollInFlight)
        {
            return false;
        }

        _pollInFlight = true;
        try
        {
            await refreshSessions();
            try { await RefreshHealthAsync(); }
            catch (Exception ex)
            {
                GuiDiagnosticWriter.AppendTraceLine($"[Shell] Health poll failed: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            GuiDiagnosticWriter.AppendTraceLine($"[Shell] Sessions poll failed: {ex.Message}");
        }
        finally
        {
            _pollInFlight = false;
        }

        return true;
    }

    /// <summary>A failure the user should see on the status line, such as a session action.</summary>
    internal void ReportError(string message) => SetStatus(message, ShellStatusTone.Danger);

    internal void ApplyHealth(HealthStatusSnapshot snapshot)
    {
        if (!snapshot.BackendRunning)
        {
            SetStatus("Could not connect to .NET Backend!", ShellStatusTone.Danger);
            ScheduleReconnect();
            return;
        }

        StopReconnect();
        if (!snapshot.BackendHealthy)
        {
            SetStatus(
                snapshot.BackendError != null
                    ? $"Backend error ({snapshot.BackendError})"
                    : "Backend starting or not ready yet",
                ShellStatusTone.Warning);
        }
        else
        {
            SetStatus(_backend.IsHarness ? "Harness ready" : "System ready", ShellStatusTone.Ready);
        }
    }

    /// <summary>
    /// Starts retrying the Host every <see cref="UiTiming.ReconnectPoll"/> until one
    /// attempt reconnects. Nothing reconnects before the first start succeeded: the
    /// window has no views to rebind, and a failed start stays reported as failed. A
    /// harness run, a closed window or a retry already scheduled does not start another.
    /// </summary>
    internal void ScheduleReconnect()
    {
        if (!_hasStarted || _closed || _backend.IsHarness || _reconnect is not null)
        {
            return;
        }

        var reconnect = new CancellationTokenSource();
        _reconnect = reconnect;
        _ = RunReconnectAsync(reconnect);
    }

    internal void StopReconnect()
    {
        var reconnect = _reconnect;
        _reconnect = null;
        reconnect?.Cancel();
    }

    private async Task RunReconnectAsync(CancellationTokenSource reconnect)
    {
        try
        {
            while (!reconnect.IsCancellationRequested)
            {
                await _delay(UiTiming.ReconnectPoll, reconnect.Token);
                if (await TryReconnectAsync(reconnect))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (reconnect.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_reconnect, reconnect))
            {
                _reconnect = null;
            }

            reconnect.Dispose();
        }
    }

    /// <summary>
    /// One reconnect attempt. Once the Host is back the retries stop even if rebinding
    /// the window fails, as before: the next health poll reports what is wrong.
    /// </summary>
    private async Task<bool> TryReconnectAsync(CancellationTokenSource reconnect)
    {
        try
        {
            string? projectRoot = _backend.ResolveProjectRoot();
            if (projectRoot is null)
            {
                return false;
            }

            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(reconnect.Token);
            attempt.CancelAfter(ReconnectAttemptTimeout);
            var session = await _backend.StartAsync(projectRoot, attempt.Token);
            if (session is null || reconnect.IsCancellationRequested || _closed)
            {
                return false;
            }

            if (ReferenceEquals(_reconnect, reconnect))
            {
                _reconnect = null;
            }

            ProjectRootResolved?.Invoke(projectRoot);
            ReportReady(session);
            await _backend.RebindAsync(session);
            IsBackendLive = true;
            await RefreshHealthAsync();
            return true;
        }
        catch (OperationCanceledException) when (reconnect.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            GuiDiagnosticWriter.AppendTraceLine($"[Shell] Reconnect attempt failed: {ex.Message}");
            return !ReferenceEquals(_reconnect, reconnect);
        }
    }

    private void OnBackendExited(object? sender, EventArgs e)
    {
        // The provider also reports a Host that died during a start that then failed;
        // reporting that failure belongs to startup.
        try
        {
            _dispatch(() =>
            {
                if (!_hasStarted)
                {
                    return;
                }

                IsBackendLive = false;
                SetStatus($"{_backend.DisplayName} Backend stopped. Reconnecting...", ShellStatusTone.Danger);
                ScheduleReconnect();
            });
        }
        catch (OperationCanceledException)
        {
            // The window's dispatcher is gone; so is anything to reconnect for.
        }
    }

    /// <summary>
    /// Resolves a shell navigation tag other than Settings; the window closes the
    /// Settings modal first. A Chat navigation always keeps catalog polling on, including
    /// one to the chat that is already showing: selecting a session reloads its models.
    /// </summary>
    internal ShellNavigation Navigate(string? tag)
    {
        var destination = ShellRoutePolicy.Resolve(tag);
        Destination = destination;
        return new ShellNavigation(
            destination,
            LeavesChat: destination != ShellDestination.Chat,
            KeepsCatalogPolling: ShellRoutePolicy.KeepsCatalogPolling(tag),
            IsLogActive: ShellRoutePolicy.IsLogActive(tag),
            IsDiagnosticsActive: ShellRoutePolicy.IsDiagnosticsActive(tag));
    }

    /// <summary>
    /// Opens Settings showing the chat's current preset. The chat's preset is read on
    /// every open because the chat may have narrowed it (Full Access left behind on
    /// New Chat) without telling anyone. Returns whether the modal was closed before.
    /// </summary>
    internal bool OpenSettings(AgentPermissionPreset? chatPreset)
    {
        if (chatPreset is { } preset)
        {
            ShowChatPreset(preset);
        }

        bool opening = !IsSettingsOpen;
        IsSettingsOpen = true;
        return opening;
    }

    /// <summary>Settings shows the chat's preset, for example when Settings is composed.</summary>
    internal void ShowChatPreset(AgentPermissionPreset chatPreset) => SettingsPreset = chatPreset;

    /// <summary>Returns whether the modal was open.</summary>
    internal bool CloseSettings()
    {
        bool closing = IsSettingsOpen;
        IsSettingsOpen = false;
        return closing;
    }

    /// <summary>
    /// The chat adopted a session's stored preset. The switch may already have moved on
    /// when this arrives, so only the session still active repaints the dropdown.
    /// </summary>
    internal bool AdoptSessionPreset(string sessionId, AgentPermissionPreset preset, string? activeSessionId)
    {
        if (!string.Equals(sessionId, activeSessionId, StringComparison.Ordinal))
        {
            return false;
        }

        SettingsPreset = preset;
        return true;
    }

    /// <summary>The user chose a preset in Settings; it applies to the chat.</summary>
    internal void ChoosePresetInSettings(AgentPermissionPreset preset)
    {
        SettingsPreset = preset;
        ChatPresetChosen?.Invoke(preset);
    }

    /// <summary>
    /// The theme button flips the plain pair; Light and a themed dark (Meteor Shower,
    /// Starry Night) both go to plain Dark.
    /// </summary>
    internal void ToggleTheme() => ChooseTheme(Theme == UiTheme.Dark ? UiTheme.Light : UiTheme.Dark);

    internal void ChooseTheme(UiTheme theme)
    {
        Theme = theme;
        _themes.TrySave(theme);
    }

    /// <summary>
    /// Settings were saved: the catalogs are stale and the chat's provider and model
    /// follow the saved selection. Overlapping reloads apply only the newest read.
    /// </summary>
    internal async Task ReloadAfterSettingsSavedAsync()
    {
        _backend.InvalidateCatalogs();
        long reload = ++_selectionReload;
        try
        {
            var settings = await _backend.GetSettingsAsync();
            if (settings is null || reload != _selectionReload)
            {
                return;
            }

            string backend = settings.BackendChoice ?? string.Empty;
            _backend.SelectCatalogModel(backend, ProviderModelSelection.GetSelectedModel(settings, backend));
        }
        catch (Exception ex)
        {
            GuiDiagnosticWriter.AppendTraceLine($"[Shell] Reload provider selection failed: {ex.Message}");
        }
    }

    internal void SetUpdateAvailable(bool available) => IsUpdateAvailable = available;

    /// <summary>Enabled only while the side panel can show, and worded for the action it will take.</summary>
    internal void SetSidePanelState(bool canToggle, bool isHidden)
    {
        CanToggleSidePanel = canToggle;
        IsSidePanelHidden = isHidden;
    }

    /// <summary>The window closed: no reconnect, health poll or status write follows.</summary>
    internal void Close()
    {
        _closed = true;
        StopReconnect();
        _health.SetActive(false);
    }

    public async ValueTask DisposeAsync()
    {
        Close();
        _backend.BackendExited -= OnBackendExited;
        await _health.DisposeAsync();
    }

    private void ReportReady(BackendSession session) =>
        SetStatus($"{_backend.DisplayName} Backend ready (127.0.0.1:{session.HttpBaseUri.Port})", ShellStatusTone.Ready);

    private void SetStatus(string text, ShellStatusTone tone)
    {
        StatusText = text;
        StatusTone = tone;
    }

    private void SetStartup(ShellStartupPhase phase, string welcomeText)
    {
        WelcomeText = welcomeText;
        StartupPhase = phase;
    }
}
