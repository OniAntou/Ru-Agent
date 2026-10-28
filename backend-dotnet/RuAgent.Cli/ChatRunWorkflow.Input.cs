using System.Text.Json;
using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli;

internal static partial class ChatRunWorkflow
{
    internal static async Task<(HarnessSafeEventEnvelope? Pending, string? Error)> ReadPendingInputAsync(
        AgentSseClient client, GetAgentRunResponse snapshot, CancellationToken ct)
    {
        if (!string.Equals(snapshot.Status, "waiting_input", StringComparison.OrdinalIgnoreCase) || snapshot.LastSequence <= 0)
            return (null, "input_recovery_unavailable: run has no pending input cursor");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ApprovalReplayTimeout);
        HarnessSafeEventEnvelope? pending = null;
        var count = 0;
        try
        {
            await foreach (var frame in client.ReadEventsAsync(snapshot.RunId,
                Math.Max(0, snapshot.LastSequence - MaxApprovalReplayEvents), timeout.Token, live: false).ConfigureAwait(false))
            {
                if (frame.IsTransient || frame.DurableSequence is not { } sequence) continue;
                if (sequence > snapshot.LastSequence) break;
                if (++count > MaxApprovalReplayEvents || frame.Data.Length > MaxApprovalReplayFrameChars)
                    return (null, "input_recovery_incomplete: replay exceeded its bounded limit");
                var envelope = JsonSerializer.Deserialize<HarnessSafeEventEnvelope>(frame.Data);
                if (envelope is null || !HarnessSafeEventEnvelope.IsValidProtocolVersion(envelope.ProtocolVersion)
                    || envelope.Sequence != sequence || envelope.RunId != snapshot.RunId || envelope.SessionId != snapshot.SessionId)
                    return (null, "input_recovery_incomplete: durable event identity mismatch");
                if (envelope.EventType == "input.requested")
                    pending = string.IsNullOrWhiteSpace(envelope.PayloadRef) ? null : envelope;
                if (envelope.EventType == "run.state_changed" && envelope.SafeSummary == "input_resolved") pending = null;
                if (sequence == snapshot.LastSequence)
                    return pending is null ? (null, "input_recovery_incomplete: pending question unavailable") : (pending, null);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return (null, "input_recovery_timeout: use /follow to retry recovery"); }
        catch (JsonException) { return (null, "input_recovery_incomplete: invalid durable event"); }
        return (null, "input_recovery_incomplete: replay did not reach the run cursor");
    }
}
