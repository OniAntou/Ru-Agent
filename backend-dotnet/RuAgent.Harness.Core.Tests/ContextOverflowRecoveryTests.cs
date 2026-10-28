using System.Text;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Core.Tests.Fakes;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed partial class ContextCompactionCoordinatorTests
{
    [Fact]
    public async Task Provider_context_rejection_compacts_and_retries_a_new_budgeted_step_without_tools()
    {
        var store = new InMemoryHarnessEventStore();
        var seed = OverflowRun(); store.Seed(seed);
        var source = new OverflowContextSource(store, true);
        var model = new OverflowModel(1);
        var frames = new List<HarnessTransientFrame>();
        var gateway = new FakeToolGateway();
        var coordinator = new AgentRunCoordinator(model, gateway, store, new AgentContextAssembler(source),
            new SequenceIdGenerator(), new IncrementingTimeProvider(Epoch), new ToolCallScheduler(),
            new TransientFrameBuffer(32, frames.Add), contextLimit: new StaticModelContextLimitProvider(10000),
            promptContextProvider: new FakeSystemPromptContextProvider());
        var recovery = await coordinator.RunOneStepAsync(seed.RunId, default);
        Assert.Equal(HarnessRunState.Running, recovery.Run.State);
        Assert.Null(recovery.ErrorCode);
        Assert.Contains(store.Commits, c => c.ContextCompaction is not null);
        Assert.Contains(frames, f => f.IsReset);
        var done = await coordinator.RunOneStepAsync(seed.RunId, default);
        Assert.Equal(2, model.Calls);
        Assert.Equal(2, done.Run.ModelStepsInWindow);
        Assert.Equal(seed.PolicySnapshotSha256, done.Run.PolicySnapshotSha256);
        Assert.Contains(model.Requests[1].Context.Entries, e => e.Kind == ModelContextEntryKind.Summary);
        Assert.DoesNotContain(store.Commits.SelectMany(c => c.Events), e => e.Kind == HarnessEventKind.ToolExecutionRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provider_context_recovery_stops_for_irreducible_or_repeated_rejection(bool hasHistory)
    {
        var store = new InMemoryHarnessEventStore();
        var seed = OverflowRun(); store.Seed(seed);
        var model = new OverflowModel(int.MaxValue);
        var coordinator = new AgentRunCoordinator(model, new FakeToolGateway(), store,
            new AgentContextAssembler(new OverflowContextSource(store, hasHistory)),
            new SequenceIdGenerator(), new IncrementingTimeProvider(Epoch), new ToolCallScheduler(), new TransientFrameBuffer(32),
            contextLimit: new StaticModelContextLimitProvider(10000), promptContextProvider: new FakeSystemPromptContextProvider());
        AgentRunStepResult result;
        do { result = await coordinator.RunOneStepAsync(seed.RunId, default); }
        while (result.Run.State == HarnessRunState.Running && model.Calls < 5);
        Assert.Equal(HarnessRunState.Failed, result.Run.State);
        Assert.Equal("context_window_exceeded", result.ErrorCode);
        Assert.InRange(model.Calls, 1, 3);
        Assert.Equal(model.Calls, result.Run.ModelStepsInWindow);
    }

    private static AgentRun OverflowRun(int lastSequence = 3)
    {
        var run = Run();
        return new(run.RunId, run.SessionId, run.WorkspaceId, run.Status, 0, lastSequence, run.BudgetWindow,
            0, 0, run.Preset, run.PolicySnapshotVersion, run.PolicySnapshotSha256, run.CreatedAt, run.UpdatedAt);
    }

    private sealed class OverflowModel(int failures) : IModelStepRunner
    {
        public int Calls { get; private set; }
        public List<ModelStepRequest> Requests { get; } = [];
        public async Task<ModelStepResult> RunStepAsync(ModelStepRequest request,
            Func<AssistantDelta, ValueTask> publish, CancellationToken ct)
        {
            Requests.Add(request); Calls++;
            await publish(new AssistantDelta(request.ModelStepId, 1, "partial rejected answer"));
            if (Calls <= failures) throw new OverflowError();
            return new(new AssistantStep("done", [], AssistantFinishReason.Stop, 1),
                SystemPromptComposer.Compose(request.PromptContext, nativeToolCalling: true).Receipt);
        }
    }

    private sealed class OverflowError : Exception, IStableStepError
    {
        public string StepErrorCode => "context_window_exceeded";
    }

    private sealed class OverflowContextSource : IModelContextSource
    {
        private readonly InMemoryHarnessEventStore _store;
        private readonly List<ModelContextEntry> _entries;
        private int _applied;
        public OverflowContextSource(InMemoryHarnessEventStore store, bool history, IReadOnlyList<ModelContextEntry>? initial = null)
        {
            _store = store;
            _entries = initial?.ToList() ?? (history
                ? [ModelContextEntry.Assistant("old-a", 1, new AssistantStep(new string('a', 6000), [], AssistantFinishReason.Stop, 1)),
                   ModelContextEntry.Assistant("old-b", 2, new AssistantStep(new string('b', 6000), [], AssistantFinishReason.Stop, 1)),
                   ModelContextEntry.Text("current", 3, ModelContextRole.User, "continue task")]
                : [ModelContextEntry.Text("current", 3, ModelContextRole.User, "continue task")]);
        }

        public Task<ModelContextSourceSnapshot> LoadAsync(RunId runId, CancellationToken ct)
        {
            foreach (var commit in _store.Commits.Skip(_applied))
            {
                if (commit.ContextCompaction is { } compact)
                {
                    bool Covered(ModelContextEntry e) => compact.CoveredRanges.Any(r => r.SourceId == e.SourceId
                        && e.Sequence >= r.StartSequence && e.Sequence <= r.EndSequence);
                    var first = _entries.First(Covered);
                    var index = _entries.IndexOf(first);
                    _entries.RemoveAll(e => Covered(e));
                    var text = Encoding.UTF8.GetString(commit.Payloads.Single(p => p.PayloadId == compact.SummaryPayloadId).Plaintext.Span);
                    _entries.Insert(index, ModelContextEntry.Summary(compact.CompactionId, first.Sequence, text, compact.CoveredRanges));
                }
                foreach (var payload in commit.Payloads.Where(p => p.Kind == HarnessPayloadKind.ModelContextNote))
                {
                    var index = commit.Events.ToList().FindIndex(e => e.PayloadId == payload.PayloadId);
                    _entries.Add(ModelContextEntry.Text("event:" + commit.Events[index].EventId.Value,
                        commit.NextRun.LastSequence - commit.Events.Count + index + 1, ModelContextRole.System,
                        Encoding.UTF8.GetString(payload.Plaintext.Span)));
                }
            }
            _applied = _store.Commits.Count;
            return Task.FromResult(new ModelContextSourceSnapshot([], _entries.ToArray()));
        }
    }
}
