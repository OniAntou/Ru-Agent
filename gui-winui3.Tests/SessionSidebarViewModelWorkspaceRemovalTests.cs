using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed partial class SessionSidebarViewModelTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("project")]
    public async Task Workspace_delete_includes_an_open_empty_project_draft_before_the_host_knows_its_path(string? hostKind)
    {
        const string path = @"D:\work\app";
        var backend = new FakeSessions(new SessionInfo { Id = "draft", MessageCount = 0, WorkspaceKind = hostKind })
        {
            CreateHook = () => Task.FromResult<SessionInfo?>(new SessionInfo { Id = "draft" }),
        };
        var viewModel = NewViewModel(backend);
        viewModel.SetRefreshActive(true);
        viewModel.Pin(path);
        var key = SidebarSessionOrganization.PathKey(path);
        var draft = await viewModel.CreateDraftAsync(key, path);
        Assert.NotNull(draft);

        var plan = await viewModel.PlanWorkspaceDeletionAsync(key);

        Assert.NotNull(plan);
        Assert.Equal(["draft"], plan!.SessionIds);
        var result = await viewModel.DeleteWorkspaceSessionsAsync(plan);
        Assert.True(result.WorkspaceRemoved);
        Assert.Equal(["draft"], backend.Deleted);
        Assert.Empty(viewModel.Sessions);
        Assert.DoesNotContain(viewModel.Groups, group => group.Key == key);
    }

    [Fact]
    public async Task Workspace_delete_removes_the_pin_and_chats_but_preserves_project_files()
    {
        var path = Path.Combine(_root, "project");
        Directory.CreateDirectory(path);
        var file = Path.Combine(path, "source.txt");
        File.WriteAllText(file, "project data");
        var chat = Session("a", "Chat");
        chat.WorkspacePath = path;
        var backend = new FakeSessions(chat);
        var viewModel = NewViewModel(backend);
        viewModel.SetRefreshActive(true);
        await viewModel.RefreshLatestAsync();
        viewModel.Pin(path);
        var key = SidebarSessionOrganization.PathKey(path);
        viewModel.SelectGroup(key);
        var plan = await viewModel.PlanWorkspaceDeletionAsync(key);

        var result = await viewModel.DeleteWorkspaceSessionsAsync(plan!);

        Assert.True(result.WorkspaceRemoved);
        Assert.Equal(["a"], backend.Deleted);
        Assert.Empty(viewModel.Sessions);
        Assert.DoesNotContain(viewModel.Groups, group => group.Key == key);
        Assert.Empty(new WorkspacePinStore(Path.Combine(_root, "pins.json")).PinnedPaths);
        Assert.Null(viewModel.SelectedWorkspaceKey);
        Assert.Equal("project data", File.ReadAllText(file));
        var reopened = NewViewModel(backend);
        reopened.SetAppRoot(null);
        Assert.DoesNotContain(reopened.Groups, group => group.Key == key);
    }

    [Fact]
    public async Task Workspace_delete_can_remove_an_empty_pinned_workspace()
    {
        var backend = new FakeSessions();
        var viewModel = NewViewModel(backend);
        viewModel.SetRefreshActive(true);
        const string path = @"D:\work\empty";
        viewModel.Pin(path);
        var key = SidebarSessionOrganization.PathKey(path);

        var plan = await viewModel.PlanWorkspaceDeletionAsync(key);

        Assert.NotNull(plan);
        Assert.Empty(plan!.SessionIds);
        var result = await viewModel.DeleteWorkspaceSessionsAsync(plan);
        Assert.True(result.WorkspaceRemoved);
        Assert.Empty(backend.Deleted);
        Assert.DoesNotContain(viewModel.Groups, group => group.Key == key);
        Assert.Empty(new WorkspacePinStore(Path.Combine(_root, "pins.json")).PinnedPaths);
    }

    [Fact]
    public async Task Workspace_delete_keeps_the_pin_when_a_session_cannot_be_deleted()
    {
        var chat = Session("a", "Chat");
        chat.WorkspacePath = @"D:\work\app";
        var backend = new FakeSessions(chat)
        {
            DeleteOutcome = new ApiService.SessionDeletionOutcome(false, false, "unavailable"),
        };
        var viewModel = NewViewModel(backend);
        viewModel.SetRefreshActive(true);
        viewModel.Pin(chat.WorkspacePath);
        var key = SidebarSessionOrganization.PathKey(chat.WorkspacePath);
        var plan = await viewModel.PlanWorkspaceDeletionAsync(key);

        var result = await viewModel.DeleteWorkspaceSessionsAsync(plan!);

        Assert.Equal(1, result.Failed);
        Assert.False(result.WorkspaceRemoved);
        Assert.Contains(new WorkspacePinStore(Path.Combine(_root, "pins.json")).PinnedPaths,
            path => SidebarSessionOrganization.SamePath(path, chat.WorkspacePath));
    }

    [Fact]
    public async Task Workspace_delete_keeps_new_chats_created_after_confirmation()
    {
        const string path = @"D:\work\app";
        var chat = Session("a", "Confirmed chat");
        chat.WorkspacePath = path;
        var backend = new FakeSessions(chat);
        var viewModel = NewViewModel(backend);
        viewModel.SetRefreshActive(true);
        viewModel.Pin(path);
        var key = SidebarSessionOrganization.PathKey(path);
        var plan = await viewModel.PlanWorkspaceDeletionAsync(key);
        backend.OnDelete = _ =>
        {
            var newer = Session("new", "Unconfirmed new chat");
            newer.WorkspacePath = path;
            backend.Rows.Add(newer);
        };

        var result = await viewModel.DeleteWorkspaceSessionsAsync(plan!);

        Assert.False(result.WorkspaceRemoved);
        Assert.Equal(["a"], backend.Deleted);
        Assert.Contains(viewModel.Sessions, row => row.Id == "new");
        Assert.Contains(new WorkspacePinStore(Path.Combine(_root, "pins.json")).PinnedPaths,
            pinned => SidebarSessionOrganization.SamePath(pinned, path));
    }

    [Fact]
    public async Task Workspace_delete_retries_removing_the_pin_after_a_preference_write_failure()
    {
        var backend = new FakeSessions();
        var viewModel = NewViewModel(backend);
        viewModel.SetRefreshActive(true);
        const string path = @"D:\work\empty";
        viewModel.Pin(path);
        var key = SidebarSessionOrganization.PathKey(path);
        var plan = await viewModel.PlanWorkspaceDeletionAsync(key);
        Assert.NotNull(plan);
        var pinsPath = Path.Combine(_root, "pins.json");
        using (new FileStream(pinsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await viewModel.DeleteWorkspaceSessionsAsync(plan!);
            Assert.False(result.WorkspaceRemoved);
            Assert.Contains(new WorkspacePinStore(pinsPath).PinnedPaths,
                pinned => SidebarSessionOrganization.SamePath(pinned, path));
        }

        var retry = await viewModel.PlanWorkspaceDeletionAsync(key);
        Assert.NotNull(retry);
        var retried = await viewModel.DeleteWorkspaceSessionsAsync(retry!);

        Assert.True(retried.WorkspaceRemoved);
        Assert.Empty(new WorkspacePinStore(pinsPath).PinnedPaths);
        Assert.DoesNotContain(viewModel.Groups, group => group.Key == key);
    }

    [Fact]
    public async Task Workspace_delete_checks_later_pages_before_removing_the_pin()
    {
        const string path = @"D:\work\app";
        var chat = Session("a", "Confirmed chat");
        chat.WorkspacePath = path;
        var backend = new FakeSessions(chat);
        foreach (var index in Enumerable.Range(0, 101))
        {
            var other = Session($"other-{index}", "Other workspace");
            other.WorkspacePath = @"D:\work\other";
            backend.Rows.Add(other);
        }
        var viewModel = NewViewModel(backend);
        viewModel.SetRefreshActive(true);
        viewModel.Pin(path);
        var key = SidebarSessionOrganization.PathKey(path);
        var plan = await viewModel.PlanWorkspaceDeletionAsync(key);
        backend.OnDelete = _ =>
        {
            var newer = Session("new", "New chat on a later page");
            newer.WorkspacePath = path;
            backend.Rows.Add(newer);
        };

        var result = await viewModel.DeleteWorkspaceSessionsAsync(plan!);

        Assert.False(result.WorkspaceRemoved);
        Assert.Equal(["a"], backend.Deleted);
        Assert.Contains(backend.Rows, row => row.Id == "new");
        Assert.Contains(new WorkspacePinStore(Path.Combine(_root, "pins.json")).PinnedPaths,
            pinned => SidebarSessionOrganization.SamePath(pinned, path));
    }

    [Fact]
    public async Task Workspace_delete_keeps_the_pin_when_the_final_listing_is_unavailable()
    {
        const string path = @"D:\work\app";
        var chat = Session("a", "Chat");
        chat.WorkspacePath = path;
        var backend = new FakeSessions(chat);
        var viewModel = NewViewModel(backend);
        viewModel.SetRefreshActive(true);
        viewModel.Pin(path);
        var key = SidebarSessionOrganization.PathKey(path);
        var plan = await viewModel.PlanWorkspaceDeletionAsync(key);
        backend.OnDelete = _ => backend.PageOverride = (_, _) => throw new InvalidOperationException("listing unavailable");

        await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.DeleteWorkspaceSessionsAsync(plan!));

        Assert.Contains(new WorkspacePinStore(Path.Combine(_root, "pins.json")).PinnedPaths,
            pinned => SidebarSessionOrganization.SamePath(pinned, path));
    }

    [Fact]
    public async Task Workspace_delete_reports_pending_cleanup_after_the_session_record_is_deleted()
    {
        const string path = @"D:\work\app";
        var chat = Session("a", "Chat");
        chat.WorkspacePath = path;
        var backend = new FakeSessions(chat)
        {
            DeleteOutcome = new ApiService.SessionDeletionOutcome(false, true, "cleanup pending"),
        };
        var viewModel = NewViewModel(backend);
        viewModel.SetRefreshActive(true);
        viewModel.Pin(path);
        var plan = await viewModel.PlanWorkspaceDeletionAsync(SidebarSessionOrganization.PathKey(path));

        var result = await viewModel.DeleteWorkspaceSessionsAsync(plan!);

        Assert.True(result.WorkspaceRemoved);
        Assert.Equal(1, result.Partial);
        Assert.Empty(backend.Rows);
        Assert.Empty(new WorkspacePinStore(Path.Combine(_root, "pins.json")).PinnedPaths);
    }
}
