using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentRunDeltaDeduplicatorTests
{
    [Fact]
    public void Assistant_delta_identity_is_run_step_and_sequence()
    {
        var deduplicator = new AgentRunDeltaDeduplicator();

        Assert.True(deduplicator.TryAcceptAssistant("run-a", "step-1", 2));
        Assert.False(deduplicator.TryAcceptAssistant("run-a", "step-1", 2));
        Assert.True(deduplicator.TryAcceptAssistant("run-a", "step-1", 3));
        Assert.True(deduplicator.TryAcceptAssistant("run-a", "step-2", 2));
        Assert.True(deduplicator.TryAcceptAssistant("run-b", "step-1", 2));
    }

    [Fact]
    public void Tool_output_identity_is_run_execution_and_sequence()
    {
        var deduplicator = new AgentRunDeltaDeduplicator();

        Assert.True(deduplicator.TryAcceptToolOutput("run-a", "exec-1", 2));
        Assert.False(deduplicator.TryAcceptToolOutput("run-a", "exec-1", 2));
        Assert.True(deduplicator.TryAcceptToolOutput("run-a", "exec-1", 3));
        Assert.True(deduplicator.TryAcceptToolOutput("run-b", "exec-1", 2));
    }

    [Fact]
    public void New_feed_clears_deduplication_history()
    {
        var deduplicator = new AgentRunDeltaDeduplicator();
        Assert.True(deduplicator.TryAcceptAssistant("run-a", "step-1", 2));

        deduplicator.Reset();

        Assert.True(deduplicator.TryAcceptAssistant("run-a", "step-1", 2));
        Assert.True(deduplicator.TryAcceptToolOutput("run-a", "exec-1", 2));
    }
}
