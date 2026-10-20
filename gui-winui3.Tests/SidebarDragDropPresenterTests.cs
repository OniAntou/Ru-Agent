using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class SidebarDragDropPresenterTests
{
    [Fact]
    public void A_session_drop_resolves_the_sibling_before_or_after_the_hovered_row()
    {
        var first = new SessionInfo { Id = "first", WorkspacePath = @"C:\work", MessageCount = 1 };
        var second = new SessionInfo { Id = "second", WorkspacePath = @"C:\work", MessageCount = 1 };
        var dragged = new SessionInfo { Id = "dragged", WorkspacePath = @"D:\other", MessageCount = 1 };
        var group = new WorkspaceGroup("work", "work", @"C:\work", [first, second]);
        WorkspaceGroupIdentity GroupFor(SessionInfo session) => new(session.WorkspacePath == @"C:\work" ? "work" : "other", "", session.WorkspacePath);

        var before = SidebarDragDropPresenter.Resolve("dragged", second, insertAbove: true, [dragged, first, second], [group], GroupFor);
        var after = SidebarDragDropPresenter.Resolve("dragged", second, insertAbove: false, [dragged, first, second], [group], GroupFor);

        Assert.Equal("second", before?.BeforeId);
        Assert.Null(after?.BeforeId);
        Assert.Equal("work", before?.Key);
    }

    [Fact]
    public void Drag_cannot_target_the_source_row_or_cross_between_cli_and_gui_sessions()
    {
        var cli = new SessionInfo { Id = "cli", ClientSource = "cli" };
        var gui = new SessionInfo { Id = "gui", ClientSource = "gui" };
        WorkspaceGroupIdentity GroupFor(SessionInfo session) =>
            session.ClientSource == "cli"
                ? new(SidebarSessionOrganization.CliKey, "CLI", null)
                : new(SidebarSessionOrganization.RecentsKey, "Recents", null);

        Assert.Null(SidebarDragDropPresenter.Resolve("cli", cli, true, [cli, gui], [], GroupFor));
        Assert.Null(SidebarDragDropPresenter.Resolve("gui", new SidebarSectionRow("cli", "CLI"), true, [cli, gui], [], GroupFor));
    }
}
