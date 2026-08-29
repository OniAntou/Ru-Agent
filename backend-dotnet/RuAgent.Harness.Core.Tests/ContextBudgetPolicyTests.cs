using RuAgent.Harness.Core.Context;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed class ContextBudgetPolicyTests
{
    [Fact]
    public void Normal_policy_triggers_at_eighty_percent_and_targets_sixty_two_percent()
    {
        var policy = new ContextBudgetPolicy(new ContextBudgetPolicyOptions(100, 0));

        var decision = policy.Evaluate(161, 300, ContextCompactionStrategy.Normal);

        Assert.Equal(200, decision.UsablePromptTokens);
        Assert.Equal(160, decision.SoftTriggerTokens);
        Assert.Equal(124, decision.TargetTokens);
        Assert.True(decision.NeedsCompaction);
    }

    [Fact]
    public void Aggressive_policy_uses_the_lower_target_but_never_a_zero_budget()
    {
        var policy = new ContextBudgetPolicy(new ContextBudgetPolicyOptions(100, 0));

        var decision = policy.Evaluate(1, 50, ContextCompactionStrategy.Aggressive);

        Assert.Equal(1, decision.UsablePromptTokens);
        Assert.Equal(1, decision.TargetTokens);
        Assert.False(decision.NeedsCompaction);
    }
}
