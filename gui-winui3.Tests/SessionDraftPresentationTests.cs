using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class SessionDraftPresentationTests
{
    [Fact]
    public void New_session_is_visible_immediately_in_its_workspace_and_survives_empty_polls()
    {
        var state = new SessionDraftPresentation();
        var row = new SessionInfo { Id = "new" };
        state.Activate(row.Id);
        state.Register(row, "workspace-a", "C:\\projects\\a");
        Assert.Same(row, Assert.Single(state.Project([])));
        var visible = Assert.Single(state.Project([new() { Id = row.Id, Title = "server default" }]));
        Assert.Equal("New Session", visible.Title);
        Assert.Equal("workspace-a", WorkspaceSessionProjection.KeyFor(visible));
        Assert.Equal("C:\\projects\\a", visible.WorkspacePath);
    }

    [Fact]
    public void Leaving_an_unsent_session_hides_it_even_when_the_server_still_lists_it()
    {
        var state = new SessionDraftPresentation();
        var row = new SessionInfo { Id = "blank" };
        state.Activate(row.Id);
        state.Register(row, "workspace-a", "C:\\a");
        Assert.Equal([row.Id], state.Activate("existing"));
        Assert.Empty(state.Project([row]));
        // Reopening the application has no local pending draft to resurrect.
        Assert.Empty(new SessionDraftPresentation().Project([row]));
    }

    [Fact]
    public void First_accepted_turn_keeps_the_same_id_and_renames_before_durable_history_arrives()
    {
        var state = new SessionDraftPresentation();
        var row = new SessionInfo { Id = "created-on-click" };
        state.Activate(row.Id);
        state.Register(row, "workspace-a", "C:\\a");
        state.MarkStarted(row.Id, "  fix\n the   tests  ");
        Assert.Empty(state.Activate("other"));
        var visible = Assert.Single(state.Project([new() { Id = row.Id }]));
        Assert.Equal("created-on-click", visible.Id);
        Assert.Equal("fix the tests", visible.Title);
        var durable = new SessionInfo { Id = row.Id, MessageCount = 1, Title = "Server title", WorkspaceId = "canonical-a", WorkspacePath = "C:\\a" };
        Assert.Same(durable, Assert.Single(state.Project([durable])));
        Assert.Empty(state.Project([]));
    }

    [Fact]
    public void Replacement_draft_discards_only_unsent_rows_and_ignores_unrelated_empty_sessions()
    {
        var state = new SessionDraftPresentation();
        state.Activate("sent");
        state.Register(new() { Id = "sent" }, null, null);
        Assert.True(state.MarkStarted("sent", new string('a', 70)));
        Assert.False(state.MarkStarted("sent", "second message must not rename"));
        state.Activate("abandoned");
        state.Register(new() { Id = "abandoned" }, null, null);
        Assert.Equal(["abandoned"], state.Activate("replacement"));
        state.Register(new() { Id = "replacement" }, null, null);

        var visible = state.Project([new() { Id = "unrelated" }, new() { Id = "abandoned" }]);
        Assert.Equal(2, visible.Count);
        Assert.Equal(new string('a', 50), Assert.Single(visible, row => row.Id == "sent").Title);
        Assert.Equal(["replacement"], state.Activate(null));
        Assert.Equal("sent", Assert.Single(state.Project([])).Id);
        state.Remove("sent");
        Assert.Empty(state.Project([]));
    }
}
