using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using RuBackend.Harness;
using RuBackend.Harness.Subagents;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed partial class SubagentCodingPublicationTests
{
    private static async Task VerifyUncommittedWorkspaceCleanupAsync(BackendHarnessTestFixture storage,
        InProcessExecutionFixture execution, AgentRun root, string repo, PolicySnapshot policy, SqliteSubagentStore agents,
        SqliteSubagentScopeStore scopes, SubagentWorkspaceStorage paths, ISubagentWorkspaceFactory workspaces)
    {
        var workspace = await workspaces.CreateAsync(root, repo, policy, "agent_uncommitted", SubagentRole.Coding, default);
        Assert.Empty(await agents.ListAsync(root.RunId, default));
        Assert.Empty(await agents.ListOwnedRunIdsAsync(root.RunId, default));
        Assert.Equal(root.RunId, Assert.Single(await agents.ListFamiliesForSessionAsync(root.SessionId, null, default)));
        var replay = await Assert.ThrowsAsync<HarnessStoreException>(() =>
            workspaces.CreateAsync(root, repo, policy, "agent_uncommitted", SubagentRole.Coding, default));
        Assert.Equal("subagent_preparation_incomplete", replay.Code);
        Assert.Single(Directory.GetDirectories(paths.Root));
        await execution.Factory.CloseSessionAsync(root.SessionId.Value, default);
        // Fresh authority and store instances have no cached workspace manifest or child lease.
        using var restarted = new InProcessExecutionFixture();
        var allocations = new SqliteSubagentWorkspaceAllocationStore(storage.Options, new(storage.KeyStore));
        Assert.Equal(SubagentWorkspaceAllocationStage.Prepared, Assert.Single(await allocations.ListAsync(root.RunId, default)).Stage);
        var cleanup = new SubagentArtifactCleanupCoordinator(storage.Options, storage.EventStore, agents, scopes, restarted.Factory, paths, allocations);
        var coordinator = new HarnessSessionDeletionCoordinator(new HarnessSessionArtifactStore(storage.Options),
            new HarnessSessionDeletionCoordinatorTests.RecordingSessionStore([], true), TimeProvider.System,
            brokerGateways: restarted.Factory, runCleanup: new CompletedAttemptCleanup(),
            subagentCancellation: new SubagentCancellationCoordinator(storage.EventStore, agents, new CompletedAttemptCleanup(), new SystemTimeProvider()),
            subagentArtifacts: cleanup);
        Assert.Equal(HarnessSessionDeletionOutcome.Deleted, await coordinator.DeleteAsync(root.SessionId.Value, default));
        Assert.False(Directory.Exists(Path.GetDirectoryName(workspace.Root)));
        Assert.Empty(await allocations.ListAsync(root.RunId, default));
        Assert.Null(await storage.EventStore.GetRunAsync(root.RunId, default));
        Assert.Equal("dirty parent\r\n", await File.ReadAllTextAsync(Path.Combine(repo, "a.txt")));
        Assert.DoesNotContain("agent_uncommitted", await Git(repo, "worktree", "list", "--porcelain"));
    }
}
