using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Core.Tests.Fakes;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

/// <summary>
/// The reasoning path through one model step: changed summary lines reach the transient
/// frame buffer with the thinking row's activity id, a retried attempt clears them, and a
/// step that streams no reasoning publishes nothing.
/// </summary>
public sealed partial class AgentRunCoordinatorTests
{
    [Fact]
    public async Task A_model_step_streams_changed_thinking_lines_as_preview_frames()
    {
        var frames = new List<HarnessTransientFrame>();
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("answer"), ["answer"], ["First line\n", "Second line\n"]);
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, new FakeToolGateway(), store,
            new TransientFrameBuffer(64, frames.Add));

        await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        var previews = frames.Where(frame => frame.Kind == TransientFrameKind.ReasoningPreview).ToArray();
        var preview = Assert.Single(previews);
        Assert.Equal("First line", preview.Content);
        Assert.False(preview.IsReset);
        Assert.NotNull(preview.ActivityId);
        Assert.StartsWith("act_", preview.ActivityId, StringComparison.Ordinal);
        Assert.Equal(preview.ActivityId, previews[0].ActivityId);
    }

    [Fact]
    public async Task A_new_reasoning_paragraph_publishes_the_next_line()
    {
        var frames = new List<HarnessTransientFrame>();
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("answer"), ["answer"], ["First line\n", "\nNext paragraph\n"]);
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, new FakeToolGateway(), store,
            new TransientFrameBuffer(64, frames.Add));

        await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        var previews = frames.Where(frame => frame.Kind == TransientFrameKind.ReasoningPreview).ToArray();
        Assert.Equal(["First line", "Next paragraph"], previews.Select(frame => frame.Content));
    }

    [Fact]
    public async Task A_retried_attempt_clears_the_line_before_the_retry_streams()
    {
        var frames = new List<HarnessTransientFrame>();
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(new ReasoningResetRunner(), new FakeToolGateway(), store,
            new TransientFrameBuffer(64, frames.Add));

        await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        var previews = frames.Where(frame => frame.Kind == TransientFrameKind.ReasoningPreview).ToArray();
        Assert.True(previews.Length >= 3);
        Assert.Equal("Old attempt", previews[0].Content);
        Assert.True(previews[1].IsReset);
        Assert.Equal("Retry line", previews[^1].Content);
        Assert.All(previews, frame => Assert.Equal(previews[0].ActivityId, frame.ActivityId));
    }

    [Fact]
    public async Task A_step_without_reasoning_publishes_no_preview()
    {
        var frames = new List<HarnessTransientFrame>();
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("answer"), ["answer"]);
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var coordinator = Coordinator(model, new FakeToolGateway(), store,
            new TransientFrameBuffer(64, frames.Add));

        await coordinator.RunOneStepAsync(RunId(), CancellationToken.None);

        Assert.DoesNotContain(frames, frame => frame.Kind == TransientFrameKind.ReasoningPreview);
    }

    /// <summary>Streams a rejected first attempt's reasoning, then a retried attempt's reasoning.</summary>
    private sealed class ReasoningResetRunner : IModelStepRunner
    {
        public Task<ModelStepResult> RunStepAsync(
            ModelStepRequest request,
            Func<AssistantDelta, ValueTask> publishDelta,
            CancellationToken cancellationToken) =>
            RunAsync(request, publishDelta);

        private static async Task<ModelStepResult> RunAsync(
            ModelStepRequest request,
            Func<AssistantDelta, ValueTask> publishDelta)
        {
            await publishDelta(new AssistantDelta(request.ModelStepId, 1, "Old attempt\n",
                kind: AssistantDeltaKind.Reasoning));
            await publishDelta(new AssistantDelta(request.ModelStepId, 2, string.Empty,
                isReset: true, kind: AssistantDeltaKind.Reasoning));
            await publishDelta(new AssistantDelta(request.ModelStepId, 3, "Retry line\n",
                kind: AssistantDeltaKind.Reasoning));
            var step = new AssistantStep("answer", [], AssistantFinishReason.Stop, 16);
            return new ModelStepResult(step, SystemPromptComposer.Compose(request.PromptContext).Receipt);
        }
    }
}
