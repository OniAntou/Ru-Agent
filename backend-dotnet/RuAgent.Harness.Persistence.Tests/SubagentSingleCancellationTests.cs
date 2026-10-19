using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class SubagentSingleCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Single_cancel_is_root_bound_idempotent_and_does_not_cancel_siblings(bool concurrentMessage)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var workspace = await fixture.RegisterWorkspaceAsync(1);
        var policy = new BrokerPolicySnapshotWire(1, "standard", new(workspace.WorkspaceId.Value,
            workspace.VolumeSerialNumber, workspace.RootFileId, workspace.DisplayIdentityHash), [], HarnessStoreTestFixture.Timestamp.ToString("O"));
        var events = fixture.CreateEventStore();
        var root = SubagentScopeStoreTests.WithPolicy(fixture.NewRun(1, 1, 1, status: new(HarnessRunState.Running)), policy);
        var other = fixture.NewRun(4, 4, 1);
        await events.TryCreateRunAsync(root, default);
        await events.TryCreateRunAsync(other, default);
        var agents = new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        var first = SubagentScopeStoreTests.WithPolicy(fixture.NewRun(2, 2, 1), policy);
        await agents.CreateAsync(new("agent_one", root.Run.RunId, "spawn_one", SubagentRole.Review, first), default);
        var second = SubagentScopeStoreTests.WithPolicy(fixture.NewRun(3, 3, 1), policy);
        await agents.CreateAsync(new("agent_two", root.Run.RunId, "spawn_two", SubagentRole.Review, second), default);
        foreach (var child in new[] { first, second })
        {
            await new SqliteAgentRunProviderSelectionStore(fixture.Options).PinAsync(child.Run.RunId,
                new("Custom OpenAI", "custom_openai", "chat_completions", "https://example.invalid/v1/", "fixture-model", null, "{}", null), default);
            await fixture.PayloadStore.CaptureBaseContextAsync(child.Run.RunId, [], HarnessStoreTestFixture.Timestamp, default);
            await new SqliteSubagentScopeStore(fixture.Options, new WorkspaceJournalProtector(new FakeHarnessKeyStore())).SaveAsync(
                root.Run.RunId, child.Run.RunId, new(fixture.RootPath, fixture.RootPath,
                    new(policy.Version, BrokerPolicyContracts.ComputeCanonicalSnapshotHash(policy)),
                    BrokerPolicyContracts.WriteSnapshot(policy).ToJsonString(), null, BrokerPolicyContracts.WriteSnapshot(policy).ToJsonString()), default);
        }
        var denied = await Assert.ThrowsAsync<HarnessStoreException>(() => agents.CancelAsync(other.Run.RunId, "agent_one", default));
        Assert.Equal("subagent_not_found", denied.Code);
        Assert.All(await agents.ListAsync(root.Run.RunId, default), a => Assert.False(a.Canceled));
        async Task Send()
        {
            try { await agents.AppendMessageAsync(root.Run.RunId, "agent_one", "racing_message", "Late message", default); }
            catch (HarnessStoreException error) { Assert.Equal("subagent_root_inactive", error.Code); }
        }
        if (concurrentMessage) await Task.WhenAll(Send(), agents.CancelAsync(root.Run.RunId, "agent_one", default));
        else await agents.CancelAsync(root.Run.RunId, "agent_one", default);
        Assert.Equal(first.Run.RunId, Assert.Single(await agents.CancelAsync(root.Run.RunId, "agent_one", default)));
        var owned = await agents.ListAsync(root.Run.RunId, default);
        Assert.True(Assert.Single(owned, a => a.AgentId == "agent_one").Canceled);
        Assert.False(Assert.Single(owned, a => a.AgentId == "agent_two").Canceled);
        Assert.False(await agents.TryAdmitAsync("agent_one", HarnessStoreTestFixture.Timestamp, default));
        Assert.True(await agents.TryAdmitAsync("agent_two", HarnessStoreTestFixture.Timestamp, default));
        await Assert.ThrowsAsync<HarnessStoreException>(() => agents.AppendMessageAsync(root.Run.RunId, "agent_one", "late_message", "No revival", default));
        await agents.AppendMessageAsync(root.Run.RunId, "agent_two", "valid_message", "Still active", default);
        Assert.Equal(root.Run.State, (await events.GetRunAsync(root.Run.RunId, default))!.State);
    }
}
