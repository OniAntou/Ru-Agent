extern alias Broker;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Harness;
using RuAgent.Protocol.Subagents;
using RuBackend.Harness.Broker;
using RuBackend.Harness.Subagents;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed partial class SubagentNativeControlTests
{
    private static async Task CompleteAndApplyCodingAsync(BackendHarnessTestFixture storage, InProcessExecutionFixture execution,
        SqliteSubagentScopeStore scopes, SubagentRecord agent, SubagentAttempt attempt, SubagentCodingResultPublisher publisher,
        Func<ToolProposal, ToolAccessKind, Task<ToolExecutionReceipt>> executeRoot,
        string? recoveryDecision, PolicySnapshot parentPolicy, bool failFinish)
    {
        var scope = (await scopes.GetAsync(attempt.Run.RunId, default))!;
        var policy = PolicySnapshotMapper.FromWire(BrokerPolicyContracts.ReadSnapshot(JsonNode.Parse(scope.PolicySnapshotJson)!.AsObject()));
        var gateway = await execution.Factory.CreateAsync(scope.WorkspaceRoot, policy, attempt.Run.RunId, attempt.Run.SessionId.Value, default);
        var agents = new SqliteSubagentStore(storage.Options, storage.PayloadStore);
        Assert.True(await new SubagentRuntimeBinder(storage.EventStore, agents, scopes, execution.Factory)
            .BindAsync(attempt.Run.RunId, scope.WorkspaceRoot, policy, default));
        var write = new ToolProposal(attempt.Run.RunId, new("step_" + new string('1', 32)), new("call_" + new string('2', 32)),
            "provider_child_write", 1, "write_file", "1.0.0", JsonSerializer.SerializeToElement(new { path = "a.txt", content = "child", overwrite = true }));
        var allowed = await gateway.EvaluateAsync(write, default);
        Assert.Equal(PolicyDecision.Allow, allowed.Decision);
        var prepared = await gateway.PrepareAsync(new(write, new("exec_" + new string('3', 32)), allowed.CanonicalIntentToken!, allowed.GatewayGeneration), default);
        var written = await gateway.ExecuteAsync(prepared, _ => ValueTask.CompletedTask, default);
        Assert.True(written.Outcome == ToolExecutionOutcome.Succeeded, written.Code + ": " + Encoding.UTF8.GetString(written.ModelContent.Span));
        Assert.Equal("parent", await File.ReadAllTextAsync(Path.Combine(scope.ParentRoot, "a.txt")));

        // Deterministic completion fixture; no model/provider outcome is claimed.
        var active = (await storage.EventStore.GetRunAsync(attempt.Run.RunId, default))!;
        var now = DateTimeOffset.UtcNow;
        var completed = new AgentRun(active.RunId, active.SessionId, active.WorkspaceId, new(HarnessRunState.Completed),
            active.Version + 1, active.LastSequence + 1, active.BudgetWindow, active.ModelStepsInWindow, active.ToolCallsInWindow,
            active.Preset, active.PolicySnapshotVersion, active.PolicySnapshotSha256, active.CreatedAt, now, terminalAt: now);
        var payload = new PayloadId("payload_" + Guid.NewGuid().ToString("N"));
        var text = Encoding.UTF8.GetBytes("Finished coding");
        await storage.EventStore.CommitAsync(new(active.RunId, active.Version, completed,
            [new(new("evt_" + Guid.NewGuid().ToString("N")), HarnessEventKind.ModelStepCompleted,
                modelStepId: new("step_" + Guid.NewGuid().ToString("N")), payloadId: payload)],
            [new(payload, HarnessPayloadKind.AssistantText, HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                HarnessPayloadEncoding.Utf8Text, text.Length, text)], []), default);
        await execution.Factory.CloseSessionAsync(active.SessionId.Value, default);
        Assert.True(await publisher.PublishAsync(new(agent.AgentId, agent.RootRunId, active.RunId, SubagentRole.Coding, HarnessRunState.Completed), default));
        if (recoveryDecision == "collect_only") return;
        if (recoveryDecision is "user_success" or "user_unknown" or "user_approval")
        {
            await VerifyUserApplyAsync(storage, execution, agent, scope.ParentRoot, parentPolicy, recoveryDecision);
            return;
        }
        if (recoveryDecision is not null)
        {
            await VerifyApplyRecoveryAsync(storage, execution, agent, scope.ParentRoot, parentPolicy, recoveryDecision, failFinish);
            return;
        }
        var apply = new ToolProposal(agent.RootRunId, new("step_" + new string('4', 32)), new("call_" + new string('5', 32)),
            "provider_apply", 1, "agent_apply", "1.0.0", JsonSerializer.SerializeToElement(new { agent_id = agent.AgentId, result_version = 1 }));
        var result = await executeRoot(apply, ToolAccessKind.Mutation);
        Assert.Equal(ToolExecutionOutcome.Succeeded, result.Outcome);
        Assert.Equal("child", await File.ReadAllTextAsync(Path.Combine(scope.ParentRoot, "a.txt")));
        await File.WriteAllTextAsync(Path.Combine(scope.ParentRoot, "a.txt"), "later parent edit");
        Assert.Equal(ToolExecutionOutcome.Succeeded, (await executeRoot(apply, ToolAccessKind.Mutation)).Outcome);
        Assert.Equal("later parent edit", await File.ReadAllTextAsync(Path.Combine(scope.ParentRoot, "a.txt")));
    }

    private static async Task InitializeRepoAsync(string repo)
    {
        var git = new Broker::RuAgent.Broker.Tools.Git.StreamingGitCli();
        foreach (var args in new[] { new[] { "init" }, ["add", "."], ["-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-m", "seed"] })
        {
            var result = await git.RunAsync(new[] { "-c", "core.hooksPath=NUL", "-c", "core.fsmonitor=false" }.Concat(args).ToArray(), repo, default);
            Assert.True(result.Succeeded, result.FailureReason + ": " + result.Stderr);
        }
    }

    private sealed class FixtureFileAttributes(string root) : IDisposable
    {
        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        }
    }
}
