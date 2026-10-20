using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuBackend.Business.Settings;
using RuBackend.Harness;
using Microsoft.Extensions.Logging;
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

    [Fact]
    public async Task Expected_settings_failure_logs_only_a_safe_type_and_uses_defaults()
    {
        var logger = new RecordingLogger();
        var policy = await AgentRuntimeCoordinatorFactory.ResolveBudgetPolicyAsync(
            new ThrowingSettingsStore(new IOException("synthetic-secret-do-not-log")), default, logger);

        Assert.Equal(new ContextBudgetPolicy().Evaluate(1, 1200, ContextCompactionStrategy.Normal),
            policy.Evaluate(1, 1200, ContextCompactionStrategy.Normal));
        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Messages).Level);
        Assert.Contains("IOException", logger.Messages[0].Text);
        Assert.DoesNotContain("synthetic-secret-do-not-log", logger.Messages[0].Text);
        Assert.Null(logger.Messages[0].Error);
    }

    [Fact]
    public async Task Cancellation_and_unexpected_programming_errors_are_not_budget_fallbacks()
    {
        var logger = new RecordingLogger();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            AgentRuntimeCoordinatorFactory.ResolveBudgetPolicyAsync(
                new ThrowingSettingsStore(new OperationCanceledException()), default, logger));
        await Assert.ThrowsAsync<NullReferenceException>(() =>
            AgentRuntimeCoordinatorFactory.ResolveBudgetPolicyAsync(
                new ThrowingSettingsStore(new NullReferenceException()), default, logger));
        Assert.Empty(logger.Messages);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Text, Exception? Error)> Messages { get; } = [];
        public bool IsEnabled(LogLevel level) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add((level, formatter(state, exception), exception));
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

    private sealed class ThrowingSettingsStore(Exception? error = null) : ISettingsStore
    {
        public Task<SettingsSnapshot> ReadAsync(CancellationToken cancellationToken) =>
            throw error ?? new InvalidOperationException("settings_unavailable");

        public Task SaveAsync(SettingsSnapshot candidate, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
