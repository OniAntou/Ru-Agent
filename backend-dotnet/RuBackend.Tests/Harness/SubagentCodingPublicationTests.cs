extern alias Broker;

using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Harness.Persistence.Workspace;
using RuAgent.Protocol;
using RuAgent.Protocol.Subagents;
using RuBackend.Harness;
using RuBackend.Harness.Subagents;
using RuBackend.Harness.Workspace;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed partial class SubagentCodingPublicationTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("cleanup")]
    [InlineData("cleanup_missing_relation")]
    [InlineData("cleanup_missing_family")]
    [InlineData("cleanup_readonly")]
    [InlineData("cleanup_pipeline")]
    [InlineData("cleanup_pipeline_readonly")]
    [InlineData("cleanup_missing_scope")]
    [InlineData("cleanup_before_child")]
    [InlineData("missing_scope")]
    [InlineData("changed_identity")]
    [InlineData("resumed")]
    [InlineData("registered")]
    [InlineData("blocked_apply")]
    [InlineData("apply")]
    [InlineData("apply_conflict")]
    [InlineData("apply_foreign")]
    [InlineData("apply_terminal")]
    [InlineData("apply_ask")]
    [InlineData("apply_receipt_failure")]
    [InlineData("apply_finish_failure")]
    [InlineData("apply_native_success")]
    [InlineData("apply_native_conflict")]
    [InlineData("apply_native_receipt_failure")]
    [InlineData("apply_native_finish_failure")]
    public async Task Coding_result_is_collected_once_from_owned_worktree_and_permanent_failures_publish_once(string scenario)
    {
        await using var storage = await BackendHarnessTestFixture.CreateAsync();
        var repo = Path.Combine(storage.RootPath, "repo");
        Directory.CreateDirectory(repo);
        using var execution = new InProcessExecutionFixture(repo);
        try
        {
            await Git(repo, "init");
            await File.WriteAllTextAsync(Path.Combine(repo, "a.txt"), "clean\r\n");
            await Git(repo, "add", ".");
            await Git(repo, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-m", "seed");
            await File.WriteAllTextAsync(Path.Combine(repo, "a.txt"), "dirty parent\r\n");
            var root = await storage.SeedRunWithEncryptedWorkspaceAsync(new(HarnessRunState.Running),
                "run_" + new string('a', 32), new string('a', 32), repo);
            var resolver = new WindowsWorkspaceIdentityResolver();
            var policy = PolicySnapshotCanonicalizer.Create(1, root.Run.Preset, await resolver.ResolveAsync(repo, default), [], BackendHarnessTestFixture.Timestamp);
            var roots = new AgentRuntimeRootRegistry();
            roots.Register(root.Run.RunId, repo, policy);
            await execution.Factory.CreateAsync(repo, policy, root.Run.RunId, root.Run.SessionId.Value, default);
            var agents = new SqliteSubagentStore(storage.Options, storage.PayloadStore);
            var crypto = new WorkspaceJournalProtector(storage.KeyStore);
            var scopes = new SqliteSubagentScopeStore(storage.Options, crypto);
            var paths = new SubagentWorkspaceStorage(Path.Combine(storage.RootPath, "workspaces"));
            var workspaces = new SubagentWorkspaceFactory(execution.Factory,
                new WorkspaceTrustService(resolver, new SqliteWorkspaceStore(storage.Options), new WorkspaceTrustRequestStore(storage.Options), storage.KeyStore), paths.Root,
                new SqliteSubagentWorkspaceAllocationStore(storage.Options, crypto));
            var pins = new SqliteAgentRunProviderSelectionStore(storage.Options);
            var time = new SystemTimeProvider();
            using var factory = new SubagentRunFactory(storage.EventStore, agents, pins, storage.PayloadStore,
                scopes, roots, new Selection(), workspaces, time);
            if (scenario == "cleanup_before_child")
            {
                await VerifyUncommittedWorkspaceCleanupAsync(storage, execution, root.Run, repo, policy, agents, scopes, paths, workspaces);
                return;
            }
            var agent = await factory.CreateAsync(root.Run.RunId, "spawn_one", new("coding", "Edit a.txt", null), default);
            var allocation = Assert.Single(await new SqliteSubagentWorkspaceAllocationStore(storage.Options, crypto)
                .ListAsync(root.Run.RunId, default));
            Assert.Equal(SubagentWorkspaceAllocationStage.Prepared, allocation.Stage);
            Assert.Equal(agent.AgentId, allocation.AgentId);
            var attempt = (await agents.GetLatestAttemptAsync(root.Run.RunId, agent.AgentId, default))!;
            var scope = (await scopes.GetAsync(attempt.Run.RunId, default))!;
            Assert.True(await agents.TryAdmitAsync(agent.AgentId, time.UtcNow, default));
            var active = (await storage.EventStore.GetRunAsync(attempt.Run.RunId, default))!;
            var completed = await CompleteAsync(storage, active);
            await File.WriteAllTextAsync(Path.Combine(scope.WorkspaceRoot, "a.txt"), "child bytes\r\n");
            if (!scenario.StartsWith("apply", StringComparison.Ordinal))
                await execution.Factory.CloseSessionAsync(root.Run.SessionId.Value, default);
            var changes = new SqliteSubagentChangeSetStore(storage.Options, crypto);
            var results = new SqliteSubagentResultStore(storage.Options, storage.PayloadStore);
            var journal = new SqliteSubagentApplyJournal(storage.Options, crypto);
            using var publisher = new SubagentCodingResultPublisher(storage.EventStore, agents, scopes, changes, results, journal,
                roots, execution.Factory, paths);
            var candidate = new SubagentPublicationCandidate(agent.AgentId, root.Run.RunId, completed.RunId, SubagentRole.Coding, HarnessRunState.Completed);
            if (scenario == "missing_scope")
            {
                await using var connection = await storage.Options.OpenConnectionAsync(default);
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM agent_subagent_scopes WHERE run_id=$run";
                command.Parameters.AddWithValue("$run", completed.RunId.Value);
                await command.ExecuteNonQueryAsync();
            }
            if (scenario == "changed_identity")
            {
                Directory.Move(scope.WorkspaceRoot, scope.WorkspaceRoot + ".old");
                Directory.CreateDirectory(scope.WorkspaceRoot);
            }
            if (scenario == "resumed")
            {
                var frozen = await execution.Factory.CollectSubagentChangesAsync(new(root.Run.RunId.Value, completed.RunId.Value,
                    completed.SessionId.Value, agent.AgentId, repo, scope.WorkspaceRoot, paths.Root, scope.WorktreeManifestJson!),
                    1, new Dictionary<string, string>(), default);
                await changes.SaveAsync(completed.RunId, frozen, default);
                await File.WriteAllTextAsync(Path.Combine(scope.WorkspaceRoot, "a.txt"), "later external bytes");
            }
            if (scenario == "registered")
            {
                roots.Register(completed.RunId, scope.WorkspaceRoot, policy);
                Assert.False(await publisher.PublishAsync(candidate, default));
                Assert.Empty(await results.ListAsync(root.Run.RunId, agent.AgentId, default));
                roots.Remove(completed.RunId);
            }
            using var waits = new SubagentWaitCoordinator(storage.EventStore, results, time, coding: publisher);
            await waits.TickAsync(default);
            var result = Assert.Single(await results.ListAsync(root.Run.RunId, agent.AgentId, default));
            Assert.True(await publisher.PublishAsync(candidate, default));
            await waits.TickAsync(default);
            if (scenario is "missing_scope" or "changed_identity")
            {
                Assert.Equal(HarnessRunState.Failed, result.State);
                Assert.Equal(scenario == "missing_scope" ? "subagent_scope_missing" : "subagent_workspace_identity_changed", result.Code);
                Assert.Null(await changes.GetAsync(root.Run.RunId, agent.AgentId, completed.RunId, default));
            }
            else
            {
                Assert.Equal(HarnessRunState.Completed, result.State);
                var frozen = (await changes.GetAsync(root.Run.RunId, agent.AgentId, completed.RunId, default))!;
                var file = Assert.Single(frozen.Files);
                Assert.Equal("a.txt", file.RelativePath);
                var expectedBase = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("dirty parent\r\n"))).ToLowerInvariant();
                Assert.Equal(expectedBase, file.BaseHash);
                Assert.Equal("child bytes\r\n", Encoding.UTF8.GetString(file.Content.Span));
            }
            if (scenario == "blocked_apply")
            {
                var first = (await changes.GetAsync(root.Run.RunId, agent.AgentId, completed.RunId, default))!;
                await journal.BeginAsync(first, new string('a', 64), default);
                await agents.AppendMessageAsync(root.Run.RunId, agent.AgentId, "followup_one", "Another edit", default);
                using var continuations = new SubagentContinuationFactory(storage.EventStore, agents, pins, storage.PayloadStore,
                    scopes, time, storage.Options);
                var next = (await continuations.ContinueAsync(root.Run.RunId, agent.AgentId, default))!;
                Assert.Equal(2, next.AttemptNumber);
                Assert.True(await agents.TryAdmitAsync(agent.AgentId, time.UtcNow, default));
                await File.WriteAllTextAsync(Path.Combine(scope.WorkspaceRoot, "a.txt"), "follow-up bytes");
                var nextCompleted = await CompleteAsync(storage, (await storage.EventStore.GetRunAsync(next.Run.RunId, default))!);
                await waits.TickAsync(default);
                Assert.Single(await results.ListAsync(root.Run.RunId, agent.AgentId, default));
                Assert.Equal(nextCompleted.RunId, Assert.Single(await results.ListUnpublishedAsync(null, default)).RunId);
                Assert.Null(await changes.GetAsync(root.Run.RunId, agent.AgentId, nextCompleted.RunId, default));
                // Resolve this test's unwritten intent through the journal seam;
                // no parent file mutation or automatic reconciliation is performed.
                await journal.FinishAsync(first, new(SubagentApplyStatus.Conflict, 0, "subagent_integration_conflict"), default);
                await waits.TickAsync(default);
                Assert.Equal(2, (await results.ListAsync(root.Run.RunId, agent.AgentId, default)).Count);
                Assert.Equal("follow-up bytes", Encoding.UTF8.GetString(Assert.Single((await changes.GetAsync(root.Run.RunId,
                    agent.AgentId, nextCompleted.RunId, default))!.Files).Content.Span));
            }
            Assert.Empty(await results.ListUnpublishedAsync(null, default));
            if (scenario == "cleanup_missing_scope")
            {
                await using var missing = await storage.Options.OpenConnectionAsync(default);
                using var erase = missing.CreateCommand();
                erase.CommandText = "DELETE FROM agent_subagent_scopes WHERE run_id=$child";
                erase.Parameters.AddWithValue("$child", attempt.Run.RunId.Value);
                await erase.ExecuteNonQueryAsync();
            }
            if (scenario.StartsWith("cleanup", StringComparison.Ordinal))
                await VerifyCleanupAsync(scenario, storage, execution, root.Run, attempt.Run.RunId, scope, agents, scopes, paths);
            if (scenario.StartsWith("apply", StringComparison.Ordinal))
                await VerifyApplyAsync(scenario, storage, execution, root.Run, agent, repo, policy);
            else Assert.Equal("dirty parent\r\n", await File.ReadAllTextAsync(Path.Combine(repo, "a.txt")));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(storage.RootPath, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    private static async Task<AgentRun> CompleteAsync(BackendHarnessTestFixture storage, AgentRun active)
    {
        var now = DateTimeOffset.UtcNow;
        var completed = new AgentRun(active.RunId, active.SessionId, active.WorkspaceId, new(HarnessRunState.Completed),
            active.Version + 1, active.LastSequence + 1, active.BudgetWindow, active.ModelStepsInWindow, active.ToolCallsInWindow,
            active.Preset, active.PolicySnapshotVersion, active.PolicySnapshotSha256, active.CreatedAt, now, terminalAt: now);
        var text = Encoding.UTF8.GetBytes("Finished coding");
        var payload = new PayloadId("payload_" + Guid.NewGuid().ToString("N"));
        await storage.EventStore.CommitAsync(new(active.RunId, active.Version, completed,
            [new(new("evt_" + Guid.NewGuid().ToString("N")), HarnessEventKind.ModelStepCompleted,
                modelStepId: new("step_" + Guid.NewGuid().ToString("N")), payloadId: payload)],
            [new(payload, HarnessPayloadKind.AssistantText, HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                HarnessPayloadEncoding.Utf8Text, text.Length, text)], []), default);
        return completed;
    }

    private static async Task<string> Git(string root, params string[] arguments)
    {
        var result = await new Broker::RuAgent.Broker.Tools.Git.StreamingGitCli().RunAsync(
            new[] { "-c", "core.hooksPath=NUL", "-c", "core.fsmonitor=false" }.Concat(arguments).ToArray(), root, default);
        Assert.True(result.Succeeded, result.FailureReason + ": " + result.Stderr);
        return result.Stdout;
    }

    private sealed class Selection : ISubagentModelSelectionProvider
    {
        public Task<AgentRunProviderSelection> ResolveAsync(RunId root, SubagentRole role, CancellationToken ct, string? model = null, string? providerId = null) =>
            Task.FromResult(new AgentRunProviderSelection("Custom OpenAI", "custom", "chat_completions",
                "https://example.invalid/v1/", "fixture-model", null, "{}", null));
    }
}
