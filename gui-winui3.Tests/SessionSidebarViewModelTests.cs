using RuApp.Models;
using RuApp.Services;
using RuApp.ViewModels;
using Xunit;

namespace RuApp.Tests;

public sealed class SessionSidebarViewModelTests : IDisposable
{
    private const string AppRoot = @"D:\LLM";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-sidebar-vm-" + Guid.NewGuid().ToString("N"));

    public SessionSidebarViewModelTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Section_chrome_renders_before_any_session_arrives()
    {
        var viewModel = NewViewModel(new FakeSessions());

        viewModel.SetAppRoot(AppRoot);

        Assert.NotEmpty(viewModel.Rows);
        Assert.Contains(viewModel.Rows, row => row is SidebarSectionRow);
        Assert.Empty(viewModel.Sessions);
    }

    [Fact]
    public async Task A_refresh_merges_rows_in_place_and_an_unchanged_poll_does_not_rerender()
    {
        var backend = new FakeSessions(Session("a", "First"), Session("b", "Second"));
        var viewModel = NewViewModel(backend);
        viewModel.SetAppRoot(AppRoot);
        viewModel.SetRefreshActive(true);
        Assert.True(await viewModel.RefreshAsync());
        var first = viewModel.Sessions.Single(session => session.Id == "a");
        int renders = 0;
        viewModel.Rendered += () => renders++;

        Assert.True(await viewModel.RefreshAsync());
        Assert.Equal(0, renders);

        backend.Rows[0] = Session("a", "Renamed");
        Assert.True(await viewModel.RefreshAsync());
        Assert.Same(first, viewModel.Sessions.Single(session => session.Id == "a"));
        Assert.Equal("Renamed", first.Title);
        Assert.Equal(1, renders);
    }

    [Fact]
    public async Task Delete_removes_the_row_as_soon_as_the_host_confirms()
    {
        var backend = new FakeSessions(Session("a", "First"), Session("b", "Second"));
        var viewModel = NewViewModel(backend);
        viewModel.SetAppRoot(AppRoot);
        viewModel.SetRefreshActive(true);
        await viewModel.RefreshAsync();
        bool removedBeforeRefresh = false;
        backend.OnList = () => removedBeforeRefresh = viewModel.Sessions.All(session => session.Id != "a");
        backend.Remove("a");

        var result = await viewModel.DeleteAsync("a", openSessionId: "b", new FakePrompts());

        Assert.True(result.IsRemoved);
        Assert.True(removedBeforeRefresh);
        Assert.IsType<SessionDeletionFollowUp.Nothing>(result.FollowUp);
        Assert.Equal(["b"], viewModel.Sessions.Select(session => session.Id));
    }

    [Fact]
    public async Task Deleting_the_open_session_opens_the_newest_remaining_one()
    {
        var backend = new FakeSessions(Session("a", "First"), Session("b", "Second"));
        var viewModel = NewViewModel(backend);
        viewModel.SetRefreshActive(true);
        await viewModel.RefreshAsync();
        backend.Remove("a");

        var result = await viewModel.DeleteAsync("a", openSessionId: "a", new FakePrompts());

        var open = Assert.IsType<SessionDeletionFollowUp.OpenSession>(result.FollowUp);
        Assert.Equal("b", open.Session.Id);
    }

    [Fact]
    public async Task Deleting_the_last_session_starts_a_new_chat()
    {
        var backend = new FakeSessions(Session("a", "Only"));
        var viewModel = NewViewModel(backend);
        viewModel.SetRefreshActive(true);
        await viewModel.RefreshAsync();
        backend.Remove("a");

        var result = await viewModel.DeleteAsync("a", openSessionId: "a", new FakePrompts());

        Assert.IsType<SessionDeletionFollowUp.StartNewChat>(result.FollowUp);
    }

    [Fact]
    public async Task A_declined_or_failed_delete_keeps_the_row()
    {
        var backend = new FakeSessions(Session("a", "First"));
        var viewModel = NewViewModel(backend);
        viewModel.SetRefreshActive(true);
        await viewModel.RefreshAsync();

        var declined = await viewModel.DeleteAsync("a", null, new FakePrompts { Confirm = false });
        Assert.False(declined.IsRemoved);
        Assert.Empty(backend.Deleted);

        backend.DeleteOutcome = new ApiService.SessionDeletionOutcome(false, false, "Không xoá được session, hãy thử lại.");
        var prompts = new FakePrompts();
        var failed = await viewModel.DeleteAsync("a", null, prompts);

        Assert.False(failed.IsRemoved);
        Assert.Equal("Không xoá được session, hãy thử lại.", prompts.FailureMessage);
        Assert.Single(viewModel.Sessions);
    }

    [Fact]
    public async Task A_partial_delete_counts_as_removed_and_retries_only_while_still_partial()
    {
        var backend = new FakeSessions(Session("a", "First"), Session("b", "Second"));
        var viewModel = NewViewModel(backend);
        viewModel.SetRefreshActive(true);
        await viewModel.RefreshAsync();
        backend.Remove("a");
        backend.DeleteOutcome = new ApiService.SessionDeletionOutcome(false, true, "artifacts left");
        var prompts = new FakePrompts { RetryAnswers = new Queue<bool>([true, true]) };

        var result = await viewModel.DeleteAsync("a", "b", prompts);
        Assert.True(result.IsRemoved);
        Assert.True(result.IsPartial);
        Assert.DoesNotContain(viewModel.Sessions, session => session.Id == "a");

        // First retry is still partial, the second can no longer say "partial": unconfirmed.
        backend.DeleteOutcomes.Enqueue(new ApiService.SessionDeletionOutcome(false, true, "still left"));
        backend.DeleteOutcomes.Enqueue(new ApiService.SessionDeletionOutcome(false, false, "Session không tồn tại."));
        await viewModel.RetryCleanupAsync("a", result.PartialMessage, prompts);

        Assert.Equal(["artifacts left", "still left"], prompts.RetryMessages);
        Assert.True(prompts.ShowedUnconfirmed);
        Assert.Equal(3, backend.Deleted.Count);
    }

    [Fact]
    public async Task An_empty_draft_is_visible_only_while_open_and_a_started_one_stays()
    {
        var backend = new FakeSessions(Session("old", "Existing"));
        var viewModel = NewViewModel(backend);
        viewModel.SetAppRoot(AppRoot);
        viewModel.SetRefreshActive(true);
        await viewModel.RefreshAsync();

        var draft = await viewModel.CreateDraftAsync(SidebarSessionOrganization.RecentsKey, AppRoot);
        Assert.NotNull(draft);
        Assert.Equal(SessionDraftPresentation.PlaceholderTitle, viewModel.Sessions[0].Title);
        Assert.Equal(draft!.Id, viewModel.ActiveSessionId);

        // Leaving an unstarted draft hides it (the Host record is not deleted).
        viewModel.Select(viewModel.Sessions.Single(session => session.Id == "old"));
        Assert.DoesNotContain(viewModel.Sessions, session => session.Id == draft.Id);
        Assert.Empty(backend.Deleted);

        var started = await viewModel.CreateDraftAsync(SidebarSessionOrganization.RecentsKey, AppRoot);
        viewModel.MarkStarted(started!.Id, "  fix   the build please ");
        viewModel.Select(viewModel.Sessions.Single(session => session.Id == "old"));
        var kept = viewModel.Sessions.Single(session => session.Id == started.Id);
        Assert.Equal("fix the build please", kept.Title);
    }

    [Fact]
    public async Task A_create_overtaken_by_a_newer_navigation_is_dropped()
    {
        var backend = new FakeSessions(Session("old", "Existing"));
        var gate = new TaskCompletionSource<SessionInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.CreateHook = () => gate.Task;
        var viewModel = NewViewModel(backend);
        viewModel.SetRefreshActive(true);
        await viewModel.RefreshAsync();

        var pending = viewModel.CreateDraftAsync(null, null);
        viewModel.Select(viewModel.Sessions.Single());
        gate.SetResult(Session("late", string.Empty));

        Assert.Null(await pending);
        Assert.DoesNotContain(viewModel.Sessions, session => session.Id == "late");
    }

    [Fact]
    public async Task A_failed_create_reports_a_status()
    {
        var backend = new FakeSessions { CreateHook = () => Task.FromResult<SessionInfo?>(null) };
        var viewModel = NewViewModel(backend);
        string? status = null;
        viewModel.ActionFailed += message => status = message;

        Assert.Null(await viewModel.CreateDraftAsync(null, null));
        Assert.Equal("Could not create a new chat session", status);
    }

    [Fact]
    public async Task Selecting_a_session_falls_back_to_the_workspace_recorded_for_its_draft()
    {
        var backend = new FakeSessions();
        var viewModel = NewViewModel(backend);
        viewModel.SetAppRoot(AppRoot);
        var draft = await viewModel.CreateDraftAsync("group", @"D:\work\api");
        draft!.WorkspacePath = null;

        Assert.Equal(@"D:\work\api", viewModel.Select(draft));
    }

    [Fact]
    public async Task A_cli_session_can_only_move_within_its_own_group()
    {
        var cli = Session("cli", "Terminal run");
        cli.ClientSource = "cli";
        var backend = new FakeSessions(cli, Session("gui", "Desktop"));
        var viewModel = NewViewModel(backend);
        viewModel.SetAppRoot(AppRoot);
        viewModel.SetRefreshActive(true);
        await viewModel.RefreshAsync();

        Assert.DoesNotContain(viewModel.MoveTargets("cli"), target => target.Text == "Recents");
        Assert.Contains(viewModel.MoveTargets("gui"), target => target.Text == "Recents");
    }

    [Fact]
    public async Task Moving_into_a_group_expands_it_and_keeps_the_open_session_in_it()
    {
        var backend = new FakeSessions(Session("a", "First"), Session("b", "Second"));
        var viewModel = NewViewModel(backend);
        viewModel.SetAppRoot(AppRoot);
        viewModel.SetRefreshActive(true);
        await viewModel.RefreshAsync();
        viewModel.Select(viewModel.Sessions.Single(session => session.Id == "a"));
        viewModel.Pin(@"D:\work\api");
        var group = viewModel.Groups.Single(candidate => candidate.Path is not null
            && candidate.Path.EndsWith("api", StringComparison.OrdinalIgnoreCase));

        Assert.True(viewModel.Move("a", group.Key, group.Path, null));

        Assert.Equal(group.Key, viewModel.SelectedWorkspaceKey);
        Assert.Contains(viewModel.Groups.Single(candidate => candidate.Key == group.Key).Sessions, session => session.Id == "a");
    }

    [Fact]
    public async Task Section_folding_keeps_all_sessions_and_selecting_one_restores_its_group()
    {
        var first = Session("a", "First");
        var backend = new FakeSessions(first, Session("b", "Second"));
        await using var vm = NewViewModel(backend);
        vm.SetAppRoot(AppRoot);
        vm.SetRefreshActive(true);
        await vm.RefreshAsync();
        Assert.Equal(2, vm.Rows.OfType<SessionInfo>().Count());
        vm.ToggleSection(SidebarSessionOrganization.RecentsKey);
        Assert.Empty(vm.Rows.OfType<SessionInfo>());
        Assert.Equal(2, vm.Sessions.Count);
        vm.Select(first);
        Assert.Equal(2, vm.Rows.OfType<SessionInfo>().Count());
    }

    private SessionSidebarViewModel NewViewModel(FakeSessions backend) =>
        new(backend,
            action => action(),
            new SidebarOrganizationStore(Path.Combine(_root, "organization.json")),
            new WorkspaceGroupPreferenceStore(Path.Combine(_root, "groups.json")),
            new WorkspacePinStore(Path.Combine(_root, "pins.json")),
            new SessionWorkspaceMapStore(Path.Combine(_root, "workspaces.json")));

    private static SessionInfo Session(string id, string title) => new()
    {
        Id = id,
        Title = title,
        MessageCount = 2,
        CreatedAt = "2026-10-20T00:00:00Z",
        UpdatedAt = "2026-10-20T00:00:00Z",
    };

    private sealed class FakeSessions(params SessionInfo[] rows) : ISessionListBackend
    {
        private int _created;

        public List<SessionInfo> Rows { get; } = rows.ToList();

        public List<string> Deleted { get; } = new();

        public ApiService.SessionDeletionOutcome DeleteOutcome { get; set; } = new(true, false, null);

        public Queue<ApiService.SessionDeletionOutcome> DeleteOutcomes { get; } = new();

        public Func<Task<SessionInfo?>>? CreateHook { get; set; }

        public Action? OnList { get; set; }

        public void Remove(string id) => Rows.RemoveAll(row => row.Id == id);

        public Task<SessionListResponse?> GetSessionsAsync(CancellationToken cancellationToken)
        {
            OnList?.Invoke();
            // Fresh objects each time, as the wire would deliver.
            var copy = Rows.Select(row => new SessionInfo
            {
                Id = row.Id, Title = row.Title, MessageCount = row.MessageCount, CreatedAt = row.CreatedAt,
                UpdatedAt = row.UpdatedAt, ClientSource = row.ClientSource, WorkspacePath = row.WorkspacePath,
            }).ToList();
            return Task.FromResult<SessionListResponse?>(new SessionListResponse { Sessions = copy });
        }

        public Task<SessionInfo?> CreateSessionAsync() =>
            CreateHook?.Invoke() ?? Task.FromResult<SessionInfo?>(new SessionInfo { Id = "draft" + ++_created });

        public Task<ApiService.SessionDeletionOutcome> DeleteSessionAsync(string sessionId)
        {
            Deleted.Add(sessionId);
            return Task.FromResult(DeleteOutcomes.Count > 0 ? DeleteOutcomes.Dequeue() : DeleteOutcome);
        }
    }

    private sealed class FakePrompts : ISessionDeletionPrompts
    {
        public bool Confirm { get; set; } = true;

        public Queue<bool> RetryAnswers { get; set; } = new();

        public string? FailureMessage { get; private set; }

        public List<string?> RetryMessages { get; } = new();

        public bool ShowedUnconfirmed { get; private set; }

        public Task<bool> ConfirmDeleteAsync() => Task.FromResult(Confirm);

        public Task ShowDeleteFailedAsync(string? message)
        {
            FailureMessage = message;
            return Task.CompletedTask;
        }

        public Task<bool> OfferCleanupRetryAsync(string? message)
        {
            RetryMessages.Add(message);
            return Task.FromResult(RetryAnswers.Count > 0 && RetryAnswers.Dequeue());
        }

        public Task ShowCleanupUnconfirmedAsync()
        {
            ShowedUnconfirmed = true;
            return Task.CompletedTask;
        }
    }

}
