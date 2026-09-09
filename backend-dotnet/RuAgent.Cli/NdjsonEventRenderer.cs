using System.Text.Json;
using RuAgent.Protocol.Harness;
using RuAgent.Client;

namespace RuAgent.Cli;

public static class NdjsonEventRenderer
{
    public const int Version = 1;

    // Versioned NDJSON: {"v":1,"type":"event", ...safe envelope fields...}
    // Never prompts/implicit approvals, no private payload auto-fetch, no prompt/command/output/secret fields.

    public static void Render(HarnessSafeEventEnvelope envelope, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(writer);

        // Ensure envelope contains no forbidden fields (defensive)
        // HarnessSafeEventEnvelope by definition has only safe fields, but we still avoid leaking

        var obj = new
        {
            v = Version,
            type = "event",
            protocol_version = envelope.ProtocolVersion,
            event_id = envelope.EventId,
            run_id = envelope.RunId,
            session_id = envelope.SessionId,
            sequence = envelope.Sequence,
            event_type = envelope.EventType,
            occurred_at = envelope.OccurredAt,
            status = envelope.Status,
            safe_summary = envelope.SafeSummary,
            payload_ref = envelope.PayloadRef,
            input_tokens = envelope.InputTokens,
            output_tokens = envelope.OutputTokens,
            total_tokens = envelope.TotalTokens,
        };
        var json = JsonSerializer.Serialize(obj);
        // Double-check no forbidden substrings
        if (ContainsForbidden(json))
            throw new InvalidOperationException("NDJSON contains forbidden field.");
        writer.WriteLine(json);
    }

    public static void RenderFrame(SseFrame frame, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(writer);

        if (frame.IsTransient)
        {
            // For transient, emit versioned NDJSON with type transient but still safe
            // Never fetch private payload automatically
            var truncated = frame.Data.Length > 500 ? frame.Data.Substring(0, 500) : frame.Data;
            var obj = new
            {
                v = Version,
                type = "transient",
                transient_sequence = frame.TransientSequence,
                event_type = frame.EventType,
                data_preview = truncated,
            };
            var json = JsonSerializer.Serialize(obj);
            if (ContainsForbidden(json))
                throw new InvalidOperationException("Transient NDJSON contains forbidden field.");
            writer.WriteLine(json);
            return;
        }

        if (frame.DurableSequence is null)
            return;

        HarnessSafeEventEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<HarnessSafeEventEnvelope>(frame.Data);
        }
        catch (JsonException ex)
        {
            var err = new { v = Version, type = "error", message = "invalid_envelope", detail = ex.Message };
            writer.WriteLine(JsonSerializer.Serialize(err));
            return;
        }
        if (envelope is null)
            return;
        if (envelope.ProtocolVersion != HarnessSafeEventEnvelope.CurrentProtocolVersion)
        {
            var err = new { v = Version, type = "error", message = "unsupported_protocol_version", version = envelope.ProtocolVersion };
            writer.WriteLine(JsonSerializer.Serialize(err));
            return;
        }
        Render(envelope, writer);
    }

    public static bool ContainsForbidden(string json)
    {
        var lower = json.ToLowerInvariant();
        // Forbidden fields that must never appear in NDJSON tracer output
        string[] forbidden = ["\"prompt\"", "\"command\"", "\"output\"", "\"secret\"", "\"argv\"", "\"diff\"", "\"stdout\"", "\"stderr\"", "\"environment\"", "\"exception\""];
        foreach (var f in forbidden)
        {
            if (lower.Contains(f))
                return true;
        }
        return false;
    }
}
