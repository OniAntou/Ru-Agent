using System.Text.Json;
using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli;

internal static class RunParkSignal
{
    internal static bool TryRead(SseFrame frame, string runId, out string? reason)
    {
        reason = null;
        if (frame.IsTransient || frame.DurableSequence is null) return false;
        try
        {
            var envelope = JsonSerializer.Deserialize<HarnessSafeEventEnvelope>(frame.Data);
            if (envelope is null || envelope.RunId != runId || envelope.Sequence != frame.DurableSequence)
                return false;
            var parked = envelope is { EventType: "continue.requested", Status: "waiting_user" }
                or { EventType: "reconciliation.required", Status: "reconciliation_required" }
                or { EventType: "run.state_changed", Status: "reconciliation_required" };
            if (!parked) return false;
            var summary = envelope.SafeSummary;
            reason = summary is { Length: > 0 and <= 128 }
                && summary is not ("waiting_user" or "reconciliation_required")
                && summary.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_')
                ? summary : null;
            return true;
        }
        catch (JsonException) { return false; }
    }
}
