using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Core.Tests.Fakes;
using Xunit;
using RuAgent.Protocol.Harness;

namespace RuAgent.Harness.Core.Tests;

public sealed partial class ContextCompactionCoordinatorTests
{
    [Fact]
    public async Task Context_snapshot_is_committed_with_the_prepared_step_before_dispatch()
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var source = new ReloadingSource(Snapshot(ModelContextEntry.Text("user", 1, ModelContextRole.User, "task")),
            Snapshot(ModelContextEntry.Text("user", 1, ModelContextRole.User, "task")));
        var model = new PreparedFakeModelStepRunner
        {
            RuntimeSnapshot = "untrusted context snapshot",
            EstimatedInputTokensOverride = 100,
            OnDispatch = () =>
            {
                var commit = Assert.Single(store.Commits);
                Assert.Contains(commit.Events, evt => evt.Kind == HarnessEventKind.ModelStepStarted);
                var payload = Assert.Single(commit.Payloads);
                Assert.Equal(HarnessPayloadKind.RuntimeContextSnapshot, payload.Kind);
                Assert.Equal(HarnessPayloadAudience.ModelContext, payload.Audience);
            },
        };
        var coordinator = new AgentRunCoordinator(model, new FakeToolGateway(), store, new AgentContextAssembler(source),
            new SequenceIdGenerator(), new IncrementingTimeProvider(Epoch), new ToolCallScheduler(), new TransientFrameBuffer(32),
            contextLimit: new StaticModelContextLimitProvider(2_000),
            promptContextProvider: new FakeSystemPromptContextProvider());
        await coordinator.RunOneStepAsync(RunId(), default);
        Assert.NotNull(model.DispatchedPreparation);
    }
    private static readonly DateTimeOffset Epoch =
        new(2026, 8, 24, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Automatic_preflight_commits_compaction_before_invoking_the_model()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(new AssistantStep(
            "done",
            Array.Empty<ModelToolCall>(),
            AssistantFinishReason.Stop,
            estimatedInputTokens: 16));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var source = new ReloadingSource(
            Snapshot(
                ModelContextEntry.Text("history-a", 1, ModelContextRole.User, new string('a', 1_800)),
                ModelContextEntry.Text("history-b", 2, ModelContextRole.User, new string('b', 1_800)),
                ModelContextEntry.Text("current", 3, ModelContextRole.User, "current request")),
            Snapshot(
                ModelContextEntry.Summary(
                    "compact-test",
                    1,
                    "summarized history",
                    [new ContextEntryRange("history-a", 1, 1), new ContextEntryRange("history-b", 2, 2)]),
                ModelContextEntry.Text("current", 3, ModelContextRole.User, "current request")));
        var coordinator = Coordinator(model, store, source, limitTokens: 1_000);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Null(result.ErrorCode);
        Assert.Single(model.Requests);
        Assert.Contains(model.Requests[0].Context.Entries, entry => entry.Kind == ModelContextEntryKind.Summary);
        var compactionCommit = Assert.Single(
            store.Commits,
            commit => commit.ContextCompaction is not null);
        Assert.Equal(HarnessPayloadKind.ModelContextSummary, Assert.Single(compactionCommit.Payloads).Kind);
        Assert.Equal(HarnessEventKind.ContextCompacted, Assert.Single(compactionCommit.Events).Kind);
        Assert.Equal(HarnessRunState.WaitingHistory, result.Run.State);
    }

    [Fact]
    public async Task Exact_request_is_reprepared_after_compaction_and_that_projection_is_dispatched()
    {
        var model = new PreparedFakeModelStepRunner();
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var source = new ReloadingSource(
            Snapshot(
                ModelContextEntry.Text("history-a", 1, ModelContextRole.User, new string('a', 1_800)),
                ModelContextEntry.Text("history-b", 2, ModelContextRole.User, new string('b', 1_800)),
                ModelContextEntry.Text("current", 3, ModelContextRole.User, "current request")),
            Snapshot(
                ModelContextEntry.Summary("compact-test", 1, "summarized history",
                    [new ContextEntryRange("history-a", 1, 1), new ContextEntryRange("history-b", 2, 2)]),
                ModelContextEntry.Text("current", 3, ModelContextRole.User, "current request")));
        var coordinator = new AgentRunCoordinator(
            model,
            new FakeToolGateway(),
            store,
            new AgentContextAssembler(source),
            new SequenceIdGenerator(),
            new IncrementingTimeProvider(Epoch),
            new ToolCallScheduler(),
            new TransientFrameBuffer(32),
            contextLimit: new StaticModelContextLimitProvider(2_000),
            promptContextProvider: new FakeSystemPromptContextProvider());

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Null(result.ErrorCode);
        Assert.Equal(2, model.PreparedRequests.Count);
        Assert.NotSame(model.PreparedRequests[0].Context, model.PreparedRequests[1].Context);
        Assert.Same(model.PreparedProjections[1], model.DispatchedPreparation);
        Assert.Contains(model.DispatchedPreparationContext!.Entries,
            entry => entry.Kind == ModelContextEntryKind.Summary);
    }

    [Fact]
    public async Task Provider_backed_runner_without_exact_projection_fails_before_dispatch()
    {
        var model = new UnpreparedProviderModelStepRunner();
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var coordinator = new AgentRunCoordinator(
            model,
            new FakeToolGateway(),
            store,
            new AgentContextAssembler(new ReloadingSource(
                Snapshot(ModelContextEntry.Text("current", 1, ModelContextRole.User, "current request")),
                Snapshot(ModelContextEntry.Text("current", 1, ModelContextRole.User, "current request")))),
            new SequenceIdGenerator(),
            new IncrementingTimeProvider(Epoch),
            new ToolCallScheduler(),
            new TransientFrameBuffer(32),
            contextLimit: new StaticModelContextLimitProvider(2_000),
            promptContextProvider: new FakeSystemPromptContextProvider());

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal("provider_request_projection_unavailable", result.ErrorCode);
        Assert.Equal(0, model.DispatchCount);
        Assert.Equal(HarnessRunState.Failed, result.Run.State);
    }

    [Fact]
    public async Task Protected_only_context_fails_closed_without_invoking_the_model()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(new AssistantStep(
            "never",
            Array.Empty<ModelToolCall>(),
            AssistantFinishReason.Stop,
            estimatedInputTokens: 16));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var source = new ReloadingSource(
            Snapshot(ModelContextEntry.Text(
                "current",
                1,
                ModelContextRole.User,
                new string('x', 5_000))),
            Snapshot(ModelContextEntry.Text(
                "current",
                1,
                ModelContextRole.User,
                new string('x', 5_000))));
        var coordinator = Coordinator(model, store, source, limitTokens: 1_000);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Equal("context_limit_reached", result.ErrorCode);
        Assert.Empty(model.Requests);
        Assert.DoesNotContain(store.Commits, commit => commit.ContextCompaction is not null);
        var limitEvent = Assert.Single(
            store.Commits.SelectMany(commit => commit.Events),
            item => item.SafeCode == "context_limit_reached");
        Assert.Equal(HarnessEventKind.ModelStepStarted, limitEvent.Kind);
        Assert.Null(limitEvent.PayloadId);
    }

    [Fact]
    public async Task Manual_compaction_is_a_no_op_when_no_entries_are_eligible()
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var source = new ReloadingSource(
            Snapshot(
                ModelContextEntry.Text("base:system", 1, ModelContextRole.System, "rules"),
                ModelContextEntry.Text("current", 2, ModelContextRole.User, new string('x', 2_000))),
            Snapshot(
                ModelContextEntry.Text("base:system", 1, ModelContextRole.System, "rules"),
                ModelContextEntry.Text("current", 2, ModelContextRole.User, new string('x', 2_000))));
        var coordinator = Coordinator(
            new FakeModelStepRunner(),
            store,
            source,
            limitTokens: 1_000);

        var first = await coordinator.CompactAsync(
            RunId(),
            ContextCompactionStrategy.Aggressive,
            ContextCompactionReason.Manual,
            CancellationToken.None);
        var second = await coordinator.CompactAsync(
            RunId(),
            ContextCompactionStrategy.Aggressive,
            ContextCompactionReason.Manual,
            CancellationToken.None);

        Assert.Equal(ContextCompactionOutcome.NoOp, first.Outcome);
        Assert.Equal(ContextCompactionOutcome.NoOp, second.Outcome);
        Assert.Empty(store.Commits);
    }

    [Fact]
    public async Task Manual_compaction_surfaces_a_compare_and_swap_conflict()
    {
        var store = new InMemoryHarnessEventStore
        {
            ReturnVersionConflict = true,
        };
        store.Seed(Run());
        var source = new ReloadingSource(
            Snapshot(
                ModelContextEntry.Text("history", 1, ModelContextRole.User, new string('h', 2_000)),
                ModelContextEntry.Text("current", 2, ModelContextRole.User, "current")),
            Snapshot(
                ModelContextEntry.Text("history", 1, ModelContextRole.User, new string('h', 2_000)),
                ModelContextEntry.Text("current", 2, ModelContextRole.User, "current")));
        var coordinator = Coordinator(
            new FakeModelStepRunner(),
            store,
            source,
            limitTokens: 1_000);

        var result = await coordinator.CompactAsync(
            RunId(),
            ContextCompactionStrategy.Normal,
            ContextCompactionReason.Manual,
            CancellationToken.None);

        Assert.Equal(ContextCompactionOutcome.CommitConflict, result.Outcome);
        Assert.Equal("commit_conflict", result.ErrorCode);
        Assert.Empty(store.Commits);
    }

    [Fact]
    public async Task Automatic_preflight_publishes_a_bounded_compaction_activity()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(new AssistantStep(
            "done",
            Array.Empty<ModelToolCall>(),
            AssistantFinishReason.Stop,
            estimatedInputTokens: 16));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var source = new ReloadingSource(
            Snapshot(
                ModelContextEntry.Text("history-a", 1, ModelContextRole.User, new string('a', 1_800)),
                ModelContextEntry.Text("history-b", 2, ModelContextRole.User, new string('b', 1_800)),
                ModelContextEntry.Text("current", 3, ModelContextRole.User, "current request")),
            Snapshot(
                ModelContextEntry.Summary(
                    "compact-test",
                    1,
                    "summarized history",
                    [new ContextEntryRange("history-a", 1, 1), new ContextEntryRange("history-b", 2, 2)]),
                ModelContextEntry.Text("current", 3, ModelContextRole.User, "current request")));
        var sink = new RecordingSink();
        var coordinator = Coordinator(model, store, source, limitTokens: 1_000, sink);

        await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        var activity = Assert.Single(sink.Items, item => item.Activity.Kind == AgentActivityKind.Compaction).Activity;
        Assert.Equal(AgentActivityState.Completed, activity.State);
        Assert.True(activity.Count > 0);
        Assert.True(activity.DurationMs > 0);
        Assert.StartsWith("act_compaction_", activity.ActivityId, StringComparison.Ordinal);
        Assert.Contains(RunId().Value, activity.ActivityId, StringComparison.Ordinal);
        Assert.DoesNotContain("summarized history", activity.ToString(), StringComparison.Ordinal);
    }

    private static AgentRunCoordinator Coordinator(
        FakeModelStepRunner model,
        InMemoryHarnessEventStore store,
        IModelContextSource source,
        long? limitTokens,
        IAgentActivitySink? activitySink = null) =>
        new(
            model,
            new FakeToolGateway(),
            store,
            new AgentContextAssembler(source),
            new SequenceIdGenerator(),
            new IncrementingTimeProvider(Epoch),
            new ToolCallScheduler(),
            new TransientFrameBuffer(32),
            contextLimit: new StaticModelContextLimitProvider(limitTokens),
            activitySink: activitySink,
            promptContextProvider: new FakeSystemPromptContextProvider());

    private sealed class RecordingSink : IAgentActivitySink
    {
        public List<(RunId RunId, AgentActivity Activity)> Items { get; } = new();

        public void Publish(RunId runId, AgentActivity activity) => Items.Add((runId, activity));
    }

    private sealed class PreparedFakeModelStepRunner : IProviderBackedModelStepRunner, IModelStepRequestPreparer
    {
        public string? RuntimeSnapshot { get; init; }
        public int? EstimatedInputTokensOverride { get; init; }
        public Action? OnDispatch { get; init; }
        public List<ModelStepRequest> PreparedRequests { get; } = [];
        public List<IPreparedModelStep> PreparedProjections { get; } = [];
        public IPreparedModelStep? DispatchedPreparation { get; private set; }
        public ModelContextSnapshot? DispatchedPreparationContext { get; private set; }

        public Task<IPreparedModelStep> PrepareAsync(ModelStepRequest request, CancellationToken cancellationToken)
        {
            PreparedRequests.Add(request);
            IPreparedModelStep prepared = new TestPreparedStep(
                EstimatedInputTokensOverride ?? (PreparedRequests.Count == 1 ? 1_800 : 100), 2_000, RuntimeSnapshot);
            PreparedProjections.Add(prepared);
            return Task.FromResult(prepared);
        }

        public Task<ModelStepResult> RunStepAsync(
            ModelStepRequest request,
            Func<AssistantDelta, ValueTask> publishDelta,
            CancellationToken cancellationToken)
        {
            DispatchedPreparation = request.Preparation;
            OnDispatch?.Invoke();
            DispatchedPreparationContext = request.Context;
            var receipt = SystemPromptComposer.Compose(request.PromptContext, nativeToolCalling: true).Receipt;
            return Task.FromResult(new ModelStepResult(
                new AssistantStep("done", Array.Empty<ModelToolCall>(), AssistantFinishReason.Stop, 0), receipt));
        }

        private sealed record TestPreparedStep(long EstimatedInputTokens, long MaxPromptTokens, string? RuntimeContextSnapshot) : IPreparedModelStep;
    }

    private sealed class UnpreparedProviderModelStepRunner : IProviderBackedModelStepRunner
    {
        public int DispatchCount { get; private set; }

        public Task<ModelStepResult> RunStepAsync(
            ModelStepRequest request,
            Func<AssistantDelta, ValueTask> publishDelta,
            CancellationToken cancellationToken)
        {
            DispatchCount++;
            throw new InvalidOperationException("Provider dispatch must not run without an exact prepared request.");
        }
    }

    private static ModelContextSourceSnapshot Snapshot(params ModelContextEntry[] entries) =>
        new(Array.Empty<ModelContextEntry>(), entries);

    private static AgentRun Run(int modelSteps = 0, int budgetWindow = 1) =>
        new(
            RunId(),
            new SessionId("22222222222222222222222222222222"),
            new WorkspaceId("ws_22222222222222222222222222222222"),
            new HarnessRunStatus(HarnessRunState.Running),
            0,
            0,
            budgetWindow,
            modelSteps,
            0,
            AgentPermissionPreset.Standard,
            1,
            new string('a', 64),
            Epoch,
            Epoch);

    private static RunId RunId() => new("run_22222222222222222222222222222222");

    private sealed class ReloadingSource(
        ModelContextSourceSnapshot initial,
        ModelContextSourceSnapshot reloaded) : IModelContextSource
    {
        private int _loads;

        public Task<ModelContextSourceSnapshot> LoadAsync(
            RunId runId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Interlocked.Increment(ref _loads) == 1 ? initial : reloaded);
    }

    private sealed class IncrementingTimeProvider : ITimeProvider
    {
        private DateTimeOffset _current;

        public IncrementingTimeProvider(DateTimeOffset current) => _current = current;

        public DateTimeOffset UtcNow => _current = _current.AddSeconds(1);
    }

    private sealed class SequenceIdGenerator : IIdGenerator
    {
        private int _next;

        public RunId NewRunId() => new(Value("run_"));
        public ModelStepId NewModelStepId() => new(Value("step_"));
        public ToolCallId NewToolCallId() => new(Value("call_"));
        public ExecutionId NewExecutionId() => new(Value("exec_"));
        public ApprovalId NewApprovalId() => new(Value("apr_"));
        public InputId NewInputId() => new(Value("inp_"));
        public TrustRequestId NewTrustRequestId() => new(Value("trust_"));
        public PolicyRuleId NewPolicyRuleId() => new(Value("rule_"));
        public EventId NewEventId() => new(Value("evt_"));
        public PayloadId NewPayloadId() => new(Value("payload_"));

        private string Value(string prefix) =>
            prefix + Interlocked.Increment(ref _next).ToString("x32");
    }
}
