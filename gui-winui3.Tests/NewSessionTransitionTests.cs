using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed partial class SessionSidebarViewModelTests
{
    [Fact]
    public async Task Replacing_an_empty_draft_publishes_one_sidebar_render()
    {
        var viewModel = NewViewModel(new FakeSessions());
        await viewModel.CreateDraftAsync(SidebarSessionOrganization.RecentsKey, null);
        var renders = 0;
        var visibleDrafts = new List<string[]>();
        viewModel.Rendered += () =>
        {
            renders++;
            visibleDrafts.Add(viewModel.Rows.OfType<SessionInfo>().Select(row => row.Id).ToArray());
        };

        // A new chat in another workspace replaces the empty draft (the same workspace keeps it).
        var next = await viewModel.CreateDraftAsync("group", @"D:\work\api");

        Assert.NotNull(next);
        Assert.Equal(1, renders);
        Assert.Equal([next!.Id], Assert.Single(visibleDrafts));
    }

    [Fact]
    public async Task Creating_a_recent_draft_preserves_unaffected_workspace_header_containers()
    {
        var project = Session("project", "Existing project chat");
        project.WorkspacePath = @"D:\work\project";
        var viewModel = NewViewModel(new FakeSessions(project));
        viewModel.ApplySessions(new([project], null, 1));
        var header = Assert.Single(viewModel.Rows.OfType<WorkspaceGroupRow>());
        var replacements = 0;
        viewModel.Rows.CollectionChanged += (_, change) =>
        {
            if (change.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Replace)
                replacements++;
        };

        await viewModel.CreateDraftAsync(SidebarSessionOrganization.RecentsKey, null);

        Assert.Same(header, Assert.Single(viewModel.Rows.OfType<WorkspaceGroupRow>()));
        Assert.Equal(0, replacements);
    }

    [Fact]
    public void Selecting_a_session_expands_its_previously_implicit_collapsed_group()
    {
        var first = Session("a", "First");
        first.WorkspacePath = @"D:\work\first";
        var second = Session("b", "Second");
        second.WorkspacePath = @"D:\work\second";
        var viewModel = NewViewModel(new FakeSessions(first, second));
        viewModel.NoteOpened(first.Id);
        viewModel.ApplySessions(new([first, second], null, 1));
        Assert.False(viewModel.Rows.OfType<WorkspaceGroupRow>().Single(
            row => row.Group.Key == viewModel.GroupFor(second).Key).IsExpanded);

        viewModel.Select(second);

        Assert.True(viewModel.Rows.OfType<WorkspaceGroupRow>().Single(
            row => row.Group.Key == viewModel.GroupFor(second).Key).IsExpanded);
    }
}

public sealed class NewSessionTransitionContractTests
{
    [Fact]
    public void Creating_a_chat_on_the_chat_screen_does_not_restart_screen_navigation()
    {
        var create = GuiSource.MethodBody(GuiSource.ViewText("MainWindow"), "CreateDraftSessionAsync");
        Assert.Contains("!ReferenceEquals(ContentFrame.Content, _chatView)", create);
        Assert.Contains("Shell.IsSettingsOpen", create);
    }

    [Fact]
    public void A_known_new_session_paints_empty_without_loading_old_session_history()
    {
        var create = GuiSource.MethodBody(GuiSource.ViewText("MainWindow"), "CreateDraftSessionAsync");
        Assert.Contains("isNewSession: true", create);
        var code = GuiSource.ViewText("ChatView");
        var open = GuiSource.MethodBody(code, "OpenSessionAsync");
        Assert.Contains("if (isNewSession)", open);
        Assert.Contains("PresentNewSession();", open);
        var fresh = GuiSource.MethodBody(code, "PresentNewSession");
        Assert.Contains("Messages.ReplaceAll", fresh);
        Assert.Contains("EndSidePanelLayoutDeferral", fresh);
        Assert.DoesNotContain("await ", fresh);
        Assert.DoesNotContain("_apiService", fresh);
        Assert.DoesNotContain("AgentActivityReplayLoader", fresh);
    }

    [Fact]
    public void Sidebar_motion_avoids_add_delete_animation_competing_with_row_repositioning()
    {
        var motion = GuiSource.MethodBody(GuiSource.ViewText("MainWindow"), "InitializeSidebarMotion");
        Assert.Contains("UiTiming.AnimationsEnabled", motion);
        Assert.Contains("RepositionThemeTransition", motion);
        Assert.DoesNotContain("AddDeleteThemeTransition", motion);
    }

    [Fact]
    public void Changing_reduced_motion_reconfigures_sidebar_transitions()
    {
        var changed = GuiSource.MethodBody(GuiSource.ViewText("MainWindow"), "RefreshSessionMotionPreference");
        Assert.Contains("InitializeSidebarMotion()", changed);
    }
}
