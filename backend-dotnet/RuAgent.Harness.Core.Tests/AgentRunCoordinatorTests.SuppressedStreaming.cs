using System.Text;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Core.Tests.Fakes;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed partial class AgentRunCoordinatorTests
{
    [Theory]
    [InlineData("process_exit_nonzero")]
    [InlineData("executable_not_found")]
    [InlineData("edit_no_match")]
    public async Task Tool_failure_recovery_retracts_the_rejected_reply_before_the_next_answer(string code)
    {
        var context = new FixedContextSource(
            ModelContextEntry.Assistant("previous-step", 1, ToolStep()),
            ModelContextEntry.ToolResult("failed-tool", 2, new ToolExecutionReceipt(
                new ExecutionId("exec_11111111111111111111111111111111"),
                new ToolCallId("call_11111111111111111111111111111111"),
                ToolExecutionOutcome.Failed, code, null, false, providerCallId: "provider_call"), "{}"));
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("The command returned an error."), "The command ", "returned an error.");
        model.Enqueue(FinalStep("[BLOCKED] The command failed."), "[BLOCKED] The command failed.");
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var observed = new List<HarnessTransientFrame>();
        var activities = new RecordingActivitySink();
        var coordinator = Coordinator(model, new FakeToolGateway(), store,
            frames: new TransientFrameBuffer(32, observed.Add), contextSource: context, activitySink: activities);

        var advised = await coordinator.RunOneStepAsync(RunId(), default);
        Assert.Equal(HarnessRunState.Running, advised.Run.State);
        Assert.Empty(store.Commits.Last().ConversationOutbox);
        var note = Assert.Single(store.Commits.Last().Payloads,
            payload => payload.Kind == HarnessPayloadKind.ModelContextNote);
        context.Add(ModelContextEntry.Text("recovery-note", 3, ModelContextRole.System,
            Encoding.UTF8.GetString(note.Plaintext.Span)));

        var rejected = observed.Where(frame => frame.Kind == TransientFrameKind.AssistantDelta).ToArray();
        Assert.True(rejected.Last().IsReset, "Recovery left the rejected answer visible before retrying.");
        Assert.All(rejected, frame => Assert.Equal(rejected[0].ModelStepId, frame.ModelStepId));
        Assert.True(rejected.Last().Sequence > rejected[^2].Sequence);
        Assert.Single(activities.Activities, activity => activity.Kind == AgentActivityKind.Retry);

        var reported = await coordinator.RunOneStepAsync(RunId(), default);
        Assert.Equal(HarnessRunState.Failed, reported.Run.State);
        Assert.Equal(code, reported.Run.TerminalErrorCode);
        Assert.Single(store.Commits.Last().ConversationOutbox);

        var visible = new Dictionary<ModelStepId, string>();
        foreach (var frame in observed.Where(frame => frame.Kind == TransientFrameKind.AssistantDelta))
        {
            var id = frame.ModelStepId!.Value;
            visible[id] = frame.IsReset ? "" : visible.GetValueOrDefault(id, "") + frame.Content;
        }
        Assert.Equal("", visible[rejected[0].ModelStepId!.Value]);
        Assert.Equal("[BLOCKED] The command failed.", Assert.Single(visible.Values, value => value.Length > 0));
    }

    [Fact]
    public async Task Accepted_final_reply_keeps_its_streamed_text()
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(FinalStep("A valid answer."), "A valid answer.");
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var observed = new List<HarnessTransientFrame>();
        var coordinator = Coordinator(model, new FakeToolGateway(), store,
            frames: new TransientFrameBuffer(32, observed.Add));

        var result = await coordinator.RunOneStepAsync(RunId(), default);

        Assert.Equal(HarnessRunState.WaitingHistory, result.Run.State);
        Assert.False(Assert.Single(observed).IsReset);
        Assert.Equal("A valid answer.", observed[0].Content);
    }

    [Theory]
    [InlineData(AssistantFinishReason.Length, "Incomplete reply")]
    [InlineData(AssistantFinishReason.ContentFilter, "Filtered reply")]
    [InlineData(AssistantFinishReason.Error, "Rejected reply")]
    [InlineData(AssistantFinishReason.Stop, "   ")]
    public async Task Rejected_reply_retracts_even_the_tail_flushed_by_redaction(
        AssistantFinishReason finish, string finalText)
    {
        var model = new FakeModelStepRunner();
        model.Enqueue(new AssistantStep(finalText, [], finish, 0), "Buffered provisional reply");
        var store = new InMemoryHarnessEventStore();
        store.Seed(Run(new HarnessRunStatus(HarnessRunState.Running)));
        var frames = new List<HarnessTransientFrame>();
        var coordinator = Coordinator(model, new FakeToolGateway(), store,
            frames: new TransientFrameBuffer(32, frames.Add), redactor: new BufferedReplyRedactor());

        await coordinator.RunOneStepAsync(RunId(), default);

        Assert.Equal("Buffered provisional reply", frames[0].Content);
        Assert.True(frames[^1].IsReset);
        Assert.Empty(store.Commits.Last().ConversationOutbox);
    }

    private sealed class BufferedReplyRedactor : ITransientContentRedactor
    {
        public ITransientContentRedactionSession CreateSession() => new Session();
        private sealed class Session : ITransientContentRedactionSession
        {
            private readonly StringBuilder _buffer = new();
            public string Append(string text) { _buffer.Append(text); return ""; }
            public string Complete() => _buffer.ToString();
        }
    }
}
