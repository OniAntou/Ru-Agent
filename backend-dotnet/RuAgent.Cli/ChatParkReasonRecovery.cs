using System.Text.Json;
using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli;

/// <summary>Recovers the companion reason from one committed park, never a later run state.</summary>
internal static class ChatParkReasonRecovery
{
    internal static bool NeedsReplay(SseFrame frame)
    {
        if (frame.IsTransient || frame.DurableSequence is null) return false;
        try
        {
            var envelope = JsonSerializer.Deserialize<HarnessSafeEventEnvelope>(frame.Data);
            return envelope is { EventType: "run.state_changed", Status: "waiting_user" }
                && (string.IsNullOrWhiteSpace(envelope.SafeSummary) || envelope.SafeSummary == "waiting_user");
        }
        catch (JsonException) { return false; }
    }

    internal static async Task<SseFrame?> ReadAsync(AgentSseClient client, string runId,
        long stateSequence, CancellationToken cancellationToken)
    {
        if (stateSequence < 0 || stateSequence == long.MaxValue) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var seen = 0;
            await foreach (var frame in client.ReadEventsAsync(runId, stateSequence, timeout.Token, live: false))
            {
                if (++seen > 4) return null;
                if (frame.IsTransient) continue;
                if (frame.DurableSequence != stateSequence + 1) return null;
                var envelope = JsonSerializer.Deserialize<HarnessSafeEventEnvelope>(frame.Data);
                return envelope is { EventType: "continue.requested", Status: "waiting_user" }
                    && envelope.RunId == runId && envelope.Sequence == stateSequence + 1
                    ? frame : null;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (Exception error) when (error is AgentApiException or HttpRequestException or IOException or JsonException) { }
        cancellationToken.ThrowIfCancellationRequested();
        return null;
    }
}
