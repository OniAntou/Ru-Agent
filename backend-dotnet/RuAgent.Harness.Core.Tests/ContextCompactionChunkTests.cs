using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Core.Tests.Fakes;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed partial class ContextCompactionCoordinatorTests
{
    [Fact]
    public async Task Checkpoint_context_rejection_reduces_the_source_span_with_a_finite_call_budget()
    {
        var entries = Enumerable.Range(0, 16).Select(i => ModelContextEntry.Assistant("old-" + i, i + 1,
            new AssistantStep(new string('x', 2000), [], AssistantFinishReason.Stop, 1))).ToList();
        entries.Add(ModelContextEntry.Text("current", 17, ModelContextRole.User, "Continue"));
        var store = new InMemoryHarnessEventStore();
        var seed = OverflowRun(17); store.Seed(seed);
        var model = new ChunkedCheckpointModel { CheckpointSizeLimit = 1000, InputFailureCode = "checkpoint_context_overflow" };
        var coordinator = new AgentRunCoordinator(model, new FakeToolGateway(), store,
            new AgentContextAssembler(new OverflowContextSource(store, true, entries)),
            new SequenceIdGenerator(), new IncrementingTimeProvider(Epoch), new ToolCallScheduler(), new TransientFrameBuffer(32),
            contextLimit: new StaticModelContextLimitProvider(4000), promptContextProvider: new FakeSystemPromptContextProvider());
        var result = await coordinator.CompactAsync(seed.RunId, ContextCompactionStrategy.Normal, ContextCompactionReason.Manual, default);
        Assert.Equal(ContextCompactionOutcome.Committed, result.Outcome);
        Assert.InRange(model.CheckpointSizes.Count, 2, 4);
        Assert.True(model.CheckpointSizes[0] > model.CheckpointSizes[^1]);
        Assert.Equal(model.CheckpointSizes.Count, result.Run!.ModelStepsInWindow);
    }

    [Fact]
    public async Task Checkpoints_do_not_pay_for_task_tool_schemas_or_duplicate_tool_references()
    {
        var store = new InMemoryHarnessEventStore(); store.Seed(Run());
        var model = new ChunkedCheckpointModel();
        var schema = "{}";
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(schema))).ToLowerInvariant();
        var prompt = new SystemPromptContext(SystemPromptContext.PromptContextSchemaVersion, SystemPromptContext.RequiredAgentProfile,
            "standard", SystemPromptContext.Empty.Environment, [],
            [new PromptCapability("read", "1.0.0", "Read a file.", "workspace_read", schema, digest, null, "enabled")],
            new PromptRuntimeContext([new PromptReference("tool_result", "old result", "already in selected history")]));
        var coordinator = new AgentRunCoordinator(model, new FakeToolGateway(), store,
            new AgentContextAssembler(new ReloadingSource(Snapshot(
                ModelContextEntry.Assistant("old", 1, new AssistantStep(new string('x', 20000), [], AssistantFinishReason.Stop, 1)),
                ModelContextEntry.Text("current", 2, ModelContextRole.User, "continue")), Snapshot())),
            new SequenceIdGenerator(), new IncrementingTimeProvider(Epoch), new ToolCallScheduler(), new TransientFrameBuffer(32),
            contextLimit: new StaticModelContextLimitProvider(4000), promptContextProvider: new FakeSystemPromptContextProvider((_, _) => prompt));
        var result = await coordinator.CompactAsync(RunId(), ContextCompactionStrategy.Normal, ContextCompactionReason.Manual, default);
        Assert.Equal(ContextCompactionOutcome.Committed, result.Outcome);
        Assert.Empty(Assert.Single(model.Prompts).Capabilities);
        Assert.DoesNotContain(model.Prompts[0].RuntimeContext.References, r => r.Kind == "tool_result");
    }

    [Fact]
    public async Task Oversized_history_compacts_in_dispatchable_chunks_before_the_task_step()
    {
        var entries = Enumerable.Range(0, 64).Select(i => ModelContextEntry.Assistant("old-" + i, i + 1,
            new AssistantStep(new string('x', 2000), [], AssistantFinishReason.Stop, 1))).ToList();
        entries.Add(ModelContextEntry.Text("current", 65, ModelContextRole.User, "Continue the task"));
        var store = new InMemoryHarnessEventStore();
        var seed = OverflowRun(65); store.Seed(seed);
        var model = new ChunkedCheckpointModel();
        var coordinator = new AgentRunCoordinator(model, new FakeToolGateway(), store,
            new AgentContextAssembler(new OverflowContextSource(store, true, entries)),
            new SequenceIdGenerator(), new IncrementingTimeProvider(Epoch), new ToolCallScheduler(), new TransientFrameBuffer(32),
            contextLimit: new StaticModelContextLimitProvider(10000), promptContextProvider: new FakeSystemPromptContextProvider());
        var result = await coordinator.RunOneStepAsync(seed.RunId, default);
        Assert.Null(result.ErrorCode);
        Assert.InRange(model.CheckpointSizes.Count, 2, 16);
        Assert.All(model.CheckpointSizes, size => Assert.InRange(size, 1, 10000));
        Assert.Equal(1, model.TaskCalls);
        Assert.Equal(model.CheckpointSizes.Count + 1, result.Run.ModelStepsInWindow);
    }

    private sealed class ChunkedCheckpointModel : IModelStepRunner, IContextCheckpointSummarizer
    {
        public List<long> CheckpointSizes { get; } = [];
        public List<SystemPromptContext> Prompts { get; } = [];
        public int CheckpointSizeLimit { get; init; } = 10000;
        public string InputFailureCode { get; init; } = "checkpoint_input_too_large";
        public int TaskCalls { get; private set; }
        public Task<ContextCheckpointResult> SummarizeAsync(ModelStepRequest request, CancellationToken ct)
        {
            var size = ModelContextSizeEstimator.EstimateTokens(request.Context);
            CheckpointSizes.Add(size);
            Prompts.Add(request.PromptContext);
            if (size > CheckpointSizeLimit) throw new ContextCheckpointException(InputFailureCode);
            var text = string.Join("\n", ContextCheckpoint.Sections.Select(section => section + "\n- preserve the parser API"));
            return Task.FromResult(new ContextCheckpointResult(text, null));
        }

        public Task<ModelStepResult> RunStepAsync(ModelStepRequest request, Func<AssistantDelta, ValueTask> delta, CancellationToken ct)
        {
            TaskCalls++;
            return Task.FromResult(new ModelStepResult(new AssistantStep("done", [], AssistantFinishReason.Stop, 1),
                SystemPromptComposer.Compose(request.PromptContext, nativeToolCalling: true).Receipt));
        }
    }
}
