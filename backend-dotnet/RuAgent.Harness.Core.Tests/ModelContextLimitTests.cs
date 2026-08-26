using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Reconciliation;
using RuAgent.Harness.Core.Tests.Fakes;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed class ModelContextLimitTests
{
    private static readonly DateTimeOffset Epoch =
        new(2026, 8, 24, 8, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------
    // Estimator.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Estimate_is_deterministic_and_positive_for_any_snapshot()
    {
        var snapshot = await SnapshotAsync("hello world");

        var first = ModelContextSizeEstimator.EstimateTokens(snapshot);
        var second = ModelContextSizeEstimator.EstimateTokens(snapshot);

        Assert.Equal(first, second);
        Assert.True(first >= 1);
    }

    [Fact]
    public async Task Estimate_grows_monotonically_with_content_size()
    {
        var small = ModelContextSizeEstimator.EstimateTokens(await SnapshotAsync("short"));
        var large = ModelContextSizeEstimator.EstimateTokens(
            await SnapshotAsync(new string('x', 10_000)));

        Assert.True(large > small);
    }

    // ------------------------------------------------------------------
    // Continuation summary builder.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Continuation_summary_is_deterministic_and_bounded()
    {
        var snapshot = await SnapshotAsync(new string('a', 500) + "|" + new string('b', 500));

        var first = ContinuationSummaryBuilder.Build(snapshot, maxChars: 512);
        var second = ContinuationSummaryBuilder.Build(snapshot, maxChars: 512);

        Assert.Equal(first, second);
        Assert.True(first.Text.Length <= 512);
        Assert.False(string.IsNullOrWhiteSpace(first.Text));
        Assert.True(first.SourceEntryCount > 0);
    }

    [Fact]
    public async Task Continuation_summary_prefers_the_most_recent_entries()
    {
        var texts = new List<string>();
        for (var index = 1; index <= 6; index++)
        {
            texts.Add($"older-content-{index} " + new string('p', 300));
        }

        texts.Add("the-final-marker");
        var snapshot = await SnapshotAsync(texts.ToArray());

        var summary = ContinuationSummaryBuilder.Build(snapshot, maxChars: 1024);

        Assert.Contains("the-final-marker", summary.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("older-content-1", summary.Text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Coordinator enforcement before every model step.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Provider_limit_blocks_the_step_before_any_model_invocation()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("never reached"));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var coordinator = Coordinator(model, store, limitTokens: 1);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Empty(model.Requests);
        Assert.Equal("context_limit_reached", result.ErrorCode);

        var summaryCommit = Assert.Single(
            store.Commits,
            commit => commit.Payloads.Any(payload => payload.Kind == HarnessPayloadKind.ContinuationSummary));
        var summary = Assert.Single(
            summaryCommit.Payloads,
            payload => payload.Kind == HarnessPayloadKind.ContinuationSummary);
        Assert.Equal(HarnessPayloadAudience.InternalOnly, summary.Audience);
        var startEvent = Assert.Single(summaryCommit.Events);
        Assert.Equal(HarnessEventKind.ModelStepStarted, startEvent.Kind);
        Assert.Equal("context_limit_reached", startEvent.SafeCode);
        Assert.Equal(summary.PayloadId, startEvent.PayloadId);
    }

    [Fact]
    public async Task Continue_cannot_override_a_true_provider_context_limit()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("never reached"));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var coordinator = Coordinator(model, store, limitTokens: 1);

        var firstBlock = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);
        Assert.Equal("context_limit_reached", firstBlock.ErrorCode);

        await coordinator.ContinueAsync(RunId(), CancellationToken.None);
        var secondBlock = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Empty(model.Requests);
        Assert.Equal("context_limit_reached", secondBlock.ErrorCode);
    }

    [Fact]
    public async Task Without_a_provider_limit_normal_evaluation_proceeds()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("done"));
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var coordinator = Coordinator(model, store, limitTokens: null);

        var result = await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.Null(result.ErrorCode);
        Assert.Single(model.Requests);
        Assert.DoesNotContain(
            store.Commits.SelectMany(commit => commit.Events),
            item => item.SafeCode == "context_limit_reached");
    }

    // ------------------------------------------------------------------
    // Fixtures.
    // ------------------------------------------------------------------

    private static AgentRunCoordinator Coordinator(
        FakeModelStepRunner model,
        InMemoryHarnessEventStore store,
        long? limitTokens) =>
        new(
            model,
            new FakeToolGateway(),
            store,
            new AgentContextAssembler(new SingleLargeContextSource()),
            new SequenceIdGenerator(),
            new IncrementingTimeProvider(Epoch),
            new ToolCallScheduler(),
            new TransientFrameBuffer(32),
            null,
            new StaticModelContextLimitProvider(limitTokens));

    private static async Task<ModelContextSnapshot> SnapshotAsync(params string[] texts)
    {
        var entries = texts
            .Select((text, index) => TextEntry($"src-{index}", index + 1, text))
            .ToArray();
        return await new AgentContextAssembler(new FixedSource(entries))
            .AssembleAsync(RunId(), CancellationToken.None);
    }

    private static ModelContextEntry TextEntry(string sourceId, long sequence, string text) =>
        ModelContextEntry.Text(sourceId, sequence, ModelContextRole.User, text);

    private static AssistantStep FinalStep(string text) =>
        new(text, Array.Empty<ModelToolCall>(), AssistantFinishReason.Stop, 16);

    private static AgentRun Run() =>
        new(
            RunId(),
            new SessionId("22222222222222222222222222222222"),
            new WorkspaceId("ws_22222222222222222222222222222222"),
            new HarnessRunStatus(HarnessRunState.Running),
            0,
            0,
            1,
            0,
            0,
            AgentPermissionPreset.Standard,
            1,
            new string('a', 64),
            Epoch,
            Epoch);

    private static RunId RunId() => new("run_22222222222222222222222222222222");

    private sealed class FixedSource(ModelContextEntry[] entries) : IModelContextSource
    {
        public Task<ModelContextSourceSnapshot> LoadAsync(
            RunId runId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ModelContextSourceSnapshot(
                Array.Empty<ModelContextEntry>(),
                entries));
    }

    private sealed class SingleLargeContextSource : IModelContextSource
    {
        public Task<ModelContextSourceSnapshot> LoadAsync(
            RunId runId,
            CancellationToken cancellationToken)
        {
            var entry = TextEntry("large-1", 1, new string('z', 4096));
            return Task.FromResult(new ModelContextSourceSnapshot(
                Array.Empty<ModelContextEntry>(),
                new[] { entry }));
        }
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
        public TrustRequestId NewTrustRequestId() => new(Value("trust_"));
        public PolicyRuleId NewPolicyRuleId() => new(Value("rule_"));
        public EventId NewEventId() => new(Value("evt_"));
        public PayloadId NewPayloadId() => new(Value("payload_"));

        private string Value(string prefix) =>
            prefix + Interlocked.Increment(ref _next).ToString("x32");
    }
}
