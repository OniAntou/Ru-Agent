using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed partial class SessionSidebarViewModelTests
{
    [Fact]
    public async Task Started_chat_stays_in_recents_when_a_poll_adopts_its_private_scratch_workspace()
    {
        var backend = new FakeSessions();
        var viewModel = NewViewModel(backend);
        viewModel.SetAppRoot(AppRoot);
        viewModel.SetRefreshActive(true);
        var draft = Assert.IsType<SessionInfo>(await viewModel.CreateDraftAsync(
            SidebarSessionOrganization.RecentsKey, workspacePath: null));
        Assert.Contains(viewModel.Rows, row => row is SessionInfo session && session.Id == draft.Id);
        viewModel.MarkStarted(draft.Id, "Hello");
        backend.Rows.Add(new SessionInfo
        {
            Id = draft.Id, Title = "Hello", MessageCount = 2, ClientSource = "gui",
            WorkspaceKind = "scratch", WorkspacePath = @"C:\RuData\workspaces\chat",
        });

        Assert.True(await viewModel.RefreshAsync());

        var stored = Assert.Single(viewModel.Sessions);
        Assert.Equal("scratch", stored.WorkspaceKind);
        Assert.Equal(SidebarSessionOrganization.RecentsKey, viewModel.GroupFor(stored).Key);
        Assert.Contains(viewModel.Rows, row => row is SessionInfo session && session.Id == draft.Id);
        Assert.Empty(viewModel.Rows.OfType<WorkspaceGroupRow>());
        var group = Assert.Single(viewModel.Rows.OfType<SidebarSectionRow>(),
            row => row.Key == SidebarSessionOrganization.RecentsKey);
        Assert.True(group.IsExpanded);

        // A subsequent unchanged poll must retain the visible row too.
        Assert.True(await viewModel.RefreshAsync());
        Assert.Contains(viewModel.Rows, row => row is SessionInfo session && session.Id == draft.Id);
    }
}
