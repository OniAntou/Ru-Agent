using System;
using Microsoft.UI.Xaml;
using RuApp.Models;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The side surface's background-task section: rows come from the Host's /tasks
/// command and a stop is the same command with a kill argument, so the Broker
/// stays the only execution authority and the view never talks to it directly.
/// The composer flyout was replaced by the surface by owner request.
/// </summary>
public sealed class AgentTasksUiContractTests
{
    [Fact]
    public void Tasks_panel_lists_and_stops_through_the_command_route()
    {
        var document = GuiSource.Xml("Controls", "AgentSidePanelControl.xaml");
        GuiSource.Require(document, "AgentTasksList");
        Assert.DoesNotContain("AgentTasksButton", GuiSource.XamlNames(document));

        var code = GuiSource.ViewText("ChatView");
        Assert.Contains("\"/tasks\"", code, StringComparison.Ordinal);
        Assert.Contains("\"/tasks kill \"", code, StringComparison.Ordinal);
    }

    [Fact]
    public void The_surface_only_shows_with_chat_content_in_a_wide_enough_window()
    {
        // The surface reserves its own space and hides when the chat area is
        // narrow or the chat is empty, so it can never cover the conversation.
        var code = GuiSource.ViewText("ChatView");
        string canShow = GuiSource.MethodBody(code, "CanShowSidePanel");
        Assert.Contains("Messages.Count > 0", canShow, StringComparison.Ordinal);
        Assert.Contains("SidePanelMinRootWidth", canShow, StringComparison.Ordinal);

        string layout = GuiSource.MethodBody(code, "UpdateSidePanelLayout");
        Assert.Contains("AgentSidePanelMode.Hidden", layout, StringComparison.Ordinal);
        Assert.Contains("_sidePanelReserveWidth", layout, StringComparison.Ordinal);

        string reserve = GuiSource.MethodBody(code, "ReserveFor");
        Assert.Contains("EffectiveSidePanelReserve", reserve, StringComparison.Ordinal);

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
