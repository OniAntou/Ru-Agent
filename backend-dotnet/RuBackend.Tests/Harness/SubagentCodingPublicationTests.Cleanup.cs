using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuBackend.Harness;
using RuBackend.Harness.Subagents;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed partial class SubagentCodingPublicationTests
{
    private static async Task VerifyCleanupAsync(string scenario, BackendHarnessTestFixture storage,
        InProcessExecutionFixture execution, AgentRun root, RunId child, SubagentRuntimeScope scope,
        SqliteSubagentStore agents, SqliteSubagentScopeStore scopes, SubagentWorkspaceStorage paths)
    {
        var artifacts = new HarnessSessionArtifactStore(storage.Options);
        var cleanup = new SubagentArtifactCleanupCoordinator(storage.Options, storage.EventStore, agents, scopes, execution.Factory, paths,
            new SqliteSubagentWorkspaceAllocationStore(storage.Options, new WorkspaceJournalProtector(storage.KeyStore)));
        var denied = await Assert.ThrowsAsync<HarnessStoreException>(() => cleanup.ReclaimSessionAsync(root.SessionId.Value, default));
        Assert.Equal("subagent_cleanup_not_authorized", denied.Code);
        Assert.True(Directory.Exists(scope.WorkspaceRoot));
        if (scenario.StartsWith("cleanup_pipeline", StringComparison.Ordinal))
        {
            await VerifyDeletionPipelineAsync(scenario, storage, execution, root, child, scope, agents, artifacts, cleanup);
            return;
        }
        await artifacts.MarkSessionDeletedAsync(root.SessionId.Value, DateTimeOffset.UtcNow, default);
        await agents.CancelFamilyAsync(root.RunId, default);
        if (scenario is "cleanup_missing_relation" or "cleanup_missing_family")
        {
            await using var connection = await storage.Options.OpenConnectionAsync(default);
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM agent_subagents WHERE root_run_id=$root";
            command.Parameters.AddWithValue("$root", root.RunId.Value);
            await command.ExecuteNonQueryAsync();
            if (scenario == "cleanup_missing_family")
            {
                command.CommandText = "DELETE FROM agent_subagent_families WHERE root_run_id=$root";
                await command.ExecuteNonQueryAsync();
                Assert.Contains(root.RunId, await agents.ListFamiliesForSessionAsync(root.SessionId, null, default));
                await agents.CancelFamilyAsync(root.RunId, default);
            }
        }
        if (scenario == "cleanup_readonly")
        {
            var file = Path.Combine(Path.GetDirectoryName(scope.WorkspaceRoot)!, "readonly.txt");
            File.WriteAllText(file, "retained until retry");
            File.SetAttributes(file, FileAttributes.ReadOnly);
            try
            {
                var failed = await Assert.ThrowsAsync<HarnessStoreException>(() => cleanup.ReclaimSessionAsync(root.SessionId.Value, default));
                Assert.Equal("subagent_cleanup_incomplete", failed.Code);
                Assert.NotNull(await storage.EventStore.GetRunAsync(root.RunId, default));
                Assert.NotNull(await storage.EventStore.GetRunAsync(child, default));
            }
            finally { File.SetAttributes(file, FileAttributes.Normal); }
        }
        await cleanup.ReclaimSessionAsync(root.SessionId.Value, default);
        Assert.False(Directory.Exists(Path.GetDirectoryName(scope.WorkspaceRoot)));
        await cleanup.ReclaimSessionAsync(root.SessionId.Value, default);
        await artifacts.CloseAndDeleteAsync(root.SessionId.Value, DateTimeOffset.UtcNow, default);
        Assert.Null(await storage.EventStore.GetRunAsync(child, default));
        Assert.Null(await storage.EventStore.GetRunAsync(root.RunId, default));
    }

    private static async Task VerifyDeletionPipelineAsync(string scenario, BackendHarnessTestFixture storage,
        InProcessExecutionFixture execution, AgentRun root, RunId child, SubagentRuntimeScope scope,
        SqliteSubagentStore agents, HarnessSessionArtifactStore artifacts, SubagentArtifactCleanupCoordinator cleanup)
    {
        var trace = new List<string>();
        var cancellation = new SubagentCancellationCoordinator(storage.EventStore, agents,
            new CompletedAttemptCleanup(), new SystemTimeProvider());
        var coordinator = new HarnessSessionDeletionCoordinator(artifacts,
            new HarnessSessionDeletionCoordinatorTests.RecordingSessionStore(trace, true), TimeProvider.System,
            brokerGateways: execution.Factory, subagentCancellation: cancellation, subagentArtifacts: cleanup);
        if (scenario == "cleanup_pipeline_readonly")
        {
            var file = Path.Combine(Path.GetDirectoryName(scope.WorkspaceRoot)!, "readonly.txt");
            File.WriteAllText(file, "retain proof after primary session deletion");
            File.SetAttributes(file, FileAttributes.ReadOnly);
            try
            {
                Assert.Equal(HarnessSessionDeletionOutcome.ArtifactsPending, await coordinator.DeleteAsync(root.SessionId.Value, default));
                Assert.Equal(["session"], trace);
                Assert.NotNull(await storage.EventStore.GetRunAsync(root.RunId, default));
                Assert.NotNull(await storage.EventStore.GetRunAsync(child, default));
                var retry = new HarnessSessionDeletionCoordinator(artifacts,
                    new HarnessSessionDeletionCoordinatorTests.RecordingSessionStore(trace, false) { PrimaryMissing = true }, TimeProvider.System,
                    brokerGateways: execution.Factory, subagentCancellation: cancellation, subagentArtifacts: cleanup);
                Assert.Equal(HarnessSessionDeletionOutcome.ArtifactsPending, await retry.DeleteAsync(root.SessionId.Value, default));
            }
            finally { File.SetAttributes(file, FileAttributes.Normal); }
        }
        if (scenario == "cleanup_pipeline_readonly")
            // The primary conversation was already deleted by the failed pass.
            coordinator = new HarnessSessionDeletionCoordinator(artifacts,
                new HarnessSessionDeletionCoordinatorTests.RecordingSessionStore(trace, false) { PrimaryMissing = true }, TimeProvider.System,
                brokerGateways: execution.Factory, subagentCancellation: cancellation, subagentArtifacts: cleanup);
        Assert.Equal(HarnessSessionDeletionOutcome.Deleted, await coordinator.DeleteAsync(root.SessionId.Value, default));
        Assert.False(Directory.Exists(Path.GetDirectoryName(scope.WorkspaceRoot)));
        Assert.Null(await storage.EventStore.GetRunAsync(child, default));
        Assert.Null(await storage.EventStore.GetRunAsync(root.RunId, default));
        Assert.NotEmpty(trace);
    }

    private sealed class CompletedAttemptCleanup : IAgentRuntimeRunCleanup
    {
        public Task CancelAndRemoveRunAsync(RunId runId, CancellationToken ct) => Task.CompletedTask;
    }
}
