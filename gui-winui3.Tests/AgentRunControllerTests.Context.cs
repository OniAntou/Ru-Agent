using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed partial class AgentRunControllerTests
{
    private const string ContextStepA = "step_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ContextStepB = "step_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    [Fact]
    public async Task Step_identity_routes_usage_and_rejects_delayed_estimates_through_the_real_controller()
    {
        var fake = new FakeClient { Run = MakeRun() };
        fake.PayloadContents["usage-a"] = """{"input_tokens":50000,"output_tokens":500}""";
        fake.PayloadContents["usage-b"] = """{"input_tokens":52000,"output_tokens":500}""";
        fake.Stream.Add(SafeEvent(1, "model.step_started") with { ModelStepId = ContextStepA });
        fake.Stream.Add(Estimate(ContextStepA, 80_000, 10));
        fake.Stream.Add(SafeEvent(2, "model.usage_recorded", "usage-a") with { ModelStepId = ContextStepA });
        fake.Stream.Add(Estimate(ContextStepA, 80_000, 11));
        fake.Stream.Add(SafeEvent(3, "model.step_started") with { ModelStepId = ContextStepB });
        fake.Stream.Add(Estimate(ContextStepB, 55_000, 1)); // New SSE connection restarts its transient cursor.
        fake.Stream.Add(SafeEvent(4, "model.usage_recorded", "usage-b") with { ModelStepId = ContextStepB });
        fake.Stream.Add(Estimate(ContextStepA, 80_000, 12));
        var controller = new AgentRunController(fake);
        var meter = new RetainedContextMeterPresenter();
        meter.ScopeToRun(fake.Run, "sess_1");
        var readings = new List<int>();
        controller.ContextStepStarted += (_, step) =>
            meter.ObserveModelStep(step.RunId, step.SessionId, step.Sequence, step.ModelStepId);
        controller.ContextEstimateReceived += (_, item) =>
        {
            meter.ObserveEstimate(item.RunId, item.SessionId, item.Estimate);
            readings.Add(meter.Measure([], null, 0).UsedTokens);
        };
        controller.UsageReceived += (_, item) =>
        {
            meter.ObserveUsage(item.RunId, item.SessionId, item.Sequence, "chat_completions", item.Usage, item.ModelStepId);
            readings.Add(meter.Measure([], null, 0).UsedTokens);
        };

        await controller.CreateAndRunAsync(new CreateAgentRunRequest("sess_1", "C:\\work", "context", "standard", null, "causal-context"));

        Assert.Equal(new[] { 80_000, 50_000, 50_000, 55_000, 52_000, 52_000 }, readings);
        Assert.True(meter.HasReportedUsage);
    }

    [Fact]
    public async Task Advisor_usage_never_reaches_the_reply_or_the_context_meter()
    {
        var fake = new FakeClient { Run = MakeRun() };
        fake.PayloadContents["usage-main"] = """{"input_tokens":50000,"output_tokens":500}""";
        fake.PayloadContents["usage-advisor"] = """{"input_tokens":90000,"output_tokens":900,"model":"advisor"}""";
        fake.Stream.Add(SafeEvent(1, "model.step_started") with { ModelStepId = ContextStepA });
        fake.Stream.Add(SafeEvent(2, "model.usage_recorded", "usage-main") with { ModelStepId = ContextStepA });
        fake.Stream.Add(SafeEvent(3, "model.usage_recorded", "usage-advisor") with
        {
            SafeSummary = SessionUsageReplayPolicy.AdvisorUsageSafeCode,
        });
        var controller = new AgentRunController(fake);
        var received = new List<AgentUsageReceivedEventArgs>();
        controller.UsageReceived += (_, item) => received.Add(item);

        await controller.CreateAndRunAsync(new CreateAgentRunRequest("sess_1", "C:\\work", "advisor", "standard", null, "causal-advisor"));

        var usage = Assert.Single(received);
        Assert.Equal(50000, usage.Usage.InputTokens);
    }

    [Fact]
    public async Task Compaction_checkpoint_usage_never_reaches_the_reply_or_the_context_meter()
    {
        var fake = new FakeClient { Run = MakeRun() };
        fake.PayloadContents["usage-main"] = """{"input_tokens":21000,"output_tokens":200}""";
        fake.PayloadContents["usage-checkpoint"] = """{"input_tokens":6000,"output_tokens":4096}""";
        fake.Stream.Add(SafeEvent(1, "model.step_started") with { ModelStepId = ContextStepA });
        fake.Stream.Add(SafeEvent(2, "model.usage_recorded", "usage-main") with { ModelStepId = ContextStepA });
        fake.Stream.Add(SafeEvent(3, "model.usage_recorded", "usage-checkpoint") with
        {
            SafeSummary = SessionUsageReplayPolicy.CheckpointUsageSafeCode,
        });
        var controller = new AgentRunController(fake);
        var received = new List<AgentUsageReceivedEventArgs>();
        controller.UsageReceived += (_, item) => received.Add(item);

        await controller.CreateAndRunAsync(new CreateAgentRunRequest("sess_1", "C:\\work", "checkpoint", "standard", null, "causal-checkpoint"));

        Assert.Equal(21000, Assert.Single(received).Usage.InputTokens);
    }

    private static AgentEventModel Estimate(string step, long tokens, long sequence)
    {
        var data = JsonSerializer.Serialize(new
        {
            kind = "agent.usage_estimate",
            data = JsonSerializer.Serialize(new { model_step_id = step, estimated_input_tokens = tokens, max_prompt_tokens = 120_000 }),
        });
        return AgentEventModel.FromSseFrame(new SseFrame(null, "transient", data, true, sequence));
    }
}
