using System.Collections.Concurrent;

namespace RuAgent.Harness.Core;

internal sealed partial class AgentToolExecutionPipeline
{
    /// <summary>
    /// Times one scheduled tool call so the completion activity can report a bounded
    /// duration. Timing is observational only: a clock going backwards, or an elapsed
    /// value outside the wire range, simply leaves the duration unreported.
    /// </summary>
    private async Task<ToolExecutionReceipt> ExecuteTimedAsync(
        RunId runId,
        PreparedToolExecution execution,
        IReadOnlyDictionary<int, ToolProposal> proposals,
        ConcurrentDictionary<string, int> durations,
        CancellationToken cancellationToken)
    {
        var startedAt = _time.UtcNow;
        try
        {
            return await ExecuteWithRetryAsync(
                    runId,
                    execution,
                    proposals,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            var elapsed = _time.UtcNow - startedAt;
            var milliseconds = elapsed.TotalMilliseconds;
            if (milliseconds is >= 0 and <= 86_400_000)
            {
                durations[execution.ExecutionId.Value] = (int)milliseconds;
            }
        }
    }

    private async Task<ToolExecutionReceipt> ExecuteWithRetryAsync(
        RunId runId,
        PreparedToolExecution execution,
        IReadOnlyDictionary<int, ToolProposal> proposals,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 2;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            var outputStream = new TransientContentStream(
                _frames,
                _transientRedactor,
                runId,
                TransientFrameKind.ToolOutput,
                executionId: execution.ExecutionId);
            try
            {
                var receipt = await _gateway.ExecuteAsync(
                        execution,
                        chunk => PublishToolOutput(
                            execution.ExecutionId,
                            outputStream,
                            chunk),
                        cancellationToken)
                    .ConfigureAwait(false);
                outputStream.Complete();
                return receipt;
            }
            catch (ToolTransportException) when (attempt < maximumAttempts)
            {
                // One retry for transport blips. A non-read-only call only lets a
                // ToolTransportException escape the gateway when the gateway can
                // attest the request was never dispatched; post-dispatch mutation
                // failures are converted into an OutcomeUnknown receipt at the
                // broker gateway instead of thrown. Read-only calls may retry
                // even after dispatch because a read cannot double-apply. The
                // second failure still reports terminally below.
                //
                // The retry replaces this attempt's streamed output, so a reset
                // frame tells the client to drop what attempt 1 already
                // rendered before attempt 2's output arrives.
                outputStream.Reset();
                if (proposals.TryGetValue(execution.ModelOrder, out var retryProposal))
                {
                    AgentActivityProjector.PublishSafe(
                        _activitySink,
                        runId,
                        AgentActivityProjector.ProjectRetry(retryProposal, attempt + 1));
                }
            }
            catch (ToolTransportException)
            {
                // Terminal failure: flush the redaction tail so the streamed
                // output ends where the receipt says it ended instead of
                // silently dropping the held suffix.
                outputStream.Complete();
                return new ToolExecutionReceipt(
                    execution.ExecutionId,
                    execution.ToolCallId,
                    execution.AccessKind == ToolAccessKind.ReadOnly
                        ? ToolExecutionOutcome.Failed
                        : ToolExecutionOutcome.OutcomeUnknown,
                    "transport_failure",
                    null,
                    sideEffectsPossible: execution.AccessKind != ToolAccessKind.ReadOnly);
            }
        }

        throw new InvalidOperationException("unreachable_tool_retry_state");
    }

    private static ValueTask PublishToolOutput(
        ExecutionId expectedExecutionId,
        TransientContentStream stream,
        ToolOutputChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.ExecutionId != expectedExecutionId)
        {
            throw new InvalidOperationException("tool_output_identity_mismatch");
        }

        stream.Append(chunk.Content);
        return ValueTask.CompletedTask;
    }
}
