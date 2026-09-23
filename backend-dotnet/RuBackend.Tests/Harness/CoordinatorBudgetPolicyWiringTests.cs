using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuBackend.Business.Settings;
using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class CoordinatorBudgetPolicyWiringTests
{
    [Fact]
    public async Task ResolveBudgetPolicyAsync_flows_settings_ratios_into_thresholds()
    {
        var store = new StubSettingsStore(Snapshot(0.75, 0.50));

        var policy = await AgentRuntimeCoordinatorFactory.ResolveBudgetPolicyAsync(
            store,
            CancellationToken.None);

        // usable = 300 - 256 - 64 = ... uses policy defaults for reserves;
        // assert against the directly built policy instead of hard-coding.
        var expected = AgentRunCoordinator.CreateBudgetPolicy(0.75, 0.50)
            .Evaluate(1, 1200, ContextCompactionStrategy.Normal);
        var actual = policy.Evaluate(1, 1200, ContextCompactionStrategy.Normal);
        Assert.Equal(expected.SoftTriggerTokens, actual.SoftTriggerTokens);
        Assert.Equal(expected.TargetTokens, actual.TargetTokens);
    }

    [Fact]
    public async Task ResolveBudgetPolicyAsync_fails_closed_when_settings_read_throws()
    {
        var policy = await AgentRuntimeCoordinatorFactory.ResolveBudgetPolicyAsync(
            new ThrowingSettingsStore(),
            CancellationToken.None);

        var expected = new ContextBudgetPolicy()
            .Evaluate(1, 1200, ContextCompactionStrategy.Normal);
        var actual = policy.Evaluate(1, 1200, ContextCompactionStrategy.Normal);
        Assert.Equal(expected.SoftTriggerTokens, actual.SoftTriggerTokens);
        Assert.Equal(expected.TargetTokens, actual.TargetTokens);
    }

    [Fact]
    public async Task ResolveBudgetPolicyAsync_fails_closed_on_trigger_at_one()
    {
        var store = new StubSettingsStore(Snapshot(1.0, 0.50));

        var policy = await AgentRuntimeCoordinatorFactory.ResolveBudgetPolicyAsync(
            store,
            CancellationToken.None);

        var expected = new ContextBudgetPolicy()
            .Evaluate(1, 1200, ContextCompactionStrategy.Normal);
        var actual = policy.Evaluate(1, 1200, ContextCompactionStrategy.Normal);
        Assert.Equal(expected.SoftTriggerTokens, actual.SoftTriggerTokens);
        Assert.Equal(expected.TargetTokens, actual.TargetTokens);
    }

    private static SettingsSnapshot Snapshot(double trigger, double target) => new(
        new JsonObject(),
        "OpenAI",
        string.Empty,
        "default",
        "http://localhost:8000/v1",
        string.Empty,
        string.Empty,
        trigger,
        target);

    private sealed class StubSettingsStore(SettingsSnapshot snapshot) : ISettingsStore
    {
        public Task<SettingsSnapshot> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(snapshot);

        public Task SaveAsync(SettingsSnapshot candidate, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class ThrowingSettingsStore : ISettingsStore
    {
        public Task<SettingsSnapshot> ReadAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("settings_unavailable");

        public Task SaveAsync(SettingsSnapshot candidate, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
