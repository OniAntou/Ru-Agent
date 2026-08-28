using System.Text.Json;
using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuApp.Services;

public sealed record AgentEventModel(
    string EventId,
    string RunId,
    string SessionId,
    string EventType,
    string OccurredAt,
    string Status,
    string SafeSummary,
    string? PayloadRef,
    string? ApprovalId,
    long? Sequence,
    bool IsTransient,
    long? TransientSequence,
    string? TransientData)
{
    public static AgentEventModel FromSseFrame(SseFrame frame)
    {
        if (frame.IsTransient)
        {
            return new AgentEventModel(
                string.Empty,
                string.Empty,
                string.Empty,
                frame.EventType,
                string.Empty,
                string.Empty,
                string.Empty,
                null,
                null,
                null,
                true,
                frame.TransientSequence,
                frame.Data);
        }

        if (string.Equals(frame.EventType, "final_snapshot", StringComparison.Ordinal))
        {
            try
            {
                using var snapshot = JsonDocument.Parse(frame.Data);
                var status = snapshot.RootElement.TryGetProperty("status", out var statusElement)
                    && statusElement.ValueKind == JsonValueKind.String
                    ? statusElement.GetString() ?? "unknown"
                    : "unknown";
                return new AgentEventModel(
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    "final_snapshot",
                    string.Empty,
                    status,
                    status,
                    null,
                    null,
                    null,
                    false,
                    null,
                    null);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Invalid final snapshot payload: {ex.Message}", ex);
            }
        }

        HarnessSafeEventEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<HarnessSafeEventEnvelope>(frame.Data);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid durable event payload: {ex.Message}", ex);
        }

        if (envelope is null)
        {
            throw new InvalidOperationException("Empty durable event payload.");
        }

        return new AgentEventModel(
            envelope.EventId,
            envelope.RunId,
            envelope.SessionId,
            envelope.EventType,
            envelope.OccurredAt,
            envelope.Status,
            envelope.SafeSummary,
            envelope.PayloadRef,
            envelope.ApprovalId,
            envelope.Sequence,
            false,
            null,
            null);
    }

    public override string ToString() =>
        $"AgentEvent[{EventType}] seq={Sequence} transient={IsTransient}";
}
