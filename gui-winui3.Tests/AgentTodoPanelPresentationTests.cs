using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentTodoPanelPresentationTests
{
    private static AgentTodoPanelPresentation Create(
        IReadOnlyList<TodoItemModel>? todos,
        double viewportHeight,
        bool expanded = false) =>
        AgentTodoPanelPresentation.Create(todos, viewportHeight, expanded);

    [Fact]
    public void Active_task_wins_over_an_earlier_pending_item()
    {
        var state = Create([
            new("Later", TodoItemModel.Pending),
            new("Working", TodoItemModel.InProgress),
            new("Done", TodoItemModel.Completed)], 800);

        Assert.True(state.IsVisible);
        Assert.Equal("Working", state.Current);
        Assert.Equal(1, state.Done);
        Assert.Equal(3, state.Total);
        Assert.Equal(2, state.Remaining);
        Assert.True(state.HasActive);
        Assert.Equal("Working", state.HeaderPrimary);
        Assert.Equal("Next: Later", state.HeaderSecondary);
    }

    [Fact]
    public void The_header_names_the_next_task_or_the_last_one()
    {
        var withNext = Create([
            new("Done", TodoItemModel.Completed),
            new("Working", TodoItemModel.InProgress),
            new("Later", TodoItemModel.Pending)], 800);
        Assert.Equal("Next: Later", withNext.HeaderSecondary);

        var last = Create([
            new("Done", TodoItemModel.Completed),
            new("Working", TodoItemModel.InProgress)], 800);
        Assert.Equal("Last task in the plan", last.HeaderSecondary);
    }

    [Fact]
    public void Expanding_the_shelf_turns_the_header_into_a_remaining_count()
    {
        TodoItemModel[] plan = [
            new("Done", TodoItemModel.Completed),
            new("Working", TodoItemModel.InProgress),
            new("Later", TodoItemModel.Pending)];

        var collapsed = Create(plan, 800);
        var expanded = Create(plan, 800, expanded: true);

        Assert.Equal("Working", collapsed.HeaderPrimary);
        Assert.Equal("2 tasks remaining", expanded.HeaderPrimary);
        Assert.Equal("Next: Later", expanded.HeaderSecondary);
    }

    [Fact]
    public void Empty_and_completed_plans_hide_the_shelf()
    {
        Assert.False(Create(null, 800).IsVisible);

        var done = Create([
            new("Done", TodoItemModel.Completed),
            new("Also done", TodoItemModel.Completed)], 800);

        Assert.False(done.IsVisible);
        Assert.Equal(2, done.Done);
        Assert.Equal(2, done.Total);
        Assert.Equal(0, done.Remaining);
        Assert.Empty(done.Segments);
    }

    [Fact]
    public void Plan_segments_keep_task_order()
    {
        var state = Create([
            new("Done", TodoItemModel.Completed),
            new("Working", TodoItemModel.InProgress),
            new("Later", TodoItemModel.Pending)], 800);

        Assert.True(state.UseSegments);
        Assert.Equal(
            new[] { TodoSegmentState.Done, TodoSegmentState.Active, TodoSegmentState.Pending },
            state.Segments);
        Assert.Equal(1.0 / 3.0, state.BarFraction, 3);
    }

    [Fact]
    public void Plans_past_the_segment_limit_use_the_continuous_bar()
    {
        var twelve = Enumerable.Range(0, AgentTodoPanelPresentation.MaxSegments)
            .Select(index => new TodoItemModel($"Task {index}", TodoItemModel.Pending))
            .ToArray();
        var thirteen = twelve.Append(new TodoItemModel("One more", TodoItemModel.Pending)).ToArray();

        var segments = Create(twelve, 800);
        var bar = Create(thirteen, 800);

        Assert.True(segments.UseSegments);
        Assert.Equal(AgentTodoPanelPresentation.MaxSegments, segments.Segments.Count);
        Assert.False(bar.UseSegments);
        Assert.Empty(bar.Segments);
        Assert.Equal(0, bar.BarFraction);
    }

    [Theory]
    [InlineData(320, 80)]
    [InlineData(600, 150)]
    [InlineData(1080, 224)]
    public void Long_plans_have_a_bounded_scroll_viewport(double height, double expected)
    {
        var state = AgentTodoPanelPresentation.Create(
            Enumerable.Range(0, 100).Select(i => new TodoItemModel($"Task {i}", TodoItemModel.Pending)).ToArray(), height);
        Assert.Equal(expected, state.ListHeight);
    }
}
