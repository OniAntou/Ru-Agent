using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentRunDeltaDeduplicatorTests
{
    [Fact]
    public void Feed_state_resets_frame_identity_but_keeps_lifetime_drop_counts()
    {
        var state = new AgentRunFeedState { TerminalStatus = "completed" };
        state.RecordIngressDrop();
        state.RecordUiGateDrop();
        state.RecordDuplicateDrop();
        state.RecordCancelDrop();

        Assert.Equal(new AgentTransientDropReport(1, 1, 1, 1), state.Drops);
        Assert.Equal(2, state.Drops.Lost);
        Assert.True(state.TryAcceptAssistant("run-a", "step-a", 1));

        state.ResetForRun();

        Assert.Equal(new AgentTransientDropReport(1, 1, 1, 1), state.Drops);
        Assert.Equal(2, state.Drops.Lost);
        Assert.Null(state.TerminalStatus);
        Assert.True(state.TryAcceptAssistant("run-a", "step-a", 1));
    }

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
