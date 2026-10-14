using System.Text.Json;
using System.Threading.Channels;
using RuAgent.Harness.Core;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class HarnessOutputDeltaTests
{
    private const string RunId = "run_44444444444444444444444444444444";
    private const string ToolCallId = "call_44444444444444444444444444444444";
    private const string ExecutionId = "exec_44444444444444444444444444444444";

    [Fact]
    public async Task Broker_wire_delta_maps_to_core_chunk_and_transient_frame_without_persisting()
    {
        var payload = new System.Text.Json.Nodes.JsonObject
        {
            ["run_id"] = RunId,
            ["tool_call_id"] = ToolCallId,
            ["execution_id"] = ExecutionId,
            ["frame_sequence"] = 7,
            ["stream"] = "stdout",
            ["text"] = "compiling module A",
            ["redacted"] = false,
        };
        var envelope = new AgentEnvelope(
            AgentEnvelopeCodec.ProtocolVersionV2,
            "delta-map-1",
            AgentMessageTypes.ToolOutputDelta,
            "session-delta",
            null,
            1_000,
            payload);

        Assert.Equal(AgentMessageTypes.ToolOutputDelta, envelope.MessageType);

        using var document = JsonDocument.Parse(envelope.Payload.ToJsonString());
        var root = document.RootElement;
        var delta = new BrokerToolOutputDeltaWire(
            root.GetProperty("run_id").GetString()!,
            root.GetProperty("tool_call_id").GetString()!,
            root.GetProperty("execution_id").GetString()!,
            root.GetProperty("frame_sequence").GetInt64(),
            root.GetProperty("stream").GetString()!,
            root.GetProperty("text").GetString()!,
            root.GetProperty("redacted").GetBoolean());

        var hub = new HarnessLiveEventHub();
        await using var lease = hub.Subscribe(new RunId(RunId));
        Assert.True(hub.TryPublishDelta(delta));

        var frame = await lease.Reader.ReadAsync(CancellationToken.None);
        Assert.Equal("tool.output.delta", frame.Kind);
        Assert.Equal(new RunId(RunId), frame.RunId);
        Assert.Equal(1, frame.LocalSequence);
        Assert.Contains("compiling module A", frame.Data, StringComparison.Ordinal);
        Assert.DoesNotContain(ToolCallId, frame.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bounded_channel_drops_oldest_frames_and_counts_them_safely()
    {
        var hub = new HarnessLiveEventHub();
        await using var lease = hub.Subscribe(new RunId(RunId));
        for (var i = 1; i <= 200; i++)
        {
            var delta = new BrokerToolOutputDeltaWire(
                RunId,
                ToolCallId,
                ExecutionId,
                i,
                "stdout",
                $"chunk-{i}",
                Redacted: false);
            hub.TryPublishDelta(delta);
        }

        var received = 0;
        await foreach (var _ in lease.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            received++;
            if (received >= 64)
            {
                break;
            }
        }

        Assert.Equal(64, received);
        Assert.True(hub.DroppedCount > 0);
    }

    [Fact]
    public async Task Usage_estimate_frame_maps_onto_the_activity_channel_with_its_measured_payload()
    {
        var hub = new HarnessLiveEventHub();
        await using var lease = hub.Subscribe(new RunId(RunId));
        var stepId = new ModelStepId("step_44444444444444444444444444444444");
        var frame = new HarnessTransientFrame(
            new RunId(RunId),
            TransientFrameKind.UsageEstimate,
            1,
            "{\"estimated_input_tokens\":4000,\"max_prompt_tokens\":200000,\"model_step_id\":\"step_44444444444444444444444444444444\"}",
            stepId);

        AgentRuntimeCoordinatorFactory.PublishLiveFrame(hub, frame);

        var published = await lease.ActivityReader.ReadAsync(CancellationToken.None);
        Assert.Equal("agent.usage_estimate", published.Kind);
        Assert.Equal(new RunId(RunId), published.RunId);
        Assert.Equal(frame.Content, published.Data);
        Assert.False(lease.Reader.TryRead(out _));
    }

    [Fact]
    public void Invalid_delta_frames_fail_closed_at_the_contract_boundary()
    {
        Assert.Throws<AgentProtocolException>(() => BrokerToolOutputDeltaWire.Validate(new BrokerToolOutputDeltaWire(
            RunId, ToolCallId, ExecutionId, FrameSequence: 0, "stdout", "text", Redacted: false)));
        Assert.Throws<AgentProtocolException>(() => BrokerToolOutputDeltaWire.Validate(new BrokerToolOutputDeltaWire(
            RunId, ToolCallId, ExecutionId, FrameSequence: 1, "stdin", "text", Redacted: false)));
        Assert.Throws<AgentProtocolException>(() => BrokerToolOutputDeltaWire.Validate(new BrokerToolOutputDeltaWire(
            string.Empty, ToolCallId, ExecutionId, FrameSequence: 1, "stdout", "text", Redacted: false)));
    }
}
