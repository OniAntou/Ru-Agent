using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using RuBackend.Business.Sessions;
using RuBackend.Harness;
using RuBackend.Harness.Broker;
using RuBackend.Harness.Subagents;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed partial class SubagentNativeControlTests
{
    private static async Task VerifyFamilyScenarioAsync(BackendHarnessTestFixture storage, InProcessExecutionFixture execution,
        SqliteSubagentStore agents, SqliteSubagentScopeStore scopes, SubagentCodingResultPublisher publisher,
        AgentRun root, string repo, PolicySnapshot policy, AgentRuntimeRootRegistry roots, SessionStore sessions)
    {
        await storage.PayloadStore.CaptureBaseContextAsync(root.RunId, [], DateTimeOffset.UtcNow, default);
        var model = new FamilyModel();
        var factoryDeps = SubagentCoordinatorFactoryTestDependencies.Create(storage, execution.Factory, agents);
        var factory = new AgentRuntimeCoordinatorFactory(model, storage.EventStore, storage.Options, storage.PayloadStore,
            inspectionGate: new RuAgent.Harness.Core.Reconciliation.ReconciliationInspectionGate(),
            workspaceCoordinatorStore: new RuAgent.Harness.Persistence.WorkspaceCoordinatorStore(storage.Options),
            settings: new NativeSettings(), time: new SystemTimeProvider(), contextLimit: new StaticModelContextLimitProvider(100000),
            brokerGateways: execution.Factory, subagentBinder: factoryDeps.Binder,
            familyBudget: factoryDeps.FamilyBudget, subagentInbox: factoryDeps.Inbox,
            transientRedactor: RuAgent.Harness.Persistence.Privacy.KnownSecretRedactor.Empty);
        var coordinator = await factory.CreateAsync(repo, policy, root.RunId, root.SessionId.Value);
        var spawned = await coordinator.RunOneStepAsync(root.RunId, default);
        Assert.Equal(2, spawned.Receipts.Count);
        Assert.All(spawned.Receipts, r => Assert.Equal(ToolExecutionOutcome.Succeeded, r.Outcome));
        var owned = await agents.ListAsync(root.RunId, default);
        Assert.Equal(2, owned.Count);
        var coding = owned.Single(a => a.Role == SubagentRole.Coding);
        var review = owned.Single(a => a.Role == SubagentRole.Review);
        model.Agents = owned.Select(a => a.AgentId).ToArray();
        model.Coding = coding.AgentId;
        await agents.AppendMessageAsync(root.RunId, review.AgentId, "user_family_followup", "Check the empty input boundary", default);
        foreach (var child in owned) Assert.True(await agents.TryAdmitAsync(child.AgentId, DateTimeOffset.UtcNow, default));
        var reviewAttempt = (await agents.GetLatestAttemptAsync(root.RunId, review.AgentId, default))!;
        Assert.Equal(1, await agents.DeliverMessagesAsync(reviewAttempt.Run.RunId, DateTimeOffset.UtcNow, default));
        Assert.Equal(0, await agents.DeliverMessagesAsync(reviewAttempt.Run.RunId, DateTimeOffset.UtcNow, default));
        var reviewScope = (await scopes.GetAsync(reviewAttempt.Run.RunId, default))!;
        var reviewPolicy = PolicySnapshotMapper.FromWire(RuAgent.Protocol.Harness.BrokerPolicyContracts.ReadSnapshot(
            System.Text.Json.Nodes.JsonNode.Parse(reviewScope.PolicySnapshotJson)!.AsObject()));
        var gateway = await execution.Factory.CreateAsync(repo, reviewPolicy, reviewAttempt.Run.RunId, reviewAttempt.Run.SessionId.Value, default);
        Assert.True(await new SubagentRuntimeBinder(storage.EventStore, agents, scopes, execution.Factory)
            .BindAsync(reviewAttempt.Run.RunId, repo, reviewPolicy, default));
        var read = new ToolProposal(reviewAttempt.Run.RunId, new("step_" + new string('7', 32)), new("call_" + new string('7', 32)),
            "fixture_review_read", 1, "read", "1.0.0", JsonSerializer.SerializeToElement(new { path = "a.txt" }));
        var allowed = await gateway.EvaluateAsync(read, default);
        Assert.Equal(PolicyDecision.Allow, allowed.Decision);
        var prepared = await gateway.PrepareAsync(new(read, new("exec_" + new string('7', 32)), allowed.CanonicalIntentToken!, allowed.GatewayGeneration), default);
        Assert.Equal(ToolExecutionOutcome.Succeeded, (await gateway.ExecuteAsync(prepared, _ => ValueTask.CompletedTask, default)).Outcome);
        var write = new ToolProposal(read.RunId, read.ModelStepId, new("call_" + new string('8', 32)), "fixture_review_denied", 1,
            "write_file", "1.0.0", JsonSerializer.SerializeToElement(new { path = "a.txt", content = "must not write", overwrite = true }));
        Assert.Equal(PolicyDecision.Deny, (await gateway.EvaluateAsync(write, default)).Decision);
        await CompleteReviewFixtureAsync(storage, reviewAttempt.Run.RunId);
        await execution.Factory.CloseSessionAsync(reviewAttempt.Run.SessionId.Value, default);
        var results = new SqliteSubagentResultStore(storage.Options, storage.PayloadStore);
        await results.PublishTerminalAsync(root.RunId, review.AgentId, reviewAttempt.Run.RunId, default);
        await CompleteAndApplyCodingAsync(storage, execution, scopes, coding,
            (await agents.GetLatestAttemptAsync(root.RunId, coding.AgentId, default))!, publisher,
            (_, _) => throw new InvalidOperationException("fixture_collect_only"), "collect_only", policy, false);
        Assert.Equal("parent", await File.ReadAllTextAsync(Path.Combine(repo, "a.txt")));

        var waited = await coordinator.RunOneStepAsync(root.RunId, default);
        Assert.Equal(HarnessRunState.WaitingSubagents, waited.Run.State);
        using var waits = new SubagentWaitCoordinator(storage.EventStore, results, new SystemTimeProvider());
        await waits.TickAsync(default);
        var resumed = (await storage.EventStore.GetRunAsync(root.RunId, default))!;
        Assert.Equal(HarnessRunState.Running, resumed.State);
        Assert.Equal(2, model.Calls);
        foreach (var agent in owned) Assert.True((await results.ListAsync(root.RunId, agent.AgentId, default))[0].Delivered);
        await waits.TickAsync(default);
        Assert.Equal(resumed, await storage.EventStore.GetRunAsync(root.RunId, default));
        Assert.Equal(ToolExecutionOutcome.Succeeded, Assert.Single((await coordinator.RunOneStepAsync(root.RunId, default)).Receipts).Outcome);
        Assert.Equal("child", await File.ReadAllTextAsync(Path.Combine(repo, "a.txt")));
        Assert.Equal(HarnessRunState.WaitingHistory, (await coordinator.RunOneStepAsync(root.RunId, default)).Run.State);
        var dispatcher = new AgentConversationOutboxDispatcher(new(storage.Options), storage.PayloadStore, sessions,
            new HarnessModelContextBootstrapper(sessions, storage.PayloadStore, new CryptographicIdGenerator(), TimeProvider.System),
            new AgentRunHistoryTransitionSink(factory, roots), TimeProvider.System);
        for (var i = 0; i < 4 && (await storage.EventStore.GetRunAsync(root.RunId, default))!.State != HarnessRunState.Completed; i++)
            await dispatcher.DispatchOnceAsync(default);
        Assert.Equal(HarnessRunState.Completed, (await storage.EventStore.GetRunAsync(root.RunId, default))!.State);
        Assert.Equal(4, model.Calls);
        var snapshot = await new SqliteSubagentSnapshotReader(storage.Options, storage.PayloadStore).ReadAsync(root.RunId, default);
        Assert.All(snapshot.Agents, a => Assert.NotNull(a.Task));
        Assert.All(snapshot.Agents, a => Assert.Equal("fixture-model", Assert.Single(a.Attempts).Model!.Model));
        Assert.Equal("Applied", snapshot.Agents.Single(a => a.Agent.Role == SubagentRole.Coding).Results[0].Apply!.Status.ToString());
        Assert.Single((await sessions.GetAsync(root.SessionId.Value, default))!.Messages);
    }

    private static async Task CompleteReviewFixtureAsync(BackendHarnessTestFixture storage, RunId id)
    {
        var active = (await storage.EventStore.GetRunAsync(id, default))!;
        var now = DateTimeOffset.UtcNow;
        var next = new AgentRun(id, active.SessionId, active.WorkspaceId, new(HarnessRunState.Completed), active.Version + 1,
            active.LastSequence + 1, active.BudgetWindow, active.ModelStepsInWindow, active.ToolCallsInWindow, active.Preset,
            active.PolicySnapshotVersion, active.PolicySnapshotSha256, active.CreatedAt, now, terminalAt: now);
        var payload = new PayloadId("payload_" + Guid.NewGuid().ToString("N"));
        var bytes = System.Text.Encoding.UTF8.GetBytes("Review complete: empty input checked.");
        await storage.EventStore.CommitAsync(new(id, active.Version, next,
            [new(new("evt_" + Guid.NewGuid().ToString("N")), HarnessEventKind.ModelStepCompleted, payloadId: payload)],
            [new(payload, HarnessPayloadKind.AssistantText, HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                HarnessPayloadEncoding.Utf8Text, bytes.Length, bytes)], []), default);
    }

    private sealed class FamilyModel : IModelStepRunner
    {
        public int Calls { get; private set; }
        public string[] Agents { get; set; } = [];
        public string Coding { get; set; } = "";
        public Task<ModelStepResult> RunStepAsync(ModelStepRequest request, Func<AssistantDelta, ValueTask> delta, CancellationToken ct)
        {
            Calls++;
            ModelToolCall Call(string id, string tool, object args) => new(id, tool, "1.0.0", JsonSerializer.SerializeToElement(args));
            var tools = Calls switch
            {
                1 => new[] { Call("family_coding", "agent_spawn", new { role = "coding", task = "Implement parser" }),
                    Call("family_review", "agent_spawn", new { role = "review", task = "Review empty input" }) },
                2 => [Call("family_wait", "agent_wait", new { agent_ids = Agents, timeout_ms = 60000 })],
                3 => [Call("family_apply", "agent_apply", new { agent_id = Coding, result_version = 1 })],
                _ => [],
            };
            return Task.FromResult(new ModelStepResult(new AssistantStep(Calls == 4 ? "Coding and review completed." : "Delegating tasks",
                tools, tools.Length == 0 ? AssistantFinishReason.Stop : AssistantFinishReason.ToolCalls, 32),
                SystemPromptComposer.Compose(request.PromptContext, nativeToolCalling: true).Receipt));
        }
    }
}
