using System.Collections.Concurrent;
using RuApp.Models;
using RuApp.Services;
using RuApp.ViewModels;
using Xunit;

namespace RuApp.Tests;

public sealed partial class SessionSidebarViewModelTests
{
    [Fact]
    public async Task Workspace_delete_clears_the_active_session_when_the_current_chat_is_deleted()
    {
        var active = Session("a", "Active");
        active.WorkspacePath = @"D:\work\app";
        var backend = new FakeSessions(active);
        var viewModel = NewViewModel(backend);
        viewModel.SetRefreshActive(true);
        Assert.True(await viewModel.RefreshLatestAsync());
        viewModel.NoteOpened("a");
        var plan = await viewModel.PlanWorkspaceDeletionAsync(SidebarSessionOrganization.PathKey(@"D:\work\app"));

        var result = await viewModel.DeleteWorkspaceSessionsAsync(plan!);

        Assert.Null(viewModel.ActiveSessionId);
        Assert.DoesNotContain(viewModel.Sessions, row => row.Id == "a");
        Assert.IsType<SessionDeletionFollowUp.StartNewChat>(result.FollowUp);
    }

    [Fact]
    public async Task Workspace_delete_preserves_navigation_to_a_session_outside_the_deleted_group()
    {
        var deleted = Session("a", "Deleted");
        deleted.WorkspacePath = @"D:\work\app";
        var surviving = Session("b", "Surviving");
        surviving.WorkspacePath = @"D:\work\other";
        var backend = new FakeSessions(deleted, surviving);
        var viewModel = NewViewModel(backend);
        viewModel.SetRefreshActive(true);
        Assert.True(await viewModel.RefreshLatestAsync());
        viewModel.NoteOpened("a");
        var plan = await viewModel.PlanWorkspaceDeletionAsync(SidebarSessionOrganization.PathKey(@"D:\work\app"));

        // Models the user switching chats while the delete confirmation is open.
        viewModel.NoteOpened("b");
        var result = await viewModel.DeleteWorkspaceSessionsAsync(plan!);

        Assert.Equal("b", viewModel.ActiveSessionId);
        Assert.Contains(viewModel.Sessions, row => row.Id == "b");
        Assert.IsType<SessionDeletionFollowUp.Nothing>(result.FollowUp);
    }

    [Fact]
    public async Task Workspace_delete_returns_the_next_session_when_it_removes_the_active_chat()
    {
        var active = Session("a", "Deleted");
        active.WorkspacePath = @"D:\work\app";
        var next = Session("b", "Next");
        next.WorkspacePath = @"D:\work\other";
        var backend = new FakeSessions(active, next);
        var viewModel = NewViewModel(backend);
        viewModel.SetRefreshActive(true);
        Assert.True(await viewModel.RefreshLatestAsync());
        viewModel.NoteOpened("a");
        var plan = await viewModel.PlanWorkspaceDeletionAsync(SidebarSessionOrganization.PathKey(@"D:\work\app"));

        var result = await viewModel.DeleteWorkspaceSessionsAsync(plan!);

        var followUp = Assert.IsType<SessionDeletionFollowUp.OpenSession>(result.FollowUp);
        Assert.Equal("b", followUp.Session.Id);
        Assert.Null(viewModel.ActiveSessionId);
    }

    [Fact]
    public async Task Workspace_delete_returns_follow_up_when_navigation_moves_to_another_chat_being_deleted()
    {
        var first = Session("a", "First");
        first.WorkspacePath = @"D:\work\app";
        var second = Session("b", "Second");
        second.WorkspacePath = @"D:\work\app";
        var backend = new FakeSessions(first, second);
        var viewModel = NewViewModel(backend);
        viewModel.SetRefreshActive(true);
        Assert.True(await viewModel.RefreshLatestAsync());
        viewModel.NoteOpened("a");
        var plan = await viewModel.PlanWorkspaceDeletionAsync(SidebarSessionOrganization.PathKey(@"D:\work\app"));
        backend.OnDelete = sessionId =>
        {
            if (sessionId == "b") viewModel.NoteOpened("b");
        };

        var result = await viewModel.DeleteWorkspaceSessionsAsync(plan!);

        Assert.Null(viewModel.ActiveSessionId);
        Assert.IsType<SessionDeletionFollowUp.StartNewChat>(result.FollowUp);
    }

    [Fact]
    public async Task Workspace_delete_suppresses_follow_up_when_navigation_changes_during_refresh()
    {
        var active = Session("a", "Deleted");
        active.WorkspacePath = @"D:\work\app";
        var surviving = Session("b", "Surviving");
        surviving.WorkspacePath = @"D:\work\other";
        var backend = new FakeSessions(active, surviving);
        var viewModel = NewViewModel(backend);
        viewModel.SetRefreshActive(true);
        Assert.True(await viewModel.RefreshLatestAsync());
        viewModel.NoteOpened("a");
        var plan = await viewModel.PlanWorkspaceDeletionAsync(SidebarSessionOrganization.PathKey(@"D:\work\app"));
        bool duringRefresh = false;
        backend.OnList = () =>
        {
            if (duringRefresh) viewModel.NoteOpened("b");
        };
        duringRefresh = true;

        var result = await viewModel.DeleteWorkspaceSessionsAsync(plan!);

        Assert.Equal("b", viewModel.ActiveSessionId);
        Assert.IsType<SessionDeletionFollowUp.Nothing>(result.FollowUp);
    }

    [Fact]
    public async Task Workspace_delete_plans_include_matching_sessions_after_the_first_page()
    {
        var rows = Enumerable.Range(0, 101)
            .Select(index => Session(index == 100 ? "old" : $"s{index:D3}", $"Chat {index}"))
            .ToArray();
        foreach (var row in rows) row.WorkspacePath = @"D:\work\app";
        var backend = new FakeSessions(rows);
        var viewModel = NewViewModel(backend);
        viewModel.SetRefreshActive(true);
        Assert.True(await viewModel.RefreshLatestAsync());
        Assert.True(viewModel.CanLoadMore);

        var plan = await viewModel.PlanWorkspaceDeletionAsync(SidebarSessionOrganization.PathKey(@"D:\work\app"));

        Assert.NotNull(plan);
        Assert.Equal(101, plan!.SessionIds.Count);
        Assert.Contains("old", plan.SessionIds);
    }

    [Fact]
    public async Task Workspace_delete_plan_fails_closed_when_the_backend_repeats_a_cursor()
    {
        var backend = new FakeSessions
        {
            PageOverride = (cursor, _) => new SessionListPageResponse
            {
                Status = "ok",
                Sessions = [Session(cursor is null ? "a" : "b", "Chat")],
                NextCursor = "repeat",
            },
        };
        var viewModel = NewViewModel(backend);

        await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.PlanWorkspaceDeletionAsync(
            SidebarSessionOrganization.PathKey(@"D:\work\app")));

        Assert.Equal(2, backend.Requests.Count);
    }

    [Fact]
    public async Task Workspace_delete_plan_fails_closed_when_more_than_the_session_cap_exists()
    {
        var backend = new FakeSessions
        {
            PageOverride = (_, _) =>
            {
                var rows = Enumerable.Range(0, SessionSidebarViewModel.MaxWorkspaceDeletionSessions + 1)
                    .Select(index => Session($"s{index:D5}", $"Chat {index}"))
                    .ToArray();
                foreach (var row in rows) row.WorkspacePath = @"D:\work\app";
                return new SessionListPageResponse
                {
                    Status = "ok",
                    Sessions = rows.ToList(),
                    NextCursor = null,
                };
            },
        };
        var viewModel = NewViewModel(backend);

        await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.PlanWorkspaceDeletionAsync(
            SidebarSessionOrganization.PathKey(@"D:\work\app")));

        Assert.Single(backend.Requests);
    }

    [Fact]
    public async Task Workspace_delete_plan_observes_cancellation_between_pages()
    {
        using var cancellation = new CancellationTokenSource();
        var backend = new FakeSessions
        {
            PageOverride = (_, _) => new SessionListPageResponse
            {
                Status = "ok",
                Sessions = [Session("a", "Chat")],
                NextCursor = "next",
            },
        };
        backend.OnList = () => cancellation.Cancel();
        var viewModel = NewViewModel(backend);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => viewModel.PlanWorkspaceDeletionAsync(
            SidebarSessionOrganization.PathKey(@"D:\work\app"), cancellation.Token));

        Assert.Single(backend.Requests);
    }

    [Fact]
    public async Task Workspace_delete_collection_notifications_stay_on_the_calling_ui_context()
    {
        using var ui = new DedicatedSynchronizationContext();
        await ui.RunAsync(async () =>
        {
            var active = Session("a", "Active");
            active.WorkspacePath = @"D:\work\app";
            var backend = new FakeSessions(active) { DeleteDelay = TimeSpan.FromMilliseconds(40) };
            var viewModel = NewViewModel(backend);
            viewModel.ApplySessions(new SessionListPageBatch([active], null, 1));
            int uiThread = Environment.CurrentManagedThreadId;
            int? notificationThread = null;
            viewModel.Sessions.CollectionChanged += (_, _) =>
                notificationThread ??= Environment.CurrentManagedThreadId;
            var plan = await viewModel.PlanWorkspaceDeletionAsync(SidebarSessionOrganization.PathKey(@"D:\work\app"));

            await viewModel.DeleteWorkspaceSessionsAsync(plan!);

            Assert.NotNull(notificationThread);
            Assert.Equal(uiThread, notificationThread);
        });
    }

    [Fact]
    public void Applying_workspace_kind_change_updates_the_existing_session_row()
    {
        var viewModel = NewViewModel(new FakeSessions());
        var existing = Session("a", "Chat");
        viewModel.ApplySessions(new SessionListPageBatch([existing], null, 1));
        int renders = 0;
        viewModel.Rendered += () => renders++;
        var incoming = Session("a", "Chat");
        incoming.WorkspaceKind = "scratch";

        viewModel.ApplySessions(new SessionListPageBatch([incoming], null, 1));

        Assert.Same(existing, Assert.Single(viewModel.Sessions));
        Assert.Equal("scratch", existing.WorkspaceKind);
        Assert.Equal(1, renders);
    }

    private sealed class DedicatedSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly Thread _thread;
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DedicatedSynchronizationContext()
        {
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                _started.SetResult();
                foreach (var item in _queue.GetConsumingEnumerable()) item.Callback(item.State);
            }) { IsBackground = true };
            _thread.Start();
            _started.Task.GetAwaiter().GetResult();
        }

        public override void Post(SendOrPostCallback callback, object? state) => _queue.Add((callback, state));

        public Task RunAsync(Func<Task> action)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(async _ =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception error) { completion.SetException(error); }
            }, null);
            return completion.Task;
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            if (Thread.CurrentThread != _thread) _thread.Join();
            _queue.Dispose();
        }
    }
}
