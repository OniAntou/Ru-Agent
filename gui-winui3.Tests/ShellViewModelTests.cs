using RuAgent.Protocol.Harness;
using RuApp.Models;
using RuApp.Services;
using RuApp.ViewModels;
using Xunit;

namespace RuApp.Tests;

public sealed class ShellViewModelTests : IDisposable
{
    private const string Root = @"D:\LLM";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-shell-vm-" + Guid.NewGuid().ToString("N"));

    public ShellViewModelTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task The_project_root_is_published_before_the_host_starts()
    {
        var backend = new FakeShellBackend();
        var shell = NewShell(backend);
        shell.ProjectRootResolved += root => backend.Calls.Add("root:" + root);
        var statuses = new List<string>();
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.StatusText)) statuses.Add(shell.StatusText);
        };

        var session = await shell.StartAsync(CancellationToken.None);

        Assert.NotNull(session);
        Assert.Equal(["root:" + Root, "start:" + Root], backend.Calls);
        Assert.Equal(["Starting .NET Backend...", ".NET Backend ready (127.0.0.1:5123)"], statuses);
        Assert.Equal(ShellStatusTone.Ready, shell.StatusTone);
        Assert.Equal(ShellStartupPhase.Ready, shell.StartupPhase);
        Assert.True(shell.IsBackendLive);
    }

    [Fact]
    public async Task A_missing_project_root_does_not_start_the_host()
    {
        var backend = new FakeShellBackend { ProjectRoot = null };
        var shell = NewShell(backend);
        bool published = false;
        shell.ProjectRootResolved += _ => published = true;

        Assert.Null(await shell.StartAsync(CancellationToken.None));

        Assert.False(published);
        Assert.Empty(backend.Calls);
        Assert.Equal(ShellStartupPhase.RootMissing, shell.StartupPhase);
        Assert.Equal("Project root not found. Click to continue.", shell.WelcomeText);
        Assert.Equal(ShellStatusTone.Danger, shell.StatusTone);
        Assert.False(shell.IsBackendLive);
    }

    [Fact]
    public async Task A_host_that_does_not_start_is_reported_and_the_polls_stay_down()
    {
        var backend = new FakeShellBackend();
        backend.Sessions.Enqueue(null);
        var shell = NewShell(backend);

        Assert.Null(await shell.StartAsync(CancellationToken.None));

        Assert.Equal("Could not connect to .NET Backend!", shell.StatusText);
        Assert.Equal(ShellStatusTone.Danger, shell.StatusTone);
        Assert.Equal(ShellStartupPhase.Failed, shell.StartupPhase);
        Assert.False(shell.IsBackendLive);
    }

    [Theory]
    [InlineData(true, null, false, "System ready", ShellStatusTone.Ready)]
    [InlineData(true, null, true, "Harness ready", ShellStatusTone.Ready)]
    [InlineData(false, "model_missing", false, "Backend error (model_missing)", ShellStatusTone.Warning)]
    [InlineData(false, null, false, "Backend starting or not ready yet", ShellStatusTone.Warning)]
    public void A_running_host_reports_its_health_on_the_status_line(
        bool healthy, string? error, bool harness, string text, ShellStatusTone tone)
    {
        var shell = NewShell(new FakeShellBackend { IsHarness = harness });

        shell.ApplyHealth(new HealthStatusSnapshot(BackendRunning: true, healthy, error));

        Assert.Equal(text, shell.StatusText);
        Assert.Equal(tone, shell.StatusTone);
        Assert.False(shell.IsReconnectScheduled);
    }

    [Fact]
    public async Task A_host_that_stopped_answering_schedules_one_reconnect_and_a_running_report_cancels_it()
    {
        var delays = new ControlledDelay();
        var shell = NewShell(new FakeShellBackend(), delays.Delay);
        await shell.StartAsync(CancellationToken.None);

        shell.ApplyHealth(new HealthStatusSnapshot(false, false, null));
        shell.ApplyHealth(new HealthStatusSnapshot(false, false, null));

        Assert.Equal("Could not connect to .NET Backend!", shell.StatusText);
        Assert.Equal(ShellStatusTone.Danger, shell.StatusTone);
        Assert.True(shell.IsReconnectScheduled);
        Assert.Equal(1, delays.Started);
        Assert.Equal(UiTiming.ReconnectPoll, delays.LastInterval);

        shell.ApplyHealth(new HealthStatusSnapshot(true, true, null));

        Assert.False(shell.IsReconnectScheduled);
        Assert.True(delays.LastToken.IsCancellationRequested);
    }

    [Fact]
    public async Task A_reconnect_retries_until_the_host_is_back_then_rebinds_and_stops()
    {
        var backend = new FakeShellBackend();
        backend.Sessions.Enqueue(BackendSession.FromLoopback(5123, "synthetic"));
        backend.Sessions.Enqueue(null);
        backend.Sessions.Enqueue(BackendSession.FromLoopback(6200, "synthetic"));
        backend.Health = new HealthStatusSnapshot(true, true, null);
        var shell = NewShell(backend, (_, _) => Task.CompletedTask);
        await shell.StartAsync(CancellationToken.None);
        shell.SetHealthActive(true);
        var roots = new List<string>();
        shell.ProjectRootResolved += roots.Add;

        shell.ScheduleReconnect();
        await backend.Rebound.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => shell.StatusText == "System ready");

        Assert.Equal(3, backend.Calls.Count(call => call.StartsWith("start:", StringComparison.Ordinal)));
        Assert.Equal(1, backend.RebindCount);
        Assert.Equal([Root], roots);
        Assert.True(shell.IsBackendLive);
        Assert.False(shell.IsReconnectScheduled);
    }

    [Fact]
    public async Task A_rebind_that_fails_still_ends_the_retries()
    {
        var backend = new FakeShellBackend { RebindFailure = new InvalidOperationException("views") };
        var shell = NewShell(backend, (_, _) => Task.CompletedTask);
        await shell.StartAsync(CancellationToken.None);

        shell.ScheduleReconnect();

        Assert.Equal(1, backend.RebindCount);
        Assert.False(shell.IsReconnectScheduled);
        Assert.Equal(2, backend.Calls.Count(call => call.StartsWith("start:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_harness_run_or_a_closed_window_never_reconnects()
    {
        var harness = NewShell(new FakeShellBackend { IsHarness = true }, new ControlledDelay().Delay);
        await harness.StartAsync(CancellationToken.None);
        harness.ApplyHealth(new HealthStatusSnapshot(false, false, null));
        Assert.False(harness.IsReconnectScheduled);

        var closed = NewShell(new FakeShellBackend(), new ControlledDelay().Delay);
        await closed.StartAsync(CancellationToken.None);
        closed.Close();
        closed.ScheduleReconnect();
        Assert.False(closed.IsReconnectScheduled);
    }

    [Fact]
    public async Task Closing_the_window_cancels_a_scheduled_reconnect()
    {
        var delays = new ControlledDelay();
        var shell = NewShell(new FakeShellBackend(), delays.Delay);
        await shell.StartAsync(CancellationToken.None);
        shell.ScheduleReconnect();

        shell.Close();

        Assert.False(shell.IsReconnectScheduled);
        Assert.True(delays.LastToken.IsCancellationRequested);
    }

    [Fact]
    public async Task A_host_exit_takes_the_polls_down_and_schedules_a_reconnect()
    {
        var backend = new FakeShellBackend();
        var delays = new ControlledDelay();
        var shell = NewShell(backend, delays.Delay);
        await shell.StartAsync(CancellationToken.None);

        backend.RaiseExited();

        Assert.False(shell.IsBackendLive);
        Assert.Equal(".NET Backend stopped. Reconnecting...", shell.StatusText);
        Assert.Equal(ShellStatusTone.Danger, shell.StatusTone);
        Assert.True(shell.IsReconnectScheduled);

        await shell.DisposeAsync();
        backend.RaiseExited();
        Assert.False(shell.IsReconnectScheduled);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_host_that_dies_during_a_failed_start_leaves_the_failure_and_never_reconnects(bool rootFound)
    {
        var backend = new FakeShellBackend { ProjectRoot = rootFound ? Root : null };
        backend.Sessions.Enqueue(null);
        var delays = new ControlledDelay();
        var shell = NewShell(backend, delays.Delay);
        await shell.StartAsync(CancellationToken.None);
        string failure = shell.StatusText;

        backend.RaiseExited();
        shell.ApplyHealth(new HealthStatusSnapshot(false, false, null));
        shell.ScheduleReconnect();

        Assert.Equal(0, delays.Started);
        Assert.False(shell.IsReconnectScheduled);
        Assert.False(shell.IsBackendLive);
        Assert.Equal(rootFound ? ShellStartupPhase.Failed : ShellStartupPhase.RootMissing, shell.StartupPhase);
        Assert.Contains(rootFound ? "Could not connect" : "Project root not found", failure, StringComparison.Ordinal);
        Assert.DoesNotContain("Reconnecting", shell.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_poll_that_lands_while_the_previous_one_runs_is_skipped()
    {
        var shell = NewShell(new FakeShellBackend());
        var sessions = new TaskCompletionSource();
        int refreshes = 0;
        Task Refresh()
        {
            refreshes++;
            return sessions.Task;
        }

        var first = shell.PollAsync(Refresh);
        Assert.False(await shell.PollAsync(Refresh));
        sessions.SetResult();

        Assert.True(await first);
        Assert.Equal(1, refreshes);
        Assert.True(await shell.PollAsync(() => throw new InvalidOperationException("list")));
    }

    [Fact]
    public void Every_chat_navigation_keeps_catalog_polling_and_other_screens_leave_the_chat()
    {
        var shell = NewShell(new FakeShellBackend());

        var first = shell.Navigate("chat");
        var again = shell.Navigate("chat");
        Assert.Equal(first, again);
        Assert.True(again.KeepsCatalogPolling);
        Assert.False(again.LeavesChat);

        var log = shell.Navigate("log");
        Assert.Equal(ShellDestination.Log, shell.Destination);
        Assert.True(log.LeavesChat);
        Assert.False(log.KeepsCatalogPolling);
        Assert.True(log.IsLogActive);
        Assert.False(log.IsDiagnosticsActive);

        var unknown = shell.Navigate("no-such-screen");
        Assert.Equal(ShellDestination.Chat, unknown.Destination);
        Assert.True(unknown.KeepsCatalogPolling);
    }

    [Fact]
    public void Opening_settings_shows_the_chats_preset_even_after_the_chat_narrowed_it()
    {
        var shell = NewShell(new FakeShellBackend());
        AgentPermissionPreset? applied = null;
        shell.ChatPresetChosen += preset => applied = preset;

        shell.ChoosePresetInSettings(AgentPermissionPreset.FullAccess);
        Assert.Equal(AgentPermissionPreset.FullAccess, applied);
        Assert.Equal(AgentPermissionPreset.FullAccess, shell.SettingsPreset);

        // New Chat narrows Full Access in the chat without an adoption event.
        Assert.True(shell.OpenSettings(AgentPermissionPreset.Standard));
        Assert.Equal(AgentPermissionPreset.Standard, shell.SettingsPreset);
        Assert.True(shell.IsSettingsOpen);
        Assert.False(shell.OpenSettings(AgentPermissionPreset.Standard));

        Assert.True(shell.CloseSettings());
        Assert.False(shell.CloseSettings());
        Assert.False(shell.IsSettingsOpen);
    }

    [Fact]
    public void An_adopted_preset_repaints_settings_only_for_the_session_still_active()
    {
        var shell = NewShell(new FakeShellBackend());

        Assert.False(shell.AdoptSessionPreset("a", AgentPermissionPreset.Plan, activeSessionId: "b"));
        Assert.Equal(AgentPermissionPreset.Standard, shell.SettingsPreset);
        Assert.False(shell.AdoptSessionPreset("a", AgentPermissionPreset.Plan, activeSessionId: null));

        Assert.True(shell.AdoptSessionPreset("b", AgentPermissionPreset.Plan, activeSessionId: "b"));
        Assert.Equal(AgentPermissionPreset.Plan, shell.SettingsPreset);
    }

    [Theory]
    [InlineData(UiTheme.Dark, UiTheme.Light)]
    [InlineData(UiTheme.Light, UiTheme.Dark)]
    [InlineData(UiTheme.DarkMeteorShower, UiTheme.Dark)]
    public void The_theme_button_flips_the_plain_pair_and_persists(UiTheme from, UiTheme to)
    {
        var store = new UiThemePreferenceStore(Path.Combine(_root, "ui-preferences.json"));
        store.TrySave(from);
        var shell = NewShell(new FakeShellBackend(), themes: store);
        Assert.Equal(from, shell.Theme);

        shell.ToggleTheme();

        Assert.Equal(to, shell.Theme);
        Assert.Equal(to, store.Load());
        bool dark = to != UiTheme.Light;
        Assert.Equal(dark ? "Switch to light mode" : "Switch to dark mode", shell.ThemeToggleLabel);
        Assert.Equal(dark ? "\uE793" : "\uE706", shell.ThemeToggleGlyph);
    }

    [Fact]
    public async Task Saved_settings_invalidate_the_catalogs_and_only_the_newest_reload_applies()
    {
        var backend = new FakeShellBackend();
        var shell = NewShell(backend);
        var older = new TaskCompletionSource<AppSettingsDto?>();
        var newer = new TaskCompletionSource<AppSettingsDto?>();
        backend.SettingsReads.Enqueue(older);
        backend.SettingsReads.Enqueue(newer);

        var first = shell.ReloadAfterSettingsSavedAsync();
        var second = shell.ReloadAfterSettingsSavedAsync();
        newer.SetResult(new AppSettingsDto { BackendChoice = "anthropic", AnthropicModel = "claude-new" });
        await second;
        older.SetResult(new AppSettingsDto { BackendChoice = "openai", OpenAiModel = "gpt-old" });
        await first;

        Assert.Equal(2, backend.Invalidations);
        Assert.Equal([("anthropic", "claude-new")], backend.Selections);
    }

    [Fact]
    public void The_title_bar_entries_follow_settings_updates_and_the_side_panel()
    {
        var shell = NewShell(new FakeShellBackend());
        Assert.False(shell.IsSettingsReady);
        Assert.False(shell.IsUpdateAvailable);
        Assert.False(shell.CanToggleSidePanel);
        Assert.Equal("Show side panel", shell.SidePanelToggleLabel);

        shell.MarkSettingsReady();
        shell.SetUpdateAvailable(true);
        shell.SetSidePanelState(canToggle: true, isHidden: false);

        Assert.True(shell.IsSettingsReady);
        Assert.True(shell.IsUpdateAvailable);
        Assert.True(shell.CanToggleSidePanel);
        Assert.Equal("Hide side panel", shell.SidePanelToggleLabel);
    }

    [Fact]
    public void A_reported_error_takes_the_status_line()
    {
        var shell = NewShell(new FakeShellBackend());

        shell.ReportError("Could not open the chat");

        Assert.Equal("Could not open the chat", shell.StatusText);
        Assert.Equal(ShellStatusTone.Danger, shell.StatusTone);
    }

    private ShellViewModel NewShell(
        FakeShellBackend backend,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        UiThemePreferenceStore? themes = null) =>
        new(
            backend,
            action => action(),
            delay ?? new ControlledDelay().Delay,
            themes ?? new UiThemePreferenceStore(Path.Combine(_root, "ui-preferences.json")));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private sealed class ControlledDelay
    {
        public int Started { get; private set; }
        public TimeSpan LastInterval { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public Task Delay(TimeSpan interval, CancellationToken token)
        {
            Started++;
            LastInterval = interval;
            LastToken = token;
            return Task.Delay(Timeout.Infinite, token);
        }
    }

    private sealed class FakeShellBackend : IShellBackend
    {
        public string DisplayName => ".NET";
        public bool IsHarness { get; init; }
        public string? ProjectRoot { get; init; } = Root;
        public List<string> Calls { get; } = new();
        public Queue<BackendSession?> Sessions { get; } = new();
        public Queue<TaskCompletionSource<AppSettingsDto?>> SettingsReads { get; } = new();
        public List<(string Backend, string Model)> Selections { get; } = new();
        public HealthStatusSnapshot? Health { get; set; }
        public Exception? RebindFailure { get; init; }
        public int RebindCount { get; private set; }
        public int Invalidations { get; private set; }
        public TaskCompletionSource Rebound { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler? BackendExited;

        public void RaiseExited() => BackendExited?.Invoke(this, EventArgs.Empty);

        public string? ResolveProjectRoot() => ProjectRoot;

        public Task<BackendSession?> StartAsync(string projectRoot, CancellationToken cancellationToken)
        {
            Calls.Add("start:" + projectRoot);
            return Task.FromResult(Sessions.Count > 0 ? Sessions.Dequeue() : BackendSession.FromLoopback(5123, "synthetic"));
        }

        public Task RebindAsync(BackendSession session)
        {
            RebindCount++;
            Rebound.TrySetResult();
            return RebindFailure is null ? Task.CompletedTask : Task.FromException(RebindFailure);
        }

        public Task<HealthStatusSnapshot?> GatherHealthAsync(CancellationToken cancellationToken) => Task.FromResult(Health);

        public void InvalidateCatalogs() => Invalidations++;

        public Task<AppSettingsDto?> GetSettingsAsync() => SettingsReads.Dequeue().Task;

        public void SelectCatalogModel(string backend, string model) => Selections.Add((backend, model));
    }
}
