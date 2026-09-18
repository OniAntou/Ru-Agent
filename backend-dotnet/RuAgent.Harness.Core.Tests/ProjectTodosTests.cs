using RuAgent.Harness.Core;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

/// <summary>
/// The task list as an activity.
/// </summary>
/// <remarks>
/// The pipeline decides whether to publish from a tool receipt, which is covered end to
/// end elsewhere. This asserts the projection and the publish path in isolation, where a
/// failure names the piece that broke instead of only that the list never arrived.
/// </remarks>
public sealed class ProjectTodosTests
{
    private static readonly RunId Run = new("run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

    [Fact]
    public void The_projection_carries_the_rows_in_order()
    {
        var activity = AgentActivityProjector.ProjectTodos(
            "act_todo_11111111111111111111111111111111",
            [
                new AgentTodoRow("Read the failing test", "completed"),
                new AgentTodoRow("Implement the validator", "in_progress"),
                new AgentTodoRow("Write the docs", "pending"),
            ]);

        Assert.Equal(AgentActivityKind.Todo, activity.Kind);
        Assert.Equal(AgentActivityState.Completed, activity.State);
        Assert.Equal("succeeded", activity.SafeCode);
        Assert.NotNull(activity.Todos);
        Assert.Equal(3, activity.Todos!.Count);
        Assert.Equal("Read the failing test", activity.Todos[0].Content);
        Assert.Equal("completed", activity.Todos[0].Status);
        Assert.Equal("in_progress", activity.Todos[1].Status);
        Assert.Equal("pending", activity.Todos[2].Status);
    }

    [Fact]
    public void An_empty_list_projects_as_an_empty_list_rather_than_null()
    {
        // Clearing the plan is a write like any other, and the view has to be able to
        // tell "empty" from "this activity carries none".
        var activity = AgentActivityProjector.ProjectTodos(
            "act_todo_22222222222222222222222222222222",
            []);

        Assert.NotNull(activity.Todos);
        Assert.Empty(activity.Todos!);
    }

    [Fact]
    public void The_projection_survives_the_publish_path()
    {
        // PublishSafe swallows a sink failure and counts it, so a projection that the
        // wire validator rejects would disappear with no visible error. This asserts the
        // activity reaches the sink, which is what the end-to-end check depends on.
        var sink = new RecordingSink();
        var activity = AgentActivityProjector.ProjectTodos(
            "act_todo_33333333333333333333333333333333",
            [new AgentTodoRow("One task", "pending")]);

        AgentActivityProjector.PublishSafe(sink, Run, activity);

        Assert.Single(sink.Published);
        Assert.Equal(AgentActivityKind.Todo, sink.Published[0].Kind);
        Assert.Equal("One task", sink.Published[0].Todos![0].Content);
    }

    [Fact]
    public void A_sink_that_throws_does_not_escape()
    {
        // The plan is a display aid. Losing it must never fail the step that produced it.
        AgentActivityProjector.PublishSafe(
            new ThrowingSink(),
            Run,
            AgentActivityProjector.ProjectTodos(
                "act_todo_44444444444444444444444444444444",
                [new AgentTodoRow("One task", "pending")]));
    }

    private sealed class RecordingSink : IAgentActivitySink
    {
        public List<AgentActivity> Published { get; } = [];

        public void Publish(RunId runId, AgentActivity activity) => Published.Add(activity);
    }

    private sealed class ThrowingSink : IAgentActivitySink
    {
        public void Publish(RunId runId, AgentActivity activity) =>
            throw new InvalidOperationException("sink is down");
    }
}
