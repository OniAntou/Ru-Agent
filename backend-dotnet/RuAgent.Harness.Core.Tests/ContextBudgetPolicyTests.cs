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

    [Fact]
    public void CreateBudgetPolicy_flows_settings_ratios_into_evaluate_thresholds()
    {
        // Default reserves are 256 + 64, so maxPrompt 1320 gives
        // usable 1000: trigger = floor(1000 * 0.75) = 750,
        // target = floor(1000 * 0.50) = 500.
        var policy = AgentRunCoordinator.CreateBudgetPolicy(0.75, 0.50);

        var decision = policy.Evaluate(751, 1320, ContextCompactionStrategy.Normal);

        Assert.Equal(750, decision.SoftTriggerTokens);
        Assert.Equal(500, decision.TargetTokens);
        Assert.True(decision.NeedsCompaction);
    }

    [Theory]
    [InlineData(1.0, 0.50)] // spec: trigger < 1
    [InlineData(1.5, 0.50)]
    [InlineData(0.0, 0.50)]
    [InlineData(0.50, 0.50)] // target must be strictly below trigger
    [InlineData(0.50, 0.62)] // inverted pair
    [InlineData(0.75, 0.0)]
    [InlineData(0.75, 1.5)]
    public void CreateBudgetPolicy_fails_closed_to_defaults_on_invalid_pairs(
        double triggerRatio,
        double targetRatio)
    {
        var policy = AgentRunCoordinator.CreateBudgetPolicy(triggerRatio, targetRatio);
        var expected = new ContextBudgetPolicy();

        // usable = 1200 - 100 - 0 = 1100 with the default 100/0 reserve.
        var actual = policy.Evaluate(1, 1200, ContextCompactionStrategy.Normal);
        var baseline = expected.Evaluate(1, 1200, ContextCompactionStrategy.Normal);

        Assert.Equal(baseline.SoftTriggerTokens, actual.SoftTriggerTokens);
        Assert.Equal(baseline.TargetTokens, actual.TargetTokens);
    }

    [Fact]
    public void CreateBudgetPolicy_accepts_trigger_just_below_one()
    {
        var policy = AgentRunCoordinator.CreateBudgetPolicy(0.99, 0.50);

        // usable = 1320 - 256 - 64 = 1000; trigger = floor(1000 * 0.99) = 990.
        var decision = policy.Evaluate(991, 1320, ContextCompactionStrategy.Normal);

        Assert.Equal(990, decision.SoftTriggerTokens);
        Assert.True(decision.NeedsCompaction);
    }

    [Theory]
    [InlineData(double.NaN, 0.50)]
    [InlineData(double.PositiveInfinity, 0.50)]
    [InlineData(0.75, double.NaN)]
    [InlineData(0.75, double.NegativeInfinity)]
    public void CreateBudgetPolicy_fails_closed_on_non_finite_ratios(
        double triggerRatio,
        double targetRatio)
    {
        var expected = new ContextBudgetPolicy().Evaluate(1, 1200, ContextCompactionStrategy.Normal);
        var actual = AgentRunCoordinator.CreateBudgetPolicy(triggerRatio, targetRatio)
            .Evaluate(1, 1200, ContextCompactionStrategy.Normal);

        Assert.Equal(expected.SoftTriggerTokens, actual.SoftTriggerTokens);
        Assert.Equal(expected.TargetTokens, actual.TargetTokens);
    }
}
