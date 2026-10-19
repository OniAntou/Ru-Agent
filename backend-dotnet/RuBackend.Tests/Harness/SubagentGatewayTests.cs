using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuBackend.Harness.Broker;
using RuBackend.Harness.Subagents;
using RuBackend.Harness;
using RuBackend.Business.Settings;
using RuBackend.Harness.Policy;
using RuAgent.Harness.Core.Context;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed partial class SubagentGatewayTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Runtime_binding_requires_durable_scope_and_enforces_review_role_after_recreation(bool validDirectoryIdentity)
    {
        await using var storage = await BackendHarnessTestFixture.CreateAsync();
        using var execution = new InProcessExecutionFixture();
        await storage.SeedRunWithEncryptedWorkspaceAsync(new(HarnessRunState.Created),
            "run_" + new string('1', 32), new string('1', 32), execution.WorkspaceRoot);
        var identity = await new RuBackend.Harness.Workspace.WindowsWorkspaceIdentityResolver().ResolveAsync(execution.WorkspaceRoot, default);
        if (!validDirectoryIdentity) identity = new(identity.WorkspaceId, "fake-volume", "fake-file", new string('a', 64));
        var policy = PolicySnapshotCanonicalizer.Create(1, AgentPermissionPreset.Standard, identity, [], BackendHarnessTestFixture.Timestamp);
        NewAgentRun Request(char id, HarnessRunState state)
        {
            var run = new AgentRun(new("run_" + new string(id, 32)), new(Guid.NewGuid().ToString("N")), identity.WorkspaceId,
                new(state), 0, 1, 1, 0, 0, policy.Preset, policy.Version, policy.Sha256,
                BackendHarnessTestFixture.Timestamp, BackendHarnessTestFixture.Timestamp);
            return new(run, [new(new("evt_" + new string(id, 32)), HarnessEventKind.RunStateChanged)], [], [],
                new(new string('c', 64), new string('d', 64), 1));
        }
        var root = Request('a', HarnessRunState.Running);
        var child = Request('b', HarnessRunState.Created);
        await storage.EventStore.TryCreateRunAsync(root, default);
        var agents = new SqliteSubagentStore(storage.Options, storage.PayloadStore);
        await agents.CreateAsync(new("agent_review", root.Run.RunId, "spawn_review", SubagentRole.Review, child), default);
        var scopes = new SqliteSubagentScopeStore(storage.Options, new WorkspaceJournalProtector(storage.KeyStore));
        var binder = new SubagentRuntimeBinder(storage.EventStore, agents, scopes, execution.Factory);
        SessionApprovalLedger.Remember(root.Run.SessionId.Value, new PolicyMatcher(ToolId: "list"), DateTimeOffset.UtcNow);
        var effectiveParent = SessionApprovalLedger.Apply(policy, root.Run.SessionId.Value, DateTimeOffset.UtcNow)!;
        await execution.Factory.CreateAsync(execution.WorkspaceRoot, effectiveParent, root.Run.RunId, root.Run.SessionId.Value, default);
        var gateway = await execution.Factory.CreateAsync(execution.WorkspaceRoot, policy, child.Run.RunId, child.Run.SessionId.Value, default);
        await Assert.ThrowsAsync<HarnessStoreException>(() => binder.BindAsync(child.Run.RunId, execution.WorkspaceRoot, policy, default));
        await scopes.SaveAsync(root.Run.RunId, child.Run.RunId, new(execution.WorkspaceRoot, execution.WorkspaceRoot,
            new(policy.Version, policy.Sha256), RuAgent.Protocol.Harness.BrokerPolicyContracts.WriteSnapshot(
                PolicySnapshotMapper.ToWire(policy)).ToJsonString(), null,
            RuAgent.Protocol.Harness.BrokerPolicyContracts.WriteSnapshot(PolicySnapshotMapper.ToWire(policy)).ToJsonString()), default);
        if (!validDirectoryIdentity)
        {
            var error = await Assert.ThrowsAsync<HarnessStoreException>(() => binder.BindAsync(child.Run.RunId, execution.WorkspaceRoot, policy, default));
            Assert.Equal("subagent_workspace_identity_changed", error.Code);
            SessionApprovalLedger.ClearSession(root.Run.SessionId.Value);
            return;
        }
        Assert.True(await binder.BindAsync(child.Run.RunId, execution.WorkspaceRoot, policy, default));
        SessionApprovalLedger.ClearSession(root.Run.SessionId.Value);
        using var arguments = JsonDocument.Parse("{\"op\":\"write\",\"path\":\"x.txt\",\"content\":\"x\"}");
        var decision = await gateway.EvaluateAsync(new(child.Run.RunId, new("step_" + new string('c', 32)),
            new("call_" + new string('d', 32)), "provider_call", 1, "write", "1.0.0", arguments.RootElement), default);
        Assert.Equal(PolicyDecision.Deny, decision.Decision);
        Assert.Equal("subagent_role_denied", decision.SafeCode);
        await new SqliteAgentRunProviderSelectionStore(storage.Options).PinAsync(child.Run.RunId,
            new("Custom OpenAI", "custom_openai", "chat_completions", "https://example.invalid/v1/", "fixture-model", null, "{}", null), default);
        await storage.PayloadStore.CaptureBaseContextAsync(child.Run.RunId, [], DateTimeOffset.UtcNow, default);
        Assert.True(await agents.TryAdmitAsync(agentId: "agent_review", now: BackendHarnessTestFixture.Timestamp.AddSeconds(1), ct: default));
        var model = new CapabilityCapture(ScriptedModelStepRunner.FromJsonForTests("""[{"text":"review result"}]"""));
        var factory = new AgentRuntimeCoordinatorFactory(model,
            storage.EventStore, storage.Options, storage.PayloadStore, settings: new StubSettings(), time: new SystemTimeProvider(),
            contextLimit: new StaticModelContextLimitProvider(100000), brokerGateways: execution.Factory, subagentBinder: binder);
        SessionApprovalLedger.Remember(child.Run.SessionId.Value, new PolicyMatcher(ToolId: "list"), DateTimeOffset.UtcNow);
        var effectiveChild = SessionApprovalLedger.Apply(policy, child.Run.SessionId.Value, DateTimeOffset.UtcNow)!;
        var coordinator = await factory.CreateAsync(execution.WorkspaceRoot, effectiveChild, child.Run.RunId, child.Run.SessionId.Value);
        SessionApprovalLedger.ClearSession(child.Run.SessionId.Value);
        var completed = await coordinator.RunOneStepAsync(child.Run.RunId, default);
        Assert.Equal(HarnessRunState.Completed, completed.Run.State);
        Assert.Contains("read", model.Tools);
        Assert.DoesNotContain("ask_user", model.Tools);
        Assert.DoesNotContain("write", model.Tools);
        Assert.DoesNotContain("edit", model.Tools);
        Assert.DoesNotContain("bash", model.Tools);
        Assert.DoesNotContain(model.Tools, id => id.StartsWith("agent_", StringComparison.Ordinal));
        Assert.Null(await new HarnessOutboxStore(storage.Options).ClaimNextAsync(DateTimeOffset.UtcNow, default));
    }

    private sealed class StubSettings : ISettingsStore
    {
        public Task<SettingsSnapshot> ReadAsync(CancellationToken ct) => Task.FromResult(new SettingsSnapshot(
            new System.Text.Json.Nodes.JsonObject(), "OpenAI", "fixture-model", "", "http://localhost:8000/v1", "fixture-model", ""));
        public Task SaveAsync(SettingsSnapshot candidate, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task Delegation_is_enforced_through_normal_in_process_gateway()
    {
        using var fixture = new InProcessExecutionFixture();
        var rootId = new RunId("run_" + new string('a', 32));
        var childId = new RunId("run_" + new string('b', 32));
        var identity = new WorkspaceIdentity(new WorkspaceId("ws_" + new string('c', 32)), "volume", "file", new string('d', 64));
        var policy = PolicySnapshotCanonicalizer.Create(1, AgentPermissionPreset.FullAccess, identity, [], DateTimeOffset.UtcNow);
        await fixture.Factory.CreateAsync(fixture.WorkspaceRoot, policy, rootId, new string('a', 32), default);
        var child = await fixture.Factory.CreateAsync(fixture.WorkspaceRoot, policy, childId, new string('b', 32), default);
        await fixture.Factory.BindDelegationAsync(rootId, childId, SubagentRole.Review, default);
        using var arguments = JsonDocument.Parse("{\"op\":\"write\",\"path\":\"x.txt\",\"content\":\"x\"}");
        var decision = await child.EvaluateAsync(new ToolProposal(childId, new("step_" + new string('c', 32)),
            new("call_" + new string('d', 32)), "provider_call", 1, "write", "1.0.0", arguments.RootElement), default);
        Assert.Equal(PolicyDecision.Deny, decision.Decision);
        Assert.Equal("subagent_role_denied", decision.SafeCode);
    }
}
