using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Core.Tests.Fakes;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

/// <summary>
/// On a large-context model the token budget may never ask for compaction before the runner's
/// fixed history window fills, and the runner then drops the oldest history unsummarized. A run
/// therefore compacts when the prepared request reports that window three quarters full; that
/// compaction is optional, so a checkpoint the model fails to write must not stop the run.
/// </summary>
public sealed partial class ContextCompactionCoordinatorTests
{
    private const long WindowLimitTokens = 1_000_000;

    [Fact]
    public async Task A_full_history_window_compacts_even_when_the_token_budget_has_room()
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var model = new WindowModel(store);

        var result = await WindowCoordinator(model, store).RunOneStepAsync(RunId(), default);

        Assert.Null(result.ErrorCode);
        Assert.Equal(1, model.CheckpointCalls);
        Assert.Contains(store.Commits, commit => commit.ContextCompaction is not null);
        Assert.Equal(1, model.Steps);
    }

    [Theory]
    [InlineData("checkpoint_incomplete")]
    [InlineData("checkpoint_timeout")]
    [InlineData("checkpoint_provider_transient")]
    public async Task A_failed_window_checkpoint_leaves_the_run_working(string code)
    {
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run());
        var model = new WindowModel(store) { CheckpointError = code };

        var result = await WindowCoordinator(model, store).RunOneStepAsync(RunId(), default);

        Assert.Null(result.ErrorCode);
        Assert.NotEqual(HarnessRunState.Failed, result.Run.State);
        Assert.NotEqual(HarnessRunState.WaitingUser, result.Run.State);
        Assert.DoesNotContain(store.Commits, commit => commit.ContextCompaction is not null);
        Assert.DoesNotContain(store.Commits.SelectMany(commit => commit.Events),
            evt => evt.Kind is HarnessEventKind.ContinueRequested or HarnessEventKind.RunFailed);
        Assert.Equal(1, model.Steps);
    }

    private static AgentRunCoordinator WindowCoordinator(WindowModel model, InMemoryHarnessEventStore store) =>
        new(model, new FakeToolGateway(), store, new AgentContextAssembler(new ReloadingSource(
                Snapshot(ModelContextEntry.Text("history", 1, ModelContextRole.User, new string('a', 20000)),
                    ModelContextEntry.Text("current", 2, ModelContextRole.User, "current request")),
                Snapshot(ModelContextEntry.Text("current", 2, ModelContextRole.User, "current request")))),
            new SequenceIdGenerator(), new IncrementingTimeProvider(Epoch), new ToolCallScheduler(), new TransientFrameBuffer(32),
            contextLimit: new StaticModelContextLimitProvider(WindowLimitTokens),
            promptContextProvider: new FakeSystemPromptContextProvider());

    /// <summary>
    /// A prepared request whose token estimate is far below the limit while its history window
    /// is 80% full until the history is compacted away.
    /// </summary>
    private sealed class WindowModel(InMemoryHarnessEventStore store)
        : IModelStepRunner, IModelStepRequestPreparer, IContextCheckpointSummarizer
    {
        private readonly FakeModelStepRunner _steps = CreateSteps();

        public int CheckpointCalls { get; private set; }
        public int Steps { get; private set; }
        public string? CheckpointError { get; init; }

        public Task<IPreparedModelStep> PrepareAsync(ModelStepRequest request, CancellationToken cancellationToken)
        {
            var full = request.Context.Entries.Any(entry => entry.SourceId == "history");
            return Task.FromResult<IPreparedModelStep>(new Prepared(full ? 6_000 : 100, WindowLimitTokens, full ? 0.8 : 0.1));
        }

        public Task<ContextCheckpointResult> SummarizeAsync(ModelStepRequest request, CancellationToken ct)
        {
            Assert.Contains(store.Commits.SelectMany(commit => commit.Events), evt => evt.SafeCode == "checkpoint_started");
            CheckpointCalls++;
            var text = string.Join("\n", ContextCheckpoint.Sections.Select(section => section + "\n- keep reading files"));
            return Task.FromResult(new ContextCheckpointResult(text, new ModelUsage(100, 50, 150, null, null), CheckpointError));
        }

        public Task<ModelStepResult> RunStepAsync(
            ModelStepRequest request, Func<AssistantDelta, ValueTask> publishDelta, CancellationToken cancellationToken)
        {
            Steps++;
            return _steps.RunStepAsync(request, publishDelta, cancellationToken);
        }

        private static FakeModelStepRunner CreateSteps()
        {
            var runner = new FakeModelStepRunner();
            runner.Enqueue(new AssistantStep("done", Array.Empty<ModelToolCall>(), AssistantFinishReason.Stop, estimatedInputTokens: 16));
            return runner;
        }

        private sealed record Prepared(long EstimatedInputTokens, long MaxPromptTokens, double HistoryWindowFill) : IPreparedModelStep;
    }
}
