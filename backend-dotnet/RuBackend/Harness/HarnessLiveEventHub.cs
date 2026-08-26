using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using RuAgent.Harness.Core;
using RuAgent.Protocol.Harness;

namespace RuBackend.Harness;

public sealed record TransientFrame(long LocalSequence, RunId RunId, string Kind, string Data);

/// <summary>
/// Fans transient frames out to per-connection subscribers so one client can no longer
/// consume or drop another client's frames. Every subscriber has a private bounded
/// channel; loss on any subscriber increments that subscription's dropped counter.
/// </summary>
public sealed class HarnessLiveEventHub
{
    private const int Capacity = 64;
    private readonly ConcurrentDictionary<Guid, Subscription> _subscriptions = new();
    private long _localSequence;
    private long _droppedCount;

    private sealed class Subscription
    {
        public Subscription(RunId runId, Channel<TransientFrame> channel)
        {
            RunId = runId;
            Channel = channel;
        }

        public RunId RunId { get; }

        public Channel<TransientFrame> Channel { get; set; }

        public long Dropped;
    }

    public HubLease Subscribe(RunId runId)
    {
        var id = Guid.NewGuid();
        var subscription = new Subscription(runId, null!);
        var options = new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        };
        subscription.Channel = Channel.CreateBounded<TransientFrame>(options, _ =>
        {
            Interlocked.Increment(ref subscription.Dropped);
            Interlocked.Increment(ref _droppedCount);
        });
        _subscriptions[id] = subscription;
        return new HubLease(this, id, subscription.Channel.Reader);
    }

    /// <summary>
    /// Maps one Broker v2 <c>tool.output.delta</c> onto the Core-neutral chunk shape and a
    /// connection-local transient frame. Transient frames never advance durable cursors
    /// and are never persisted.
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
        if (_subscriptions.IsEmpty)
        {
            // No live subscribers: nothing to deliver, nothing to count as lost for a client.
            return true;
        }

        var seq = Interlocked.Increment(ref _localSequence);
        var frame = new TransientFrame(seq, runId, kind, data);
        var delivered = true;
        foreach (var pair in _subscriptions)
        {
            if (pair.Value.RunId.Value != runId.Value)
            {
                continue;
            }

            if (!pair.Value.Channel.Writer.TryWrite(frame))
            {
                var subscription = pair.Value;
                Interlocked.Increment(ref subscription.Dropped);
                _subscriptions[pair.Key] = subscription;
                Interlocked.Increment(ref _droppedCount);
                delivered = false;
            }
        }

        return delivered;
    }

    public long DroppedCount => Volatile.Read(ref _droppedCount);

    internal int SubscriberCount => _subscriptions.Count;

    public readonly struct HubLease : IAsyncDisposable
    {
        private readonly HarnessLiveEventHub _hub;
        private readonly Guid _id;
        public ChannelReader<TransientFrame> Reader { get; }

        internal HubLease(HarnessLiveEventHub hub, Guid id, ChannelReader<TransientFrame> reader)
        {
            _hub = hub;
            _id = id;
            Reader = reader;
        }

        public ValueTask DisposeAsync()
        {
            _hub._subscriptions.TryRemove(_id, out _);
            return ValueTask.CompletedTask;
        }
    }
}
