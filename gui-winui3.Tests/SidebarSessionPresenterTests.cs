using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class SidebarSessionPresenterTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Scratch_chat_renders_in_recents_and_respects_its_expansion(bool isExpanded)
    {
        var scratch = new SessionInfo { Id = "scratch", Title = "Hello", MessageCount = 2,
            WorkspaceKind = "scratch", WorkspacePath = @"C:\RuData\workspaces\one" };
        var project = new SessionInfo { Id = "project", MessageCount = 2, WorkspacePath = @"C:\project" };
        var result = SidebarSessionPresenter.Build(
            [scratch, project], null, new SidebarOrganizationPreferences(), [], "scratch",
            (key, fallback) => key == SidebarSessionOrganization.RecentsKey ? isExpanded : fallback,
            _ => false);

        var header = Assert.Single(result.Rows.OfType<SidebarSectionRow>(),
            row => row.Key == SidebarSessionOrganization.RecentsKey);
        Assert.Equal(isExpanded, header.IsExpanded);
        Assert.Equal(isExpanded, result.Rows.OfType<SessionInfo>().Any(row => row.Id == scratch.Id));
        Assert.DoesNotContain(result.Rows.OfType<WorkspaceGroupRow>(), row => row.Group.Label == "Scratch");
        Assert.Single(result.Rows.OfType<WorkspaceGroupRow>());
        if (isExpanded)
        {
            Assert.True(result.Rows.ToList().IndexOf(header) < result.Rows.ToList().IndexOf(scratch));
            Assert.True(result.Rows.ToList().IndexOf(scratch) < result.Rows.ToList().FindIndex(
                row => row is SidebarSectionRow section && section.IsCli));
        }
        Assert.DoesNotContain(result.Rows.OfType<SidebarEmptyRow>(),
            row => row.Key == SidebarSessionOrganization.WorkspacesKey);
    }

    [Fact]
    public void New_chat_uses_selected_workspace_and_defaults_to_recents()
    {
        Assert.Equal("work", SidebarSessionPresenter.ResolveNewChatWorkspace("work"));
        Assert.Equal(SidebarSessionOrganization.RecentsKey, SidebarSessionPresenter.ResolveNewChatWorkspace(null));
    }

    [Theory]
    [InlineData("scratch", null, false)]
    [InlineData("recents", null, true)]
    [InlineData("path:project", @"C:\project", true)]
    [InlineData("path:project", null, false)]
    [InlineData("cli", null, false)]
    [InlineData("unknown", null, false)]
    public void Chat_creation_allows_recents_or_a_resolved_project_path(
        string key, string? path, bool expected) =>
        Assert.Equal(expected, SidebarSessionPresenter.CanCreateChatInGroup(key, path));

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
            sessions, "C:\\app", new SidebarOrganizationPreferences(), [],
            activeSessionId: "old", expanded: (_, fallback) => fallback, showAll: _ => false);

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
    public void Active_workspace_does_not_hide_sessions_in_other_groups()
    {
        var result = SidebarSessionPresenter.Build(
            [new SessionInfo { Id = "one", Title = "API work", WorkspacePath = @"C:\services\one" },
             new SessionInfo { Id = "two", Title = "Notes", WorkspacePath = @"C:\docs" }],
            null, new SidebarOrganizationPreferences(), [], "one", (_, _) => true, _ => false);
        Assert.Equal(2, result.Groups.Count);
        Assert.Equal(new[] { "one", "two" }, result.Rows.OfType<SessionInfo>().Select(row => row.Id).Order());
    }

    [Fact]
    public void Empty_projection_keeps_the_three_sidebar_section_headers_available()
    {
        var result = SidebarSessionPresenter.Build(
            [], null, new SidebarOrganizationPreferences(), [],
            activeSessionId: null, expanded: (_, fallback) => fallback, showAll: _ => false);

        Assert.Equal(
            new[] { "WORKSPACES", "RECENTS", "CLI SESSIONS" },
            result.Rows.OfType<SidebarSectionRow>().Select(section => section.Label));
        // Each section says what it holds when it is empty, WORKSPACES included.
        Assert.Equal(
            new[] { SidebarSessionPresenter.NoWorkspacesLabel, "No recent sessions", "No CLI sessions" },
            result.Rows.OfType<SidebarEmptyRow>().Select(empty => empty.Label));
    }

}
