using System;
using System.Text.Json;
using Microsoft.UI.Xaml;
using RuApp.Models;
using RuApp.Services;
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
    public void Tasks_panel_lists_stops_and_reads_through_the_command_route()
    {
        var document = GuiSource.Xml("Controls", "AgentSidePanelControl.xaml");
        GuiSource.Require(document, "AgentTasksList");
        Assert.DoesNotContain("AgentTasksButton", GuiSource.XamlNames(document));
        Assert.Contains("OnTaskLogClick", document.ToString(), StringComparison.Ordinal);

        var code = GuiSource.ViewText("ChatView");
        Assert.Contains("\"/tasks\"", code, StringComparison.Ordinal);
        Assert.Contains("\"/tasks kill \"", code, StringComparison.Ordinal);
        // Reading a task's output is the same typed command, never a second route.
        Assert.Contains("\"/tasks read \"", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Task_output_projection_keeps_the_page_inside_its_reported_bounds()
    {
        var page = AgentTaskOutputProjection.Project(JsonSerializer.SerializeToElement(new
        {
            execution_id = "req-1",
            state = "exited",
            exit_code = 3,
            offset = 4,
            limit = 8,
            total_chars = 12,
            page = "abcdefgh",
            has_before = true,
            truncated = false,
        }));

        Assert.NotNull(page);
        Assert.Equal("req-1", page.ExecutionId);
        Assert.Equal(3, page.ExitCode);
        Assert.Equal(4, page.Offset);
        Assert.Equal(12, page.NextOffset);
        Assert.True(page.HasBefore);
        Assert.False(page.HasMore);
        Assert.Contains("exit 3", page.Header, StringComparison.Ordinal);
    }

    [Fact]
    public void Task_output_projection_rejects_a_payload_without_text()
    {
        Assert.Null(AgentTaskOutputProjection.Project(JsonSerializer.SerializeToElement(new { execution_id = "req-1" })));
        Assert.Null(AgentTaskOutputProjection.Project(JsonSerializer.SerializeToElement(new { page = "text" })));
        Assert.Null(AgentTaskOutputProjection.Project(null));
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

    [Fact]
    public void Narrow_layout_does_not_overwrite_the_sessions_selected_panel_mode()
    {
        var code = GuiSource.ViewText("ChatView");
        Assert.DoesNotContain("CollapseToCard", GuiSource.MethodBody(code, "UpdateSidePanelLayout"), StringComparison.Ordinal);
    }
}
