using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using RuBackend.Harness;
using RuBackend.Harness.Subagents;
using RuBackend.Harness.Workspace;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class SubagentCodingPublicationFairnessTests
{
    [Fact]
    public async Task Deferred_first_child_does_not_starve_later_coding_results()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        using var execution = new InProcessExecutionFixture(fixture.RootPath);
        var root = await fixture.SeedRunAsync(new(HarnessRunState.Running));
        var agents = new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        var ids = new List<RunId>();
        foreach (var (suffix, name) in new[] { ('b', "agent_one"), ('c', "agent_two") })
        {
            var child = new AgentRun(new("run_" + new string(suffix, 32)), new(new string(suffix, 32)), root.Run.WorkspaceId,
                new(HarnessRunState.Created), 0, 1, 1, 0, 0, root.Run.Preset, root.Run.PolicySnapshotVersion,
                root.Run.PolicySnapshotSha256, BackendHarnessTestFixture.Timestamp, BackendHarnessTestFixture.Timestamp);
            ids.Add(child.RunId);
            await agents.CreateAsync(new(name, root.Run.RunId, "spawn_" + name, SubagentRole.Coding,
                new(child, [new(new("evt_" + new string(suffix, 32)), HarnessEventKind.RunStateChanged)], [], [],
                    new(new string('a', 64), new string('b', 64), 1))), default);
        }
        await using var connection = await fixture.Options.OpenConnectionAsync(default);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE agent_runs SET state=7,terminal_at_utc=updated_at_utc WHERE run_id IN($one,$two)";
        command.Parameters.AddWithValue("$one", ids[0].Value);
        command.Parameters.AddWithValue("$two", ids[1].Value);
        await command.ExecuteNonQueryAsync();
        var roots = new AgentRuntimeRootRegistry();
        var policy = PolicySnapshotCanonicalizer.Create(1, root.Run.Preset,
            await new WindowsWorkspaceIdentityResolver().ResolveAsync(fixture.RootPath, default), [], DateTimeOffset.UtcNow);
        roots.Register(ids[0], fixture.RootPath, policy);
        var crypto = new WorkspaceJournalProtector(fixture.KeyStore);
        var results = new SqliteSubagentResultStore(fixture.Options, fixture.PayloadStore);
        using var publisher = new SubagentCodingResultPublisher(fixture.EventStore, agents,
            new SqliteSubagentScopeStore(fixture.Options, crypto), new SqliteSubagentChangeSetStore(fixture.Options, crypto),
            results, new SqliteSubagentApplyJournal(fixture.Options, crypto), roots, execution.Factory,
            new SubagentWorkspaceStorage(Path.Combine(fixture.RootPath, "private")));
        using var waits = new SubagentWaitCoordinator(fixture.EventStore, results, new SystemTimeProvider(), coding: publisher);
        await waits.TickAsync(default);
        Assert.Empty(await results.ListAsync(root.Run.RunId, "agent_one", default));
        Assert.Empty(await results.ListAsync(root.Run.RunId, "agent_two", default));
        await waits.TickAsync(default);
        Assert.Empty(await results.ListAsync(root.Run.RunId, "agent_one", default));
        Assert.Equal("subagent_scope_missing", Assert.Single(await results.ListAsync(root.Run.RunId, "agent_two", default)).Code);
    }
}
