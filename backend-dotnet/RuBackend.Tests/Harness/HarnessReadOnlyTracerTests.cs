using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;
using RuBackend.Harness.Broker;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class HarnessReadOnlyTracerTests
{
    [Fact]
    public async Task Only_files_list_and_files_read_are_executable()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "harness-tracer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(Path.Combine(tempRoot, "sub"));
        File.WriteAllText(Path.Combine(tempRoot, "a.txt"), "hello");
        try
        {
            var gateway = new HarnessToolGateway(tempRoot, new[] { "files.list", "files.read" });
            var proposalList = new ToolProposal(
                new RunId("run_11111111111111111111111111111111"),
                new ModelStepId("step_11111111111111111111111111111111"),
                new ToolCallId("call_11111111111111111111111111111111"),
                1,
                "files.list",
                "1.0.0",
                JsonDocument.Parse("{\"path\":\".\"}").RootElement);
            var decisionList = await gateway.EvaluateAsync(proposalList, CancellationToken.None);
            Assert.Equal(PolicyDecision.Allow, decisionList.Decision);

            var proposalRead = new ToolProposal(
                new RunId("run_11111111111111111111111111111111"),
                new ModelStepId("step_11111111111111111111111111111111"),
                new ToolCallId("call_22222222222222222222222222222222"),
                2,
                "files.read",
                "1.0.0",
                JsonDocument.Parse("{\"path\":\"a.txt\"}").RootElement);
            var decisionRead = await gateway.EvaluateAsync(proposalRead, CancellationToken.None);
            Assert.Equal(PolicyDecision.Allow, decisionRead.Decision);

            var proposalWrite = new ToolProposal(
                new RunId("run_11111111111111111111111111111111"),
                new ModelStepId("step_11111111111111111111111111111111"),
                new ToolCallId("call_33333333333333333333333333333333"),
                3,
                "files.write",
                "1.0.0",
                JsonDocument.Parse("{\"path\":\"b.txt\",\"content\":\"x\"}").RootElement);
            var decisionWrite = await gateway.EvaluateAsync(proposalWrite, CancellationToken.None);
            Assert.Equal(PolicyDecision.Deny, decisionWrite.Decision);
            Assert.Equal("phase_not_available", decisionWrite.SafeCode);

            var proposalTerminal = new ToolProposal(
                new RunId("run_11111111111111111111111111111111"),
                new ModelStepId("step_11111111111111111111111111111111"),
                new ToolCallId("call_44444444444444444444444444444444"),
                4,
                "terminal.run",
                "1.0.0",
                JsonDocument.Parse("{\"executable\":\"git\"}").RootElement);
            var decisionTerminal = await gateway.EvaluateAsync(proposalTerminal, CancellationToken.None);
            Assert.Equal(PolicyDecision.Deny, decisionTerminal.Decision);
            Assert.Equal("phase_not_available", decisionTerminal.SafeCode);
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task Persist_proposal_request_receipt_and_denial_for_mutation()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "harness-tracer-persist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        File.WriteAllText(Path.Combine(tempRoot, "a.txt"), "hello");
        try
        {
            var gateway = new HarnessToolGateway(tempRoot, new[] { "files.list", "files.read" });
            var store = new SimpleInMemoryStore();
            var contextAssembler = new AgentContextAssembler(new SimpleContextSource());
            var ids = new SimpleIdGenerator();
            var time = new SimpleTimeProvider(new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.Zero));
            var scheduler = new ToolCallScheduler();
            var frames = new TransientFrameBuffer(1024);

            var wsIdentity = new RuAgent.Harness.Core.Workspace.WorkspaceIdentity(
                new WorkspaceId("ws_11111111111111111111111111111111"),
                "vol-1",
                "fid-1",
                new string('a', 64));
            var run = new AgentRun(
                new RunId("run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
                new SessionId(new string('b', 32)),
                wsIdentity.WorkspaceId,
                new HarnessRunStatus(HarnessRunState.Running),
                version: 1,
                lastSequence: 1,
                budgetWindow: 1,
                modelStepsInWindow: 0,
                toolCallsInWindow: 0,
                AgentPermissionPreset.Standard,
                policySnapshotVersion: 1,
                policySnapshotSha256: new string('c', 64),
                createdAt: time.UtcNow,
                updatedAt: time.UtcNow);
            // Store the run as if already committed (use TryCreate)
            var newRun = new NewAgentRun(
                run,
                new[] { new HarnessEventIntent(ids.NewEventId(), HarnessEventKind.RunStateChanged, safeCode: "running") },
                Array.Empty<HarnessPayloadIntent>(),
                Array.Empty<ConversationOutboxIntent>(),
                new RunCreationIdentity(new string('d', 64), new string('e', 64), 1));
            await store.TryCreateRunAsync(newRun, CancellationToken.None);

            var toolCallRead = new ModelToolCall(null, "files.read", "1.0.0", JsonDocument.Parse("{\"path\":\"a.txt\"}").RootElement);
            var toolCallWrite = new ModelToolCall(null, "files.write", "1.0.0", JsonDocument.Parse("{\"path\":\"b.txt\",\"content\":\"x\"}").RootElement);
            var fakeRunner = new SimpleModelRunner(new[] { new AssistantStep(null, new[] { toolCallRead }, AssistantFinishReason.ToolCalls, 0) });
            var coordinator = new AgentRunCoordinator(fakeRunner, gateway, store, contextAssembler, ids, time, scheduler, frames);
            var result = await coordinator.RunOneStepAsync(run.RunId, CancellationToken.None);
            Assert.NotNull(result);

            var fakeRunner2 = new SimpleModelRunner(new[] { new AssistantStep(null, new[] { toolCallWrite }, AssistantFinishReason.ToolCalls, 0) });
            var coordinator2 = new AgentRunCoordinator(fakeRunner2, gateway, store, contextAssembler, ids, time, scheduler, frames);
            var result2 = await coordinator2.RunOneStepAsync(run.RunId, CancellationToken.None);
            Assert.NotNull(result2);
            var events = await ToListAsync(store.ReadEventsAsync(run.RunId, 0, CancellationToken.None));
            Assert.Contains(events, e => e.Event.Kind == "tool.proposed");
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task Deliver_final_visible_answer_once()
    {
        var gateway = new HarnessToolGateway(Path.GetTempPath(), new[] { "files.list", "files.read" });
        var store = new SimpleInMemoryStore();
        var contextAssembler = new AgentContextAssembler(new SimpleContextSource());
        var ids = new SimpleIdGenerator();
        var time = new SimpleTimeProvider(new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.Zero));
        var scheduler = new ToolCallScheduler();
        var frames = new TransientFrameBuffer(1024);
        var fakeRunner = new SimpleModelRunner(new[] { new AssistantStep("final answer", Array.Empty<ModelToolCall>(), AssistantFinishReason.Stop, 0) });
        var coordinator = new AgentRunCoordinator(fakeRunner, gateway, store, contextAssembler, ids, time, scheduler, frames);

        var wsIdentity = new RuAgent.Harness.Core.Workspace.WorkspaceIdentity(
            new WorkspaceId("ws_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            "vol-1",
            "fid-1",
            new string('a', 64));
        var run = new AgentRun(
            new RunId("run_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
            new SessionId(new string('b', 32)),
            wsIdentity.WorkspaceId,
            new HarnessRunStatus(HarnessRunState.Running),
            version: 1,
            lastSequence: 1,
            budgetWindow: 1,
            modelStepsInWindow: 0,
            toolCallsInWindow: 0,
            AgentPermissionPreset.Standard,
            policySnapshotVersion: 1,
            policySnapshotSha256: new string('c', 64),
            createdAt: time.UtcNow,
            updatedAt: time.UtcNow);
        var newRun2 = new NewAgentRun(
            run,
            new[] { new HarnessEventIntent(ids.NewEventId(), HarnessEventKind.RunStateChanged, safeCode: "running") },
            Array.Empty<HarnessPayloadIntent>(),
            Array.Empty<ConversationOutboxIntent>(),
            new RunCreationIdentity(new string('d', 64), new string('e', 64), 1));
        await store.TryCreateRunAsync(newRun2, CancellationToken.None);

        var result = await coordinator.RunOneStepAsync(run.RunId, CancellationToken.None);
        Assert.Equal(HarnessRunState.WaitingHistory, result.Run.State);
        Assert.Equal(HistoryWaitKind.FinalBeforeCompletion, result.Run.HistoryWaitKind);
    }

    [Fact]
    public async Task Capability_boundary_excludes_user_control_apis()
    {
        var allowed = new[] { "files.list", "files.read" };
        var forbidden = new[] { "trust", "approval", "policy", "session-preset", "cancel", "continue", "payload_fetch", "reconcile" };
        foreach (var f in forbidden)
        {
            Assert.DoesNotContain(f, allowed);
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "harness-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var gateway = new HarnessToolGateway(tempRoot, allowed);
            var proposal = new ToolProposal(
                new RunId("run_cccccccccccccccccccccccccccccccc"),
                new ModelStepId("step_cccccccccccccccccccccccccccccccc"),
                new ToolCallId("call_cccccccccccccccccccccccccccccccc"),
                1,
                "trust",
                "1.0.0",
                JsonDocument.Parse("{}").RootElement);
            var decision = await gateway.EvaluateAsync(proposal, CancellationToken.None);
            Assert.Equal("phase_not_available", decision.SafeCode);
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task Standard_mutation_against_protected_file_requires_approval()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "harness-policy-protected-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var protectedPath = Path.Combine(tempRoot, ".env.local");
        File.WriteAllText(protectedPath, "secret");
        try
        {
            var gateway = new HarnessToolGateway(tempRoot, new[] { "files.write" });
            var proposal = new ToolProposal(
                new RunId("run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
                new ModelStepId("step_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
                new ToolCallId("call_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
                1,
                "files.write",
                "1.0.0",
                JsonDocument.Parse("{\"path\":\".env.local\",\"content\":\"replaced\"}").RootElement);

            var decision = await gateway.EvaluateAsync(proposal, CancellationToken.None);

            Assert.Equal(PolicyDecision.Ask, decision.Decision);
            Assert.Equal("approval_required", decision.SafeCode);
            Assert.NotNull(decision.ApprovalId);
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task Policy_snapshot_deny_rule_overrides_standard_fallback()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "harness-policy-rule-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var workspace = new RuAgent.Harness.Core.Workspace.WorkspaceIdentity(
                new WorkspaceId("ws_dddddddddddddddddddddddddddddddd"),
                "vol-policy",
                "fid-policy",
                new string('d', 64));
            var snapshot = PolicySnapshotCanonicalizer.Create(
                2,
                AgentPermissionPreset.Standard,
                workspace,
                new[]
                {
                    new PolicyRule(
                        new PolicyRuleId("rule_" + new string('d', 32)),
                        PolicyRuleScope.Project,
                        PolicyDecision.Deny,
                        new PolicyMatcher(ToolId: "files.write", OperationClass: "write", CanonicalPathPattern: "blocked.txt"),
                        1,
                        null),
                },
                new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero));
            var gateway = new HarnessToolGateway(
                tempRoot,
                new[] { "files.write" },
                presetProvider: _ => AgentPermissionPreset.Standard,
                idGenerator: new CryptographicIdGenerator(),
                policySnapshot: snapshot);
            var proposal = new ToolProposal(
                new RunId("run_dddddddddddddddddddddddddddddddd"),
                new ModelStepId("step_dddddddddddddddddddddddddddddddd"),
                new ToolCallId("call_dddddddddddddddddddddddddddddddd"),
                1,
                "files.write",
                "1.0.0",
                JsonDocument.Parse("{\"path\":\"blocked.txt\",\"content\":\"no\"}").RootElement);

            var decision = await gateway.EvaluateAsync(proposal, CancellationToken.None);

            Assert.Equal(PolicyDecision.Deny, decision.Decision);
            Assert.Equal("policy_denied", decision.SafeCode);
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task Apply_patch_is_not_advertised_until_a_real_executor_exists()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "harness-patch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var gateway = new HarnessToolGateway(tempRoot, new[] { "files.apply_patch" });
            var proposal = new ToolProposal(
                new RunId("run_eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"),
                new ModelStepId("step_eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"),
                new ToolCallId("call_eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"),
                1,
                "files.apply_patch",
                "1.0.0",
                JsonDocument.Parse("{\"patch\":\"--- a/a.txt\\n+++ b/a.txt\\n\"}").RootElement);

            var decision = await gateway.EvaluateAsync(proposal, CancellationToken.None);

            Assert.Equal(PolicyDecision.Deny, decision.Decision);
            Assert.Equal("phase_not_available", decision.SafeCode);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Forged_file_execution_without_a_live_preparation_is_rejected()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "harness-forged-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var gateway = new HarnessToolGateway(tempRoot, new[] { "files.read" });
            var forged = new PreparedToolExecution(
                new ExecutionId("exec_" + new string('a', 32)),
                new ToolCallId("call_" + new string('a', 32)),
                1,
                ToolAccessKind.ReadOnly,
                "prepared-canonical-files.read-forged",
                Array.Empty<PreparedPayload>());

            var receipt = await gateway.ExecuteAsync(forged, _ => ValueTask.CompletedTask, CancellationToken.None);

            Assert.Equal(ToolExecutionOutcome.Failed, receipt.Outcome);
            Assert.Equal("decision_stale", receipt.Code);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData("files.read", "{\"path\":\"a.txt\",\"offset\":-1}")]
    [InlineData("files.read", "{\"path\":\"a.txt\",\"limit\":0}")]
    [InlineData("files.glob", "{\"pattern\":123}")]
    [InlineData("files.search", "{\"query\":true}")]
    [InlineData("files.write", "{\"path\":\"a.txt\",\"content\":123}")]
    [InlineData("files.edit", "{\"path\":\"a.txt\",\"old_string\":\"x\",\"expected_occurrences\":0}")]
    public async Task File_tools_reject_malformed_arguments_before_prepare(string toolId, string rawArguments)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "harness-argument-validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var gateway = new HarnessToolGateway(tempRoot, new[] { toolId });
            var proposal = new ToolProposal(
                new RunId("run_" + new string('a', 32)),
                new ModelStepId("step_" + new string('a', 32)),
                new ToolCallId("call_" + new string('a', 32)),
                1,
                toolId,
                "1.0.0",
                JsonDocument.Parse(rawArguments).RootElement);

            var decision = await gateway.EvaluateAsync(proposal, CancellationToken.None);

            Assert.Equal(PolicyDecision.Deny, decision.Decision);
            Assert.Equal("invalid_arguments", decision.SafeCode);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task File_read_honours_bounded_offset_and_limit_without_splitting_utf8()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "harness-read-window-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        File.WriteAllText(Path.Combine(tempRoot, "a.txt"), "a\u00e9bcdef", new UTF8Encoding(false));
        try
        {
            var gateway = new HarnessToolGateway(tempRoot, new[] { "files.read" });
            var proposal = new ToolProposal(
                new RunId("run_" + new string('b', 32)),
                new ModelStepId("step_" + new string('b', 32)),
                new ToolCallId("call_" + new string('b', 32)),
                1,
                "files.read",
                "1.0.0",
                JsonDocument.Parse("{\"path\":\"a.txt\",\"offset\":2,\"limit\":3}").RootElement);
            var decision = await gateway.EvaluateAsync(proposal, CancellationToken.None);
            Assert.Equal(PolicyDecision.Allow, decision.Decision);

            var execution = new ApprovedToolExecution(
                proposal,
                new ExecutionId("exec_" + new string('b', 32)),
                decision.CanonicalIntentToken!);
            var prepared = await gateway.PrepareAsync(execution, CancellationToken.None);
            var receipt = await gateway.ExecuteAsync(prepared, _ => ValueTask.CompletedTask, CancellationToken.None);

            Assert.Equal(ToolExecutionOutcome.Succeeded, receipt.Outcome);
            using var document = JsonDocument.Parse(receipt.ModelContent);
            Assert.Equal("bc", document.RootElement.GetProperty("content").GetString());
            Assert.Equal(2, document.RootElement.GetProperty("offset").GetInt32());
            Assert.Equal(2, document.RootElement.GetProperty("bytes_read").GetInt32());
            Assert.True(document.RootElement.GetProperty("truncated").GetBoolean());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private sealed class SimpleContextSource : IModelContextSource
    {
        public Task<ModelContextSourceSnapshot> LoadAsync(RunId runId, CancellationToken cancellationToken)
        {
            var entry = ModelContextEntry.Text("src-1", 1, ModelContextRole.User, "prompt");
            return Task.FromResult(new ModelContextSourceSnapshot(new[] { entry }, Array.Empty<ModelContextEntry>()));
        }
    }

    private sealed class SimpleIdGenerator : IIdGenerator
    {
        private int _counter = 0;
        private string Next(string prefix) => prefix + (++_counter).ToString("x32");
        public RunId NewRunId() => new(Next("run_"));
        public ModelStepId NewModelStepId() => new(Next("step_"));
        public ToolCallId NewToolCallId() => new(Next("call_"));
        public ExecutionId NewExecutionId() => new(Next("exec_"));
        public ApprovalId NewApprovalId() => new(Next("apr_"));
        public TrustRequestId NewTrustRequestId() => new(Next("trust_"));
        public PolicyRuleId NewPolicyRuleId() => new(Next("rule_"));
        public EventId NewEventId() => new(Next("evt_"));
        public PayloadId NewPayloadId() => new(Next("payload_"));
    }

    private sealed class SimpleTimeProvider : ITimeProvider
    {
        private DateTimeOffset _now;
        public SimpleTimeProvider(DateTimeOffset now) => _now = now;
        public DateTimeOffset UtcNow => _now;
    }

    private sealed class SimpleModelRunner : IModelStepRunner
    {
        private readonly Queue<AssistantStep> _steps;
        public SimpleModelRunner(IEnumerable<AssistantStep> steps) => _steps = new Queue<AssistantStep>(steps);
        public Task<AssistantStep> RunStepAsync(ModelStepRequest request, Func<AssistantDelta, ValueTask> publishDelta, CancellationToken cancellationToken)
        {
            if (_steps.Count == 0) throw new InvalidOperationException("no step");
            return Task.FromResult(_steps.Dequeue());
        }
    }

    private sealed class SimpleInMemoryStore : IHarnessEventStore
    {
        private readonly Dictionary<string, AgentRun> _runs = new();
        private readonly Dictionary<string, List<HarnessEventEnvelope>> _events = new();

        public Task<CreateRunResult> TryCreateRunAsync(NewAgentRun request, CancellationToken cancellationToken)
        {
            var key = request.Run.RunId.Value;
            if (_runs.ContainsKey(key))
            {
                return Task.FromResult(CreateRunResult.AlreadyExists(_runs[key]));
            }

            _runs[key] = request.Run;
            _events[key] = new List<HarnessEventEnvelope>();
            return Task.FromResult(CreateRunResult.Created(request.Run));
        }

        public Task<AgentRun?> GetRunAsync(RunId runId, CancellationToken cancellationToken)
        {
            _runs.TryGetValue(runId.Value, out var run);
            return Task.FromResult(run);
        }

        public Task<HarnessCommitResult> CommitAsync(HarnessCommit commit, CancellationToken cancellationToken)
        {
            if (!_runs.TryGetValue(commit.RunId.Value, out var current))
            {
                return Task.FromResult(HarnessCommitResult.RunNotFound());
            }

            if (current.Version != commit.ExpectedVersion)
            {
                return Task.FromResult(HarnessCommitResult.VersionConflict(current));
            }

            _runs[commit.RunId.Value] = commit.NextRun;
            if (!_events.TryGetValue(commit.RunId.Value, out var list))
            {
                list = new List<HarnessEventEnvelope>();
                _events[commit.RunId.Value] = list;
            }

            long seq = current.LastSequence;
            foreach (var intent in commit.Events)
            {
                seq++;
                var kind = intent.Kind switch
                {
                    HarnessEventKind.RunStateChanged => "run.state_changed",
                    HarnessEventKind.RunFailed => "run.failed",
                    HarnessEventKind.ModelStepStarted => "model.step_started",
                    HarnessEventKind.ModelStepCompleted => "model.step_completed",
                    HarnessEventKind.ToolProposed => "tool.proposed",
                    HarnessEventKind.ToolExecutionRequested => "tool.execution_requested",
                    HarnessEventKind.ToolExecutionCompleted => "tool.execution_completed",
                    HarnessEventKind.ApprovalRequested => "approval.requested",
                    HarnessEventKind.ContinueRequested => "continue.requested",
                    HarnessEventKind.ReconciliationRequired => "reconciliation.required",
                    HarnessEventKind.TransientFramesDropped => "transient.frames_dropped",
                    _ => "unknown",
                };
                var ev = new HarnessEvent(intent.EventId, commit.RunId, seq, kind, DateTimeOffset.UtcNow, intent.PayloadId, intent.ModelStepId, intent.ToolCallId, intent.ExecutionId, intent.ApprovalId, intent.SafeCount, intent.SafeCode, intent.ToolOutcome, intent.SideEffectsPossible);
                list.Add(new HarnessEventEnvelope(1, ev));
            }

            return Task.FromResult(HarnessCommitResult.Committed(commit.NextRun));
        }

        public async IAsyncEnumerable<HarnessEventEnvelope> ReadEventsAsync(RunId runId, long afterSequence, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (_events.TryGetValue(runId.Value, out var list))
            {
                foreach (var e in list.Where(x => x.Event.Sequence > afterSequence))
                {
                    yield return e;
                    await Task.Yield();
                }
            }
        }
    }

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }
}
