using System.Text;

namespace RuAgent.Harness.Core;

/// <summary>
/// Follows one model step's reasoning stream and publishes the running thinking row's
/// summary line as an absolute transient frame whenever that line changes.
/// </summary>
/// <remarks>
/// The content is redacted incrementally with the same session the assistant text uses,
/// so a secret split across provider deltas is held back before it can enter a line. The
/// accumulated tail is bounded: the paragraph heuristic only needs the newest paragraphs.
/// A reset clears the line for a superseded attempt; completion needs no frame, because the
/// row's own settled state hides the preview.
/// </remarks>
internal sealed class ReasoningPreviewStream
{
    private readonly object _gate = new();
    private readonly TransientFrameBuffer _frames;
    private readonly ITransientContentRedactionSession _redaction;
    private readonly RunId _runId;
    private readonly ModelStepId _modelStepId;
    private readonly string _activityId;
    private readonly StringBuilder _tail = new();
    private string _lastPublished = string.Empty;
    private long _sequence;

    internal ReasoningPreviewStream(
        TransientFrameBuffer frames,
        ITransientContentRedactor redactor,
        RunId runId,
        ModelStepId modelStepId,
        string activityId)
    {
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        ArgumentNullException.ThrowIfNull(redactor);
        _redaction = redactor.CreateSession()
            ?? throw new InvalidOperationException("transient_redactor_returned_null");
        if (!RuAgent.Protocol.Harness.AgentActivityWireValidator.IsValidActivityId(activityId))
        {
            throw new ArgumentException("Transient activity id is invalid.", nameof(activityId));
        }

        _runId = runId;
        _modelStepId = modelStepId;
        _activityId = activityId;
    }

    /// <summary>
    /// Appends one provider reasoning delta. A line is published only when a newline makes
    /// the paragraph heuristic's answer move; plain text growth cannot change it.
    /// </summary>
    internal void Append(string reasoningDelta)
    {
        ArgumentNullException.ThrowIfNull(reasoningDelta);
        if (reasoningDelta.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            var safe = _redaction.Append(reasoningDelta)
                ?? throw new InvalidOperationException("transient_redactor_returned_null");
            _tail.Append(safe);
            var overflow = _tail.Length - ReasoningPreview.MaximumTrackedCharacters;
            if (overflow > 0)
            {
                // Keep the newest tail; a split surrogate at the cut is dropped with it.
                if (char.IsLowSurrogate(_tail[overflow]))
                {
                    overflow++;
                }

                _tail.Remove(0, overflow);
            }

            if (!safe.Contains('\n') && !safe.Contains('\r'))
            {
                return;
            }

            var summary = ReasoningPreview.Summary(_tail.ToString());
            if (summary.Length == 0 || string.Equals(summary, _lastPublished, StringComparison.Ordinal))
            {
                return;
            }

            _lastPublished = summary;
            Publish(summary, isReset: false);
        }
    }

    /// <summary>Clears the row's line when a retried attempt supersedes the one that streamed it.</summary>
    internal void Reset()
    {
        lock (_gate)
        {
            _tail.Clear();
            _lastPublished = string.Empty;
            Publish(string.Empty, isReset: true);
        }
    }

    private void Publish(string content, bool isReset) =>
        _frames.TryPublish(new HarnessTransientFrame(
            _runId,
            TransientFrameKind.ReasoningPreview,
            checked(++_sequence),
            content,
            _modelStepId,
            isReset: isReset,
            activityId: _activityId));
}
