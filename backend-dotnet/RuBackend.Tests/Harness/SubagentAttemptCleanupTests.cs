using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using RuBackend.Harness;
using RuBackend.Harness.Subagents;
using RuBackend.Harness.Workspace;
using RuAgent.Harness.Core.Policy;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class SubagentAttemptCleanupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Attempt_release_closes_its_internal_authority_session_even_without_owner_metadata(bool missingOwner)
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        using var execution = new InProcessExecutionFixture(fixture.RootPath);
        var root = await fixture.SeedRunAsync(new(HarnessRunState.Running));
        var identity = await new WindowsWorkspaceIdentityResolver().ResolveAsync(fixture.RootPath, default);
        var policy = PolicySnapshotCanonicalizer.Create(1, root.Run.Preset, identity, [], DateTimeOffset.UtcNow);
        var agents = new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        var child = new AgentRun(new("run_" + new string('b', 32)), new(new string('b', 32)), root.Run.WorkspaceId,
            new(HarnessRunState.Created), 0, 1, 1, 0, 0, root.Run.Preset, root.Run.PolicySnapshotVersion,
            root.Run.PolicySnapshotSha256, BackendHarnessTestFixture.Timestamp, BackendHarnessTestFixture.Timestamp);
        await agents.CreateAsync(new("agent_one", root.Run.RunId, "spawn_one", SubagentRole.Review,
            new(child, [new(new("evt_" + new string('b', 32)), RuAgent.Harness.Core.Persistence.HarnessEventKind.RunStateChanged)], [], [],
                new(new string('a', 64), new string('b', 64), 1))), default);
        await execution.Factory.CreateAsync(fixture.RootPath, policy, root.Run.RunId, root.Run.SessionId.Value, default);
        await execution.Factory.CreateAsync(fixture.RootPath, policy, child.RunId, child.SessionId.Value, default);
        Assert.True(execution.Session.Owns(child.SessionId.Value));
        if (missingOwner)
        {
            await using var connection = await fixture.Options.OpenConnectionAsync(default);
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM agent_subagents WHERE agent_id='agent_one'";
            await command.ExecuteNonQueryAsync();
        }
        using var scheduler = new SubagentScheduler(fixture.EventStore, agents,
            new SqliteSubagentScopeStore(fixture.Options, new WorkspaceJournalProtector(fixture.KeyStore)),
            new AgentRuntimeRootRegistry(), new SystemTimeProvider(), execution.Factory);
        await scheduler.CancelChildrenBeforeReleaseAsync(child.RunId, new NoChildren(), default);
        Assert.False(execution.Session.Owns(child.SessionId.Value));
        Assert.True(execution.Session.Owns(root.Run.SessionId.Value));
    }

    private sealed class NoChildren : IAgentRuntimeRunCleanup
    {
        public Task CancelAndRemoveRunAsync(RunId runId, CancellationToken ct) => throw new InvalidOperationException("unexpected_child");
    }
}
