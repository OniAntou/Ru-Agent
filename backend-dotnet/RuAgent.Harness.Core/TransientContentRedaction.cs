namespace RuAgent.Harness.Core;

public interface ITransientContentRedactor
{
    ITransientContentRedactionSession CreateSession();
}

public interface ITransientContentRedactionSession
{
    string Append(string content);

    string Complete();
}

internal sealed class PassthroughTransientContentRedactor : ITransientContentRedactor
{
    internal static PassthroughTransientContentRedactor Instance { get; } = new();

    private PassthroughTransientContentRedactor()
    {
    }

    public ITransientContentRedactionSession CreateSession() => new Session();

    private sealed class Session : ITransientContentRedactionSession
    {
        public string Append(string content) =>
            content ?? throw new ArgumentNullException(nameof(content));

        public string Complete() => string.Empty;
    }
}

internal sealed class TransientContentStream
{
    private const int MaximumFrameCharacters = 64 * 1024;

    private readonly object _gate = new();
    private readonly TransientFrameBuffer _frames;
    private readonly ITransientContentRedactionSession _redaction;
    private readonly RunId _runId;
    private readonly TransientFrameKind _kind;
    private readonly ModelStepId? _modelStepId;
    private readonly ExecutionId? _executionId;
    private long _sequence;
    private bool _completed;

    internal TransientContentStream(
        TransientFrameBuffer frames,
        ITransientContentRedactor redactor,
        RunId runId,
        TransientFrameKind kind,
        ModelStepId? modelStepId = null,
        ExecutionId? executionId = null)
    {
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        ArgumentNullException.ThrowIfNull(redactor);
        _redaction = redactor.CreateSession()
            ?? throw new InvalidOperationException("transient_redactor_returned_null");
        _runId = runId;
        _kind = kind;
        _modelStepId = modelStepId;
        _executionId = executionId;
    }

    internal void Append(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        lock (_gate)
        {
            if (_completed)
            {
                throw new InvalidOperationException("transient_redaction_completed");
            }

            Publish(_redaction.Append(content));
        }
    }

    internal void Complete()
    {
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            Publish(_redaction.Complete());
        }
    }

    private void Publish(string? content)
    {
        if (content is null)
        {
            throw new InvalidOperationException("transient_redactor_returned_null");
        }

        for (var offset = 0; offset < content.Length; offset += MaximumFrameCharacters)
        {
            var length = Math.Min(MaximumFrameCharacters, content.Length - offset);
            _frames.TryPublish(new HarnessTransientFrame(
                _runId,
                _kind,
                checked(++_sequence),
                content.Substring(offset, length),
                _modelStepId,
                _executionId));
        }
    }
}
