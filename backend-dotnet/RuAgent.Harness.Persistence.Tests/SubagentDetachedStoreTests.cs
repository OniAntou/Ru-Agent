using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

/// <summary>Detached children (ADR 0045): session ownership after a normal root end.</summary>
public sealed class SubagentDetachedStoreTests
{
    [Fact]
    public async Task A_normal_root_end_cancels_attached_children_and_leaves_detached_ones_to_the_session()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var events = fixture.CreateEventStore();
        var agents = new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        var root = fixture.NewRun(1, 1, 1);
        await events.TryCreateRunAsync(root, default);
        await agents.CreateAsync(new("agent_attached", root.Run.RunId, "spawn_a", SubagentRole.Review, fixture.NewRun(2, 2, 1)), default);
        var created = await agents.CreateAsync(new("agent_detached", root.Run.RunId, "spawn_d", SubagentRole.Coding,
            fixture.NewRun(3, 3, 1), Detached: true), default);
        Assert.True(created.Detached);

        await agents.CancelAttachedAsync(root.Run.RunId, default);

        var listed = (await agents.ListAsync(root.Run.RunId, default)).ToDictionary(a => a.AgentId);
        Assert.True(listed["agent_attached"].Canceled);
        Assert.False(listed["agent_attached"].Detached);
        Assert.False(listed["agent_detached"].Canceled);
        Assert.True(listed["agent_detached"].Detached);
        Assert.False(await agents.IsFamilyCanceledAsync(root.Run.RunId, default));
        Assert.Single(await agents.ListAttemptRunIdsAsync("agent_detached", default));

        await EndAsync(fixture, root.Run.RunId);
        var later = fixture.NewRun(4, 1, 1, idempotencyHashCharacter: 'c');
        await events.TryCreateRunAsync(later, default);
        var other = fixture.NewRun(5, 5, 1);
        await events.TryCreateRunAsync(other, default);

        var visible = await agents.FindVisibleAsync(later.Run.RunId, "agent_detached", default);
        Assert.Equal(root.Run.RunId, visible!.RootRunId);
        Assert.Null(await agents.FindVisibleAsync(later.Run.RunId, "agent_attached", default));
        Assert.Null(await agents.FindVisibleAsync(other.Run.RunId, "agent_detached", default));
        Assert.Equal(["agent_detached"], (await agents.ListInheritedDetachedAsync(later.Run.RunId, 8, default)).Select(a => a.AgentId));
        Assert.Empty(await agents.ListInheritedDetachedAsync(root.Run.RunId, 8, default));
        Assert.Empty(await agents.ListInheritedDetachedAsync(other.Run.RunId, 8, default));
    }

    [Fact]
    public async Task An_explicit_family_cancel_reaches_detached_children()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var agents = new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        var root = fixture.NewRun(1, 1, 1);
        await fixture.CreateEventStore().TryCreateRunAsync(root, default);
        await agents.CreateAsync(new("agent_detached", root.Run.RunId, "spawn_d", SubagentRole.Review,
            fixture.NewRun(2, 2, 1), Detached: true), default);

        await agents.CancelFamilyAsync(root.Run.RunId, default);

        Assert.True(Assert.Single(await agents.ListAsync(root.Run.RunId, default)).Canceled);
        Assert.True(await agents.IsFamilyCanceledAsync(root.Run.RunId, default));
    }

    [Fact]
    public async Task A_replayed_detached_spawn_keeps_its_detached_mark()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var agents = new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        var root = fixture.NewRun(1, 1, 1);
        await fixture.CreateEventStore().TryCreateRunAsync(root, default);
        var child = fixture.NewRun(2, 2, 1);
        await agents.CreateAsync(new("agent_detached", root.Run.RunId, "spawn_d", SubagentRole.Review, child, Detached: true), default);

        var replay = await agents.CreateAsync(new("agent_detached", root.Run.RunId, "spawn_d", SubagentRole.Review, child, Detached: true), default);

        Assert.True(replay.Detached);
        Assert.True((await agents.FindOwnerAsync(child.Run.SessionId, default))!.Detached);
    }

    private static async Task EndAsync(HarnessStoreTestFixture fixture, RunId runId)
    {
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE agent_runs SET state=$state, terminal_at_utc=updated_at_utc WHERE run_id=$run";
        command.Parameters.AddWithValue("$state", (int)HarnessRunState.Completed);
        command.Parameters.AddWithValue("$run", runId.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }
}
