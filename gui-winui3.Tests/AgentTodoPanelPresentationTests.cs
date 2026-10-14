using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentTodoPanelPresentationTests
{
    [Fact]
    public void Active_task_wins_over_an_earlier_pending_item()
    {
        var state = AgentTodoPanelPresentation.Create([
            new("Later", TodoItemModel.Pending), new("Working", TodoItemModel.InProgress),
            new("Done", TodoItemModel.Completed)], 800);
        Assert.True(state.IsVisible);
        Assert.Equal("Working", state.Current);
        Assert.Equal(1, state.Done);
        Assert.Equal(3, state.Total);
    }

    [Fact]
    public void Empty_and_completed_plans_leave_room_for_the_chat()
    {
        Assert.False(AgentTodoPanelPresentation.Create(null, 800).IsVisible);
        Assert.False(AgentTodoPanelPresentation.Create([new("Done", TodoItemModel.Completed)], 800).IsVisible);
    }

    [Theory]
    [InlineData(320, 80)]
    [InlineData(600, 150)]
    [InlineData(1080, 180)]
    public void Long_plans_have_a_bounded_scroll_viewport(double height, double expected)
    {
        var state = AgentTodoPanelPresentation.Create(
            Enumerable.Range(0, 100).Select(i => new TodoItemModel($"Task {i}", TodoItemModel.Pending)).ToArray(), height);
        Assert.Equal(expected, state.ListHeight);
    }
}
