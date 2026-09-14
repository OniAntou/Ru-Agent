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
///
/// Agent activity rows (<c>agent.activity</c>, <c>agent.summary</c>) travel on a
/// dedicated channel: high-volume assistant/tool deltas share one bounded queue
/// with DropOldest, and a streaming burst longer than the backlog used to evict
/// the one or two activity frames a tool step publishes. Deltas are re-derivable
/// from durable state; a dropped activity row is gone forever.
/// </summary>
public sealed class HarnessLiveEventHub
{
    private const int Capacity = 64;
    private const int ActivityCapacity = 64;
    private readonly ConcurrentDictionary<Guid, Subscription> _subscriptions = new();
    private long _localSequence;
    private long _droppedCount;
    private long _activityDroppedCount;

    private sealed class Subscription
    {
        public Subscription(RunId runId, Channel<TransientFrame> channel, Channel<TransientFrame> activityChannel)
        {
            RunId = runId;
            Channel = channel;
            ActivityChannel = activityChannel;
        }

        public RunId RunId { get; }

        public Channel<TransientFrame> Channel { get; set; }

        public Channel<TransientFrame> ActivityChannel { get; set; }

        public long Dropped;

        public long ActivityDropped;
    }

    public HubLease Subscribe(RunId runId)
    {
        var id = Guid.NewGuid();
        var subscription = new Subscription(runId, null!, null!);
        var options = new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            // Runtime coordinators publish concurrently for independent runs.
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        };
        subscription.Channel = Channel.CreateBounded<TransientFrame>(options, _ =>
        {
            Interlocked.Increment(ref subscription.Dropped);
            Interlocked.Increment(ref _droppedCount);
        });
        var activityOptions = new BoundedChannelOptions(ActivityCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        };
        subscription.ActivityChannel = Channel.CreateBounded<TransientFrame>(activityOptions, _ =>
        {
            Interlocked.Increment(ref subscription.ActivityDropped);
            Interlocked.Increment(ref _activityDroppedCount);
            Interlocked.Increment(ref _droppedCount);
        });
        _subscriptions[id] = subscription;
        return new HubLease(this, id, subscription.Channel.Reader, subscription.ActivityChannel.Reader);
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
        var activity = IsActivityKind(kind);
        foreach (var pair in _subscriptions)
        {
            if (pair.Value.RunId.Value != runId.Value)
            {
                continue;
            }

            var channel = activity ? pair.Value.ActivityChannel : pair.Value.Channel;
            if (!channel.Writer.TryWrite(frame))
            {
                var subscription = pair.Value;
                if (activity)
                {
                    Interlocked.Increment(ref subscription.ActivityDropped);
                    Interlocked.Increment(ref _activityDroppedCount);
                }
                else
                {
                    Interlocked.Increment(ref subscription.Dropped);
                }

                Interlocked.Increment(ref _droppedCount);
                delivered = false;
            }
        }

        return delivered;
    }

    private static bool IsActivityKind(string kind) =>
        string.Equals(kind, "agent.activity", StringComparison.Ordinal)
        || string.Equals(kind, "agent.summary", StringComparison.Ordinal);

    public long DroppedCount => Volatile.Read(ref _droppedCount);

    public long ActivityDroppedCount => Volatile.Read(ref _activityDroppedCount);

    internal int SubscriberCount => _subscriptions.Count;

    public readonly struct HubLease : IAsyncDisposable
    {
        private readonly HarnessLiveEventHub _hub;
        private readonly Guid _id;
        public ChannelReader<TransientFrame> Reader { get; }
        public ChannelReader<TransientFrame> ActivityReader { get; }

        internal HubLease(HarnessLiveEventHub hub, Guid id, ChannelReader<TransientFrame> reader, ChannelReader<TransientFrame> activityReader)
        {
            _hub = hub;
            _id = id;
            Reader = reader;
            ActivityReader = activityReader;
        }

        public ValueTask DisposeAsync()
        {
            if (_hub._subscriptions.TryRemove(_id, out var subscription))
            {
                subscription.Channel.Writer.TryComplete();
                subscription.ActivityChannel.Writer.TryComplete();
            }

            return ValueTask.CompletedTask;
        }
    }
}
