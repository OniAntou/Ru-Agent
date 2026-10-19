using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Harness;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed partial class SubagentScopeStoreTests
{
    [Fact]
    public async Task Scope_is_encrypted_immutable_owned_and_survives_restart()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var workspace = await fixture.RegisterWorkspaceAsync(1);
        var policy = new BrokerPolicySnapshotWire(1, "standard", new(workspace.WorkspaceId.Value,
            workspace.VolumeSerialNumber, workspace.RootFileId, workspace.DisplayIdentityHash), [],
            HarnessStoreTestFixture.Timestamp.ToString("O"));
        var root = WithPolicy(fixture.NewRun(1, 1, 1), policy);
        var child = WithPolicy(fixture.NewRun(2, 2, 1), policy);
        await fixture.CreateEventStore().TryCreateRunAsync(root, default);
        await new SqliteSubagentStore(fixture.Options, fixture.PayloadStore).CreateAsync(
            new("agent_one", root.Run.RunId, "spawn_one", SubagentRole.Review, child), default);
        var crypto = new WorkspaceJournalProtector(new FakeHarnessKeyStore());
        var store = new SqliteSubagentScopeStore(fixture.Options, crypto);
        var scope = new SubagentRuntimeScope(fixture.RootPath, fixture.RootPath,
            new(root.Run.PolicySnapshotVersion, root.Run.PolicySnapshotSha256),
            BrokerPolicyContracts.WriteSnapshot(policy).ToJsonString(), null, BrokerPolicyContracts.WriteSnapshot(policy).ToJsonString());
        await store.SaveAsync(root.Run.RunId, child.Run.RunId, scope, default);
        await store.SaveAsync(root.Run.RunId, child.Run.RunId, scope, default);
        Assert.Equal(scope, await new SqliteSubagentScopeStore(fixture.Options, crypto).GetAsync(child.Run.RunId, default));
        await Assert.ThrowsAsync<HarnessStoreException>(() => store.SaveAsync(root.Run.RunId, child.Run.RunId,
            scope with { WorkspaceRoot = Path.Combine(fixture.RootPath, "other") }, default));
        await Assert.ThrowsAsync<HarnessStoreException>(() => store.SaveAsync(HarnessStoreTestFixture.Run(3), child.Run.RunId, scope, default));
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT ciphertext FROM agent_subagent_scopes";
        Assert.DoesNotContain(fixture.RootPath, System.Text.Encoding.UTF8.GetString(Assert.IsType<byte[]>(await query.ExecuteScalarAsync())));
    }

    internal static NewAgentRun WithPolicy(NewAgentRun request, BrokerPolicySnapshotWire policy)
    {
        var r = request.Run;
        var run = new AgentRun(r.RunId, r.SessionId, r.WorkspaceId, r.Status, r.Version, r.LastSequence,
            r.BudgetWindow, r.ModelStepsInWindow, r.ToolCallsInWindow, r.Preset, policy.Version,
            BrokerPolicyContracts.ComputeCanonicalSnapshotHash(policy), r.CreatedAt, r.UpdatedAt);
        return new(run, request.Events, request.Payloads, request.ConversationOutbox, request.CreationIdentity);
    }
}
