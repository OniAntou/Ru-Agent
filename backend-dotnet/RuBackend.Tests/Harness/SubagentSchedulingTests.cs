using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Harness;
using RuAgent.Protocol.Subagents;
using RuBackend.Business;
using RuBackend.Business.Sessions;
using RuBackend.Business.Settings;
using RuBackend.Harness;
using RuBackend.Harness.Broker;
using RuBackend.Harness.Subagents;
using RuBackend.Harness.Policy;
using RuBackend.Harness.Workspace;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class SubagentSchedulingTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task Four_parked_parents_release_workers_and_restore_leases_and_cancel_children(bool closeParents, bool omitInitialParentLease, bool rememberParentPermission)
    {
        await using var storage = await BackendHarnessTestFixture.CreateAsync();
        using var execution = new InProcessExecutionFixture(storage.RootPath);
        var roots = new AgentRuntimeRootRegistry();
        var agents = new SqliteSubagentStore(storage.Options, storage.PayloadStore);
        var scopes = new SqliteSubagentScopeStore(storage.Options, new WorkspaceJournalProtector(storage.KeyStore));
        var selections = new SqliteAgentRunProviderSelectionStore(storage.Options);
        var results = new SqliteSubagentResultStore(storage.Options, storage.PayloadStore);
        var identity = await new WindowsWorkspaceIdentityResolver().ResolveAsync(storage.RootPath, default);
        var policy = PolicySnapshotCanonicalizer.Create(1, AgentPermissionPreset.Standard, identity, [], DateTimeOffset.UtcNow);
        var policyJson = BrokerPolicyContracts.WriteSnapshot(PolicySnapshotMapper.ToWire(policy)).ToJsonString();
        var childIds = new List<RunId>();
        var parentIds = new List<RunId>();
        using var remembered = new RememberedPermissions();
        using var probeArguments = System.Text.Json.JsonDocument.Parse("{\"path\":\".\"}");
        var parentProbes = new List<(IToolGateway Gateway, RunId Run, long? Generation)>();
        ToolProposal Probe(RunId runId) => new(runId, new("step_" + Guid.NewGuid().ToString("N")),
            new("call_" + Guid.NewGuid().ToString("N")), "provider_parent_probe", 1, "list", "1.0.0", probeArguments.RootElement);
        for (var index = 0; index < 4; index++)
        {
            var parent = await storage.SeedRunWithEncryptedWorkspaceAsync(new(HarnessRunState.WaitingSubagents),
                "run_" + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), storage.RootPath);
            roots.Register(parent.Run.RunId, storage.RootPath, policy);
            parentIds.Add(parent.Run.RunId);
            if (!omitInitialParentLease)
            {
                var runtimePolicy = rememberParentPermission ? remembered.Apply(policy, parent.Run.SessionId.Value) : policy;
                var parentGateway = await execution.Factory.CreateAsync(storage.RootPath, runtimePolicy, parent.Run.RunId, parent.Run.SessionId.Value, default);
                if (rememberParentPermission)
                    parentProbes.Add((parentGateway, parent.Run.RunId, (await parentGateway.EvaluateAsync(Probe(parent.Run.RunId), default)).GatewayGeneration));
            }
            var childId = new RunId("run_" + Guid.NewGuid().ToString("N"));
            childIds.Add(childId);
            var now = DateTimeOffset.UtcNow;
            var run = new AgentRun(childId, new(Guid.NewGuid().ToString("N")), identity.WorkspaceId,
                new(HarnessRunState.Created), 0, 1, 1, 0, 0, policy.Preset, policy.Version, policy.Sha256, now, now);
            var request = new NewAgentRun(run,
                [new(new("evt_" + Guid.NewGuid().ToString("N")), HarnessEventKind.RunStateChanged)], [], [],
                new(new string('a', 64), new string('b', 64), 1));
            await agents.CreateAsync(new("agent_" + index, parent.Run.RunId, "spawn_" + index, SubagentRole.Review, request), default);
            await selections.PinAsync(childId, new("Custom OpenAI", "custom", "chat_completions",
                "https://example.invalid/v1/", "fixture-model", null, "{}", null), default);
            await storage.PayloadStore.CaptureBaseContextAsync(childId, [], now, default);
            await scopes.SaveAsync(parent.Run.RunId, childId,
                new(storage.RootPath, storage.RootPath, new(policy.Version, policy.Sha256), policyJson, null, policyJson), default);
        }
        using var scheduler = new SubagentScheduler(storage.EventStore, agents, scopes, roots, new SystemTimeProvider(), execution.Factory);
        using var waits = new SubagentWaitCoordinator(storage.EventStore, results, new SystemTimeProvider());
        var binder = new SubagentRuntimeBinder(storage.EventStore, agents, scopes, execution.Factory);
        var blocked = new BlockingModel();
        IModelStepRunner model = closeParents ? blocked : ScriptedModelStepRunner.FromJsonForTests(
            """[{"text":"review one"},{"text":"review two"},{"text":"review three"},{"text":"review four"}]""");
        var factory = new AgentRuntimeCoordinatorFactory(model,
            storage.EventStore, storage.Options, storage.PayloadStore, settings: new Settings(), time: new SystemTimeProvider(),
            contextLimit: new StaticModelContextLimitProvider(100000), brokerGateways: execution.Factory, subagentBinder: binder);
        var sessions = new SessionStore(new DataPaths(Path.Combine(storage.RootPath, "settings.json"),
            Path.Combine(storage.RootPath, "sessions"), Path.Combine(storage.RootPath, "activity.log")), TimeProvider.System);
        var dispatcher = new AgentConversationOutboxDispatcher(new HarnessOutboxStore(storage.Options), storage.PayloadStore, sessions,
            new HarnessModelContextBootstrapper(sessions, storage.PayloadStore, new CryptographicIdGenerator(), TimeProvider.System),
            new AgentRunHistoryTransitionSink(factory, roots), TimeProvider.System);
        using var loop = new AgentRuntimeLoopService(dispatcher, storage.EventStore, factory, roots,
            NullLogger<AgentRuntimeLoopService>.Instance, brokerGateways: execution.Factory, subagents: scheduler, subagentWaits: waits);
        if (closeParents)
        {
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var driving = loop.StepOnceAsync(lifetime.Token);
            try
            {
                await blocked.AllStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                foreach (var parentId in parentIds) await loop.CancelAndRemoveRunAsync(parentId, default);
                await driving.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(4, blocked.Canceled);
                foreach (var childId in childIds)
                    Assert.Equal(HarnessRunState.Cancelled, (await storage.EventStore.GetRunAsync(childId, default))!.State);
                Assert.Empty(roots.Snapshot());
            }
            finally
            {
                lifetime.Cancel();
                await driving.WaitAsync(TimeSpan.FromSeconds(5));
            }
            return;
        }
        await loop.StepOnceAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
        foreach (var probe in parentProbes)
        {
            Assert.NotNull(probe.Generation);
            Assert.Equal(probe.Generation, (await probe.Gateway.EvaluateAsync(Probe(probe.Run), default)).GatewayGeneration);
        }
        foreach (var childId in childIds)
            Assert.Equal(HarnessRunState.Completed, (await storage.EventStore.GetRunAsync(childId, default))!.State);
        Assert.Equal(4, roots.Snapshot().Count);
        await loop.StepOnceAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
        foreach (var root in roots.Snapshot())
        {
            var owner = Assert.Single(await agents.ListAsync(new(root.RunId), default));
            Assert.Equal(HarnessRunState.Completed, Assert.Single(await results.ListAsync(new(root.RunId), owner.AgentId, default)).State);
        }
        Assert.Null(await new HarnessOutboxStore(storage.Options).ClaimNextAsync(DateTimeOffset.UtcNow, default));
    }

    private sealed class RememberedPermissions : IDisposable
    {
        private readonly List<string> _sessions = [];
        public PolicySnapshot Apply(PolicySnapshot policy, string session)
        {
            _sessions.Add(session);
            SessionApprovalLedger.Remember(session, new PolicyMatcher(ToolId: "list"), DateTimeOffset.UtcNow);
            return SessionApprovalLedger.Apply(policy, session, DateTimeOffset.UtcNow)!;
        }
        public void Dispose() { foreach (var session in _sessions) SessionApprovalLedger.ClearSession(session); }
    }

    private sealed class BlockingModel : IModelStepRunner
    {
        private int _started;
        private int _canceled;
        public TaskCompletionSource AllStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Canceled => Volatile.Read(ref _canceled);
        public async Task<ModelStepResult> RunStepAsync(ModelStepRequest request,
            Func<AssistantDelta, ValueTask> publishDelta, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _started) == 4) AllStarted.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { if (ct.IsCancellationRequested) Interlocked.Increment(ref _canceled); }
            throw new InvalidOperationException("blocking_model_completed");
        }
    }

    private sealed class Settings : ISettingsStore
    {
        public Task<SettingsSnapshot> ReadAsync(CancellationToken ct) => Task.FromResult(new SettingsSnapshot(
            new JsonObject(), "OpenAI", "fixture-model", "", "http://localhost:8000/v1", "fixture-model", ""));
        public Task SaveAsync(SettingsSnapshot candidate, CancellationToken ct) => Task.CompletedTask;
    }
}
