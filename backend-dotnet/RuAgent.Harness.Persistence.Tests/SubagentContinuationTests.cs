using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class SubagentContinuationTests
{
    [Fact]
    public async Task Coding_followup_waits_for_sealed_result_before_reusing_the_worktree()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var events = fixture.CreateEventStore();
        var root = fixture.NewRun(1, 1, 1);
        var child = fixture.NewRun(2, 2, 1);
        await events.TryCreateRunAsync(root, default);
        var agents = new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        await agents.CreateAsync(new("agent_one", root.Run.RunId, "spawn_one", SubagentRole.Coding, child), default);
        await new SqliteAgentRunProviderSelectionStore(fixture.Options).PinAsync(child.Run.RunId,
            new("Custom OpenAI", "custom", "chat_completions", "https://example.invalid/v1/", "original-model", null, "{}", null), default);
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE agent_runs SET state=$state,terminal_at_utc=updated_at_utc WHERE run_id=$run";
        command.Parameters.AddWithValue("$state", (int)HarnessRunState.Completed);
        command.Parameters.AddWithValue("$run", child.Run.RunId.Value);
        await command.ExecuteNonQueryAsync();
        await agents.AppendMessageAsync(root.Run.RunId, "agent_one", "message_one", "Follow up", default);
        var next = fixture.NewRun(3, 2, 1, idempotencyHashCharacter: 'd');
        var error = await Assert.ThrowsAsync<HarnessStoreException>(() => agents.CreateContinuationAsync(root.Run.RunId,
            "agent_one", child.Run.RunId, next, default));
        Assert.Equal("subagent_result_pending", error.Code);
        Assert.Null(await events.GetRunAsync(next.Run.RunId, default));
        Assert.Single(await agents.ReadPendingMessagesAsync(root.Run.RunId, "agent_one", default));
        await new SqliteSubagentResultStore(fixture.Options, fixture.PayloadStore).PublishFailureAsync(root.Run.RunId,
            "agent_one", child.Run.RunId, "subagent_snapshot_changed", default);
        Assert.Equal(next.Run.RunId, (await agents.CreateContinuationAsync(root.Run.RunId, "agent_one", child.Run.RunId, next, default))!.Run.RunId);
    }

    [Theory]
    [InlineData("missing_pin")]
    [InlineData("canceled")]
    [InlineData("exhausted")]
    [InlineData("foreign_session")]
    public async Task Invalid_continuation_does_not_create_run_or_consume_message(string failure)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var events = fixture.CreateEventStore();
        var root = fixture.NewRun(1, 1, 1);
        await events.TryCreateRunAsync(root, default);
        var agents = new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        var first = fixture.NewRun(2, 2, 1);
        await agents.CreateAsync(new("agent_one", root.Run.RunId, "spawn_one", SubagentRole.Review, first), default);
        if (failure != "missing_pin")
            await new SqliteAgentRunProviderSelectionStore(fixture.Options).PinAsync(first.Run.RunId,
                new("Custom OpenAI", "custom", "chat_completions", "https://example.invalid/v1/", "original-model", null, "{}", null), default);
        await events.CommitAsync(SubagentAdmission.CreateTerminalCommit(first.Run,
            new("evt_" + new string('f', 32)), false, "fixture_failure", HarnessStoreTestFixture.Timestamp), default);
        await agents.AppendMessageAsync(root.Run.RunId, "agent_one", "message_one", "Try again", default);
        if (failure == "canceled") await agents.CancelFamilyAsync(root.Run.RunId, default);
        if (failure == "exhausted")
        {
            await using var connection = fixture.CreateRawConnection();
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE agent_subagent_budgets SET model_steps=128";
            await command.ExecuteNonQueryAsync();
        }
        var next = fixture.NewRun(3, failure == "foreign_session" ? 3 : 2, 1,
            idempotencyHashCharacter: 'c', requestHashCharacter: 'd');
        await Assert.ThrowsAsync<HarnessStoreException>(() => agents.CreateContinuationAsync(
            root.Run.RunId, "agent_one", first.Run.RunId, next, default));
        Assert.Null(await events.GetRunAsync(next.Run.RunId, default));
        Assert.Single(await agents.ReadPendingMessagesAsync(root.Run.RunId, "agent_one", default));
    }

    [Fact]
    public async Task Followup_creates_one_attempt_with_same_internal_session_and_pin_without_resetting_family_budget()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var events = fixture.CreateEventStore();
        var root = fixture.NewRun(1, 1, 1);
        await events.TryCreateRunAsync(root, default);
        var agents = new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        var first = fixture.NewRun(2, 2, 1);
        await agents.CreateAsync(new("agent_one", root.Run.RunId, "spawn_one", SubagentRole.Review, first), default);
        var pins = new SqliteAgentRunProviderSelectionStore(fixture.Options);
        var pin = (await pins.PinAsync(first.Run.RunId, new("Custom OpenAI", "custom", "chat_completions",
            "https://example.invalid/v1/", "original-model", null, "{}", null), default)).Selection;
        var budgets = new SqliteSubagentBudgetStore(fixture.Options);
        Assert.True(await budgets.ReserveAmountAsync(root.Run.RunId, 1, "model_before", SubagentBudgetKind.ModelStep, 1, 0, 0, default));
        await events.CommitAsync(SubagentAdmission.CreateTerminalCommit(first.Run,
            new("evt_" + new string('f', 32)), false, "fixture_failure", HarnessStoreTestFixture.Timestamp), default);
        await agents.AppendMessageAsync(root.Run.RunId, "agent_one", "message_one", "Try the empty case", default);
        var next = fixture.NewRun(3, 2, 1, idempotencyHashCharacter: 'c', requestHashCharacter: 'd');
        var attempts = await Task.WhenAll(
            agents.CreateContinuationAsync(root.Run.RunId, "agent_one", first.Run.RunId, next, default),
            agents.CreateContinuationAsync(root.Run.RunId, "agent_one", first.Run.RunId, next, default));
        Assert.All(attempts, attempt => Assert.Equal(next.Run.RunId, attempt!.Run.RunId));
        Assert.Equal(2, attempts[0]!.AttemptNumber);
        Assert.Equal(first.Run.SessionId, attempts[0]!.Run.SessionId);
        Assert.Equal(pin, (await pins.GetAsync(next.Run.RunId, default))!.Selection);
        // If the new attempt fails before consuming its message, retrying that
        // same send still must not create yet another attempt.
        await events.CommitAsync(SubagentAdmission.CreateTerminalCommit(next.Run,
            new("evt_" + new string('e', 32)), false, "fixture_failure", HarnessStoreTestFixture.Timestamp), default);
        await agents.AppendMessageAsync(root.Run.RunId, "agent_one", "message_one", "Try the empty case", default);
        var third = fixture.NewRun(4, 2, 1, idempotencyHashCharacter: 'e', requestHashCharacter: 'f');
        Assert.Null(await agents.CreateContinuationAsync(root.Run.RunId, "agent_one", next.Run.RunId, third, default));
        Assert.Equal(next.Run.RunId, (await agents.GetLatestAttemptAsync(root.Run.RunId, "agent_one", default))!.Run.RunId);
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT model_steps FROM agent_subagent_budgets WHERE root_run_id=$root AND window=1";
        command.Parameters.AddWithValue("$root", root.Run.RunId.Value);
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }
}
