using System;
using Microsoft.UI.Xaml;
using RuApp.Models;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The composer's background-task flyout: rows come from the Host's /tasks
/// command and a stop is the same command with a kill argument, so the Broker
/// stays the only execution authority and the view never talks to it directly.
/// </summary>
public sealed class AgentTasksUiContractTests
{
    [Fact]
    public void Tasks_flyout_lists_and_stops_through_the_command_route()
    {
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        GuiSource.Require(document, "AgentTasksButton");
        GuiSource.Require(document, "AgentTasksList");

        var code = GuiSource.ViewText("ChatView");
        Assert.Contains("\"/tasks\"", code, StringComparison.Ordinal);
        Assert.Contains("\"/tasks kill \"", code, StringComparison.Ordinal);
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
