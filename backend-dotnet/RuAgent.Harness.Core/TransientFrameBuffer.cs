using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace RuAgent.Harness.Core;

public enum TransientFrameKind
{
    AssistantDelta,
    ToolOutput,
}

public sealed record HarnessTransientFrame
{
    public HarnessTransientFrame(
        RunId runId,
        TransientFrameKind kind,
        long sequence,
        string content,
        ModelStepId? modelStepId = null,
        ExecutionId? executionId = null)
    {
        _ = runId.Value;
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        if (string.IsNullOrEmpty(content) || content.Length > 64 * 1024)
        {
            throw new ArgumentException("Transient content must be bounded.", nameof(content));
        }

        if (modelStepId is { } stepId)
        {
            _ = stepId.Value;
        }

        if (executionId is { } toolExecutionId)
        {
            _ = toolExecutionId.Value;
        }

        RunId = runId;
        Kind = kind;
        Sequence = sequence;
        Content = content;
        ModelStepId = modelStepId;
        ExecutionId = executionId;
    }

    public RunId RunId { get; }
    public TransientFrameKind Kind { get; }
    public long Sequence { get; }
    public string Content { get; }
    public ModelStepId? ModelStepId { get; }
    public ExecutionId? ExecutionId { get; }
}

public sealed class TransientFrameBuffer
{
    private readonly Channel<HarnessTransientFrame> _channel;
    private long _droppedCount;

    public TransientFrameBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _channel = Channel.CreateBounded<HarnessTransientFrame>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
    }

    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    public bool TryPublish(HarnessTransientFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_channel.Writer.TryWrite(frame))
        {
            return true;
        }

        Interlocked.Increment(ref _droppedCount);
        return false;
    }

    public async IAsyncEnumerable<HarnessTransientFrame> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var frame in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return frame;
        }
    }
}
