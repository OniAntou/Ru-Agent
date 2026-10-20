using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentRunApprovalInputCoordinatorTests
{
    [Fact]
    public void Approval_state_clears_command_with_approval_identity()
    {
        var state = new AgentRunApprovalCoordinator();
        state.Set("approval-1", "Review file");
        state.SetCommand("git status");

        Assert.True(state.HasApproval);
        Assert.Equal("Review file", state.Summary);
        Assert.Equal("git status", state.Command);

        state.SetId(null);

        Assert.False(state.HasApproval);
        Assert.Null(state.Command);
    }

    [Fact]
    public void Input_state_is_independent_and_clears_question_options()
    {
        var approval = new AgentRunApprovalCoordinator();
        var input = new AgentRunInputCoordinator();
        approval.Set("approval-1", "Review file");
        input.Set("input-1", "Which file?", ["a.cs", "b.cs"]);

        Assert.True(approval.HasApproval);
        Assert.True(input.HasInput);
        Assert.Equal(new[] { "a.cs", "b.cs" }, input.Options);

        input.Clear();

        Assert.True(approval.HasApproval);
        Assert.False(input.HasInput);
        Assert.Null(input.Question);
        Assert.Empty(input.Options);
    }
}
