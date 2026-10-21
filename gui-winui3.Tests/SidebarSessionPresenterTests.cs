using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class SidebarSessionPresenterTests
{
    [Fact]
    public void New_chat_uses_selected_workspace_and_defaults_to_recents()
    {
        Assert.Equal("work", SidebarSessionPresenter.ResolveNewChatWorkspace("work"));
        Assert.Equal(SidebarSessionOrganization.RecentsKey, SidebarSessionPresenter.ResolveNewChatWorkspace(null));
    }

    [Fact]
    public void Group_toggle_uses_the_bound_effective_state_before_the_stored_default()
    {
        Assert.False(SidebarSessionPresenter.NextExpandedState(true, false));
        Assert.True(SidebarSessionPresenter.NextExpandedState(false, true));
        Assert.False(SidebarSessionPresenter.NextExpandedState(null, true));
    }

    [Fact]
    public void Projection_orders_workspace_sections_and_keeps_the_active_new_session_visible()
    {
        var sessions = new[]
        {
            new SessionInfo { Id = "old", Title = "Old chat", WorkspacePath = @"C:\project", MessageCount = 2 },
            new SessionInfo { Id = "draft", Title = "New Session", WorkspacePath = @"C:\project" },
        };
        var result = SidebarSessionPresenter.Build(
            sessions, null, "C:\\app", new SidebarOrganizationPreferences(), [],
            activeSessionId: "old", activeWorkspaceFilter: false, expanded: (_, fallback) => fallback, showAll: _ => false);

        Assert.Equal(new[] { "WORKSPACES", "project", "RECENTS", "CLI SESSIONS" },
            result.Rows.Select(row => row switch
            {
                SidebarSectionRow section => section.Label,
                WorkspaceGroupRow group => group.Group.Label,
                _ => string.Empty,
            }).Where(label => label.Length > 0));
        Assert.Equal(new[] { "draft", "old" }, result.Rows.OfType<SessionInfo>().Select(session => session.Id));
    }

    [Fact]
    public void Search_filters_by_title_workspace_label_and_path_without_empty_groups()
    {
        var sessions = new[]
        {
            new SessionInfo { Id = "one", Title = "API work", WorkspacePath = @"C:\services\one", MessageCount = 1 },
            new SessionInfo { Id = "two", Title = "Notes", WorkspacePath = @"C:\docs", MessageCount = 1 },
        };

        var result = SidebarSessionPresenter.Build(
            sessions, "services", null, new SidebarOrganizationPreferences(), [],
            activeSessionId: null, activeWorkspaceFilter: false, expanded: (_, fallback) => fallback, showAll: _ => false);

        Assert.Equal(1, result.MatchCount);
        Assert.Equal("one", Assert.Single(result.Rows.OfType<SessionInfo>()).Id);
        Assert.DoesNotContain(result.Groups, group => group.Sessions.Count == 0);
    }

    [Fact]
    public void Empty_projection_keeps_the_three_sidebar_section_headers_available()
    {
        var result = SidebarSessionPresenter.Build(
            [], null, null, new SidebarOrganizationPreferences(), [],
            activeSessionId: null, activeWorkspaceFilter: false, expanded: (_, fallback) => fallback, showAll: _ => false);

        Assert.Equal(
            new[] { "WORKSPACES", "RECENTS", "CLI SESSIONS" },
            result.Rows.OfType<SidebarSectionRow>().Select(section => section.Label));
        // Each section says what it holds when it is empty, WORKSPACES included.
        Assert.Equal(
            new[] { SidebarSessionPresenter.NoWorkspacesLabel, "No recent sessions", "No CLI sessions" },
            result.Rows.OfType<SidebarEmptyRow>().Select(empty => empty.Label));
    }

    [Fact]
    public void The_workspaces_hint_leaves_once_a_workspace_exists_or_while_searching()
    {
        var searching = SidebarSessionPresenter.Build(
            [], "anything", null, new SidebarOrganizationPreferences(), [],
            activeSessionId: null, activeWorkspaceFilter: false, expanded: (_, fallback) => fallback, showAll: _ => false);

        Assert.DoesNotContain(searching.Rows.OfType<SidebarEmptyRow>(), empty => empty.Label == SidebarSessionPresenter.NoWorkspacesLabel);
    }
}
