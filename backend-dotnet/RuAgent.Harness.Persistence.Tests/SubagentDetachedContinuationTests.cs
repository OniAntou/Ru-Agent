using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

/// <summary>
/// A detached child outlives its creating root (ADR 0045). A later root of the same session
/// may message it; the follow-up keeps the creating root's family and policy.
/// </summary>
public sealed class SubagentDetachedContinuationTests
{
    [Fact]
    public async Task A_later_root_can_queue_a_followup_and_resume_a_detached_child()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var events = fixture.CreateEventStore();
        var root = fixture.NewRun(1, 1, 1);
        await events.TryCreateRunAsync(root, default);
        var agents = new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        var child = fixture.NewRun(2, 2, 1);
        await agents.CreateAsync(new("agent_one", root.Run.RunId, "spawn_one", SubagentRole.Review, child,
            Detached: true), default);
        await new SqliteAgentRunProviderSelectionStore(fixture.Options).PinAsync(child.Run.RunId,
            new("Custom OpenAI", "custom", "chat_completions", "https://example.invalid/v1/", "original-model", null, "{}", null), default);
        await TerminateAsync(fixture, child.Run.RunId, HarnessRunState.Completed);
        await TerminateAsync(fixture, root.Run.RunId, HarnessRunState.Completed);
        var later = fixture.NewRun(3, 1, 1, idempotencyHashCharacter: 'e', requestHashCharacter: 'f');
        var created = await events.TryCreateRunAsync(later, default);
        Assert.Equal(CreateRunOutcome.Created, created.Outcome);
        var latest = await agents.GetLatestAttemptAsync(root.Run.RunId, "agent_one", default);
        Assert.True(latest!.Agent.Detached);
        Assert.False(latest.Agent.Canceled);

        await agents.AppendMessageAsync(later.Run.RunId, root.Run.RunId, "agent_one", "message_one",
            "Review the empty case", default);

        var pending = Assert.Single(await agents.ReadPendingMessagesAsync(root.Run.RunId, "agent_one", default));
        Assert.Equal("message_one", pending.MessageId);
        var candidate = Assert.Single(await agents.ListContinuationCandidatesAsync(null, default));
        Assert.Equal(root.Run.RunId.Value, candidate.RootRunId.Value);
        Assert.Equal("agent_one", candidate.AgentId);

        var next = fixture.NewRun(4, 2, 1, idempotencyHashCharacter: 'c', requestHashCharacter: 'd');
        var attempt = await agents.CreateContinuationAsync(root.Run.RunId, "agent_one", child.Run.RunId, next, default);
        Assert.NotNull(attempt);
        Assert.Equal(2, attempt!.AttemptNumber);
        Assert.Equal(child.Run.SessionId, attempt.Run.SessionId);
        // The message is scheduled onto the new attempt but is not consumed until the
        // child reads it at its next step.
        var scheduled = Assert.Single(await agents.ReadPendingMessagesAsync(root.Run.RunId, "agent_one", default));
        Assert.Null(scheduled.ContinuationErrorCode);
    }

    [Fact]
    public async Task An_attached_child_of_a_finished_root_still_refuses_a_followup()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var events = fixture.CreateEventStore();
        var root = fixture.NewRun(1, 1, 1);
        await events.TryCreateRunAsync(root, default);
        var agents = new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        var child = fixture.NewRun(2, 2, 1);
        await agents.CreateAsync(new("agent_one", root.Run.RunId, "spawn_one", SubagentRole.Review, child), default);
        await TerminateAsync(fixture, child.Run.RunId, HarnessRunState.Completed);
        await TerminateAsync(fixture, root.Run.RunId, HarnessRunState.Completed);

        var refused = await Assert.ThrowsAsync<HarnessStoreException>(() => agents.AppendMessageAsync(
            root.Run.RunId, root.Run.RunId, "agent_one", "message_one", "Try again", default));
        Assert.Equal("subagent_root_inactive", refused.Code);
        Assert.Empty(await agents.ReadPendingMessagesAsync(root.Run.RunId, "agent_one", default));
        Assert.Empty(await agents.ListContinuationCandidatesAsync(null, default));
        var next = fixture.NewRun(3, 2, 1, idempotencyHashCharacter: 'c', requestHashCharacter: 'd');
        var continuation = await Assert.ThrowsAsync<HarnessStoreException>(() => agents.CreateContinuationAsync(
            root.Run.RunId, "agent_one", child.Run.RunId, next, default));
        Assert.Equal("subagent_root_inactive", continuation.Code);
    }

    [Fact]
    public async Task A_foreign_session_or_a_cancelled_family_cannot_reach_the_child()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var events = fixture.CreateEventStore();
        var root = fixture.NewRun(1, 1, 1);
        await events.TryCreateRunAsync(root, default);
        var agents = new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        var child = fixture.NewRun(2, 2, 1);
        await agents.CreateAsync(new("agent_one", root.Run.RunId, "spawn_one", SubagentRole.Review, child,
            Detached: true), default);
        await TerminateAsync(fixture, child.Run.RunId, HarnessRunState.Completed);
        await TerminateAsync(fixture, root.Run.RunId, HarnessRunState.Completed);
        var foreign = fixture.NewRun(3, 3, 1);
        await events.TryCreateRunAsync(foreign, default);

        var notVisible = await Assert.ThrowsAsync<HarnessStoreException>(() => agents.AppendMessageAsync(
            foreign.Run.RunId, root.Run.RunId, "agent_one", "message_one", "Try again", default));
        Assert.Equal("subagent_not_found", notVisible.Code);

        await agents.CancelFamilyAsync(root.Run.RunId, default);
        var cancelled = await Assert.ThrowsAsync<HarnessStoreException>(() => agents.AppendMessageAsync(
            root.Run.RunId, root.Run.RunId, "agent_one", "message_one", "Try again", default));
        Assert.Equal("subagent_root_inactive", cancelled.Code);
    }

    private static async Task TerminateAsync(HarnessStoreTestFixture fixture, RunId run, HarnessRunState state)
    {
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE agent_runs SET state=$state,terminal_at_utc=updated_at_utc WHERE run_id=$run";
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue("$run", run.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }
}
