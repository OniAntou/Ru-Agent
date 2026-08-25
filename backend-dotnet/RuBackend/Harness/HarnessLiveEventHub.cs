using System.Text.Json;
using System.Threading.Channels;
using RuAgent.Harness.Core;
using RuAgent.Protocol.Harness;

namespace RuBackend.Harness;

public sealed record TransientFrame(long LocalSequence, RunId RunId, string Kind, string Data);

public sealed class HarnessLiveEventHub
{
    private const int Capacity = 64;
    private readonly Channel<TransientFrame> _channel;
    private long _localSequence;
    private long _droppedCount;

    public HarnessLiveEventHub()
    {
        var options = new BoundedChannelOptions(Capacity)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        };
        _channel = Channel.CreateBounded<TransientFrame>(options, _ => Interlocked.Increment(ref _droppedCount));
    }

    /// <summary>
    /// Maps one Broker v2 <c>tool.output.delta</c> onto the Core-neutral chunk shape and a
    /// connection-local transient frame. Transient frames never advance durable cursors
    /// and are never persisted; bounded loss only increments a safe dropped counter.
    /// </summary>
    public bool TryPublishDelta(BrokerToolOutputDeltaWire delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        BrokerToolOutputDeltaWire.Validate(delta);
        _ = new ToolOutputChunk(
            new ExecutionId(delta.ExecutionId),
            delta.FrameSequence,
            delta.Stream == "stderr" ? ToolOutputStream.Stderr : ToolOutputStream.Stdout,
            delta.Text);

        var data = JsonSerializer.Serialize(new
        {
            execution_id = delta.ExecutionId,
            frame_sequence = delta.FrameSequence,
            stream = delta.Stream,
            text = delta.Text,
            redacted = delta.Redacted,
        });
        return TryPublish(new RunId(delta.RunId), "tool.output.delta", data);
    }

    public bool TryPublish(RunId runId, string kind, string data)
    {
        var seq = Interlocked.Increment(ref _localSequence);
        var frame = new TransientFrame(seq, runId, kind, data);
        if (_channel.Writer.TryWrite(frame))
        {
            return true;
        }

        Interlocked.Increment(ref _droppedCount);
        // With DropOldest, TryWrite should succeed after dropping; but if still fails count as dropped
        return false;
    }

    public long DroppedCount => Volatile.Read(ref _droppedCount);

    public ChannelReader<TransientFrame> Reader => _channel.Reader;

    // Durable events are not published via this hub; they are persisted to SQLite. This hub only manages transient frames.
    // Bounded loss does not block persistence: callers that persist durable receipts do not await hub.
}
