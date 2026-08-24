using System.Threading.Channels;
using RuAgent.Harness.Core;

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
        _channel = Channel.CreateBounded<TransientFrame>(options);
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
