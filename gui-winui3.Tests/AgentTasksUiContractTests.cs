using System;
using Microsoft.UI.Xaml;
using RuApp.Models;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The side panel's background-task section: rows come from the Host's /tasks
/// command and a stop is the same command with a kill argument, so the Broker
/// stays the only execution authority and the view never talks to it directly.
/// The composer flyout was replaced by the panel by owner request.
/// </summary>
public sealed class AgentTasksUiContractTests
{
    [Fact]
    public void Tasks_panel_lists_and_stops_through_the_command_route()
    {
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        GuiSource.Require(document, "SidePanelCard");
        GuiSource.Require(document, "AgentTasksList");
        Assert.DoesNotContain("AgentTasksButton", GuiSource.XamlNames(document));

        var code = GuiSource.ViewText("ChatView");
        Assert.Contains("\"/tasks\"", code, StringComparison.Ordinal);
        Assert.Contains("\"/tasks kill \"", code, StringComparison.Ordinal);
    }

    [Fact]
    public void The_panel_only_shows_with_chat_content_in_a_wide_enough_window()
    {
        // The panel reserves its own space and hides when the chat area is
        // narrow or the chat is empty, so it can never cover the conversation.
        var code = GuiSource.ViewText("ChatView");
        string layout = GuiSource.MethodBody(code, "UpdateSidePanelLayout");
        Assert.Contains("Messages.Count > 0", layout, StringComparison.Ordinal);
        Assert.Contains("SidePanelMinRootWidth", layout, StringComparison.Ordinal);
        Assert.Contains("_sidePanelUserCollapsed", layout, StringComparison.Ordinal);
        Assert.Contains(
            "_sidePanelReserveWidth",
            GuiSource.MethodBody(code, "ApplyInputWidth"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Task_rows_only_offer_stop_while_running()
    {
        var running = new AgentTaskRow("req-1", "running", null);
        var done = new AgentTaskRow("req-2", "exited", 0);

        Assert.Equal(Visibility.Visible, running.StopVisibility);
        Assert.Equal(Visibility.Collapsed, done.StopVisibility);
        Assert.Equal("exit 0", done.DetailText);
        Assert.Contains("req-1", running.Label, StringComparison.Ordinal);
    }
}
