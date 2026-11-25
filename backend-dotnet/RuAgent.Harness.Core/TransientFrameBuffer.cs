using System.Runtime.CompilerServices;
using System.Threading.Channels;
using RuAgent.Protocol.Harness;

namespace RuAgent.Harness.Core;

public enum TransientFrameKind
{
    AssistantDelta,
    ToolOutput,

    /// <summary>
    /// The measured request size for a model step about to run. Provider usage only
    /// arrives when a step ends, so this frame is what lets a client show the context
    /// meter moving while the model is still working.
    /// </summary>
    UsageEstimate,

    /// <summary>
    /// One absolute line of the running model step's reasoning summary. The content is
    /// the whole line, not a delta; a reset frame means "clear this row's line".
    /// </summary>
    ReasoningPreview,
}

public sealed record HarnessTransientFrame
{
    public HarnessTransientFrame(
        RunId runId,
        TransientFrameKind kind,
        long sequence,
        string content,
        ModelStepId? modelStepId = null,
        ExecutionId? executionId = null,
        bool isReset = false,
        string? activityId = null)
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

        if (isReset)
        {
            if (!string.IsNullOrEmpty(content))
            {
                throw new ArgumentException("A reset frame must not carry content.", nameof(content));
            }
        }
        else if (string.IsNullOrEmpty(content) || content.Length > 64 * 1024)
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

        if (activityId is not null && !AgentActivityWireValidator.IsValidActivityId(activityId))
        {
            throw new ArgumentException("Transient activity id is invalid.", nameof(activityId));
        }

        RunId = runId;
        Kind = kind;
        Sequence = sequence;
        Content = content;
        ModelStepId = modelStepId;
        ExecutionId = executionId;
        IsReset = isReset;
        ActivityId = activityId;
    }

    public RunId RunId { get; }
    public TransientFrameKind Kind { get; }
    public long Sequence { get; }
    public string Content { get; }
    public ModelStepId? ModelStepId { get; }
    public ExecutionId? ExecutionId { get; }

    /// <summary>The activity row this frame belongs to, when it carries one.</summary>
    public string? ActivityId { get; }

    /// <summary>
    /// True when this frame tells the client to discard the text it has already
    /// streamed for this stream (a superseded model-step attempt).
    /// </summary>
    public bool IsReset { get; }
}

public sealed class TransientFrameBuffer
{
    private readonly Channel<HarnessTransientFrame> _channel;
    private readonly Action<HarnessTransientFrame>? _observer;
    private long _droppedCount;

    public TransientFrameBuffer(int capacity, Action<HarnessTransientFrame>? observer = null)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _observer = observer;
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
        var written = _channel.Writer.TryWrite(frame);
        if (!written)
        {
            Interlocked.Increment(ref _droppedCount);
        }

        // The observer is deliberately independent from the bounded Core buffer:
        // a slow/non-consuming Core reader must not suppress live SSE delivery.
        // Observers are infrastructure side effects and cannot make a model step
        // fail just because a client disconnected.
        try
        {
            _observer?.Invoke(frame);
        }
        catch
        {
        }

        return written;
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
