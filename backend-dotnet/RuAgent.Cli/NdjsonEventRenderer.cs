using System.Text.Json;
using System.Text.Json.Nodes;
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

        var obj = new JsonObject
        {
            ["v"] = Version,
            ["type"] = "event",
            ["protocol_version"] = envelope.ProtocolVersion,
            ["event_id"] = envelope.EventId,
            ["run_id"] = envelope.RunId,
            ["session_id"] = envelope.SessionId,
            ["sequence"] = envelope.Sequence,
            ["event_type"] = envelope.EventType,
            ["occurred_at"] = envelope.OccurredAt,
            ["status"] = envelope.Status,
            ["safe_summary"] = envelope.SafeSummary,
            ["payload_ref"] = envelope.PayloadRef,
            ["input_tokens"] = envelope.InputTokens,
            ["output_tokens"] = envelope.OutputTokens,
            ["total_tokens"] = envelope.TotalTokens,
        };
        // A headless consumer waits on an approval it cannot see otherwise: the
        // id is the only handle `agent approve` accepts.
        if (!string.IsNullOrEmpty(envelope.ApprovalId))
        {
            obj["approval_id"] = envelope.ApprovalId;
        }

        var json = obj.ToJsonString();
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
            // Transient frames are connection-local and carry raw model text or
            // tool output in `data`. The documented tracer contract is that --json
            // emits safe envelope fields only and never output/stdout/stderr, so
            // the payload itself is withheld and only its bounded size is
            // reported. A consumer that genuinely wants the streamed text reads
            // the durable payload instead of the NDJSON trace.
            var obj = new
            {
                v = Version,
                type = "transient",
                transient_sequence = frame.TransientSequence,
                event_type = frame.EventType,
                // Bytes, not chars: it is named data_bytes and a consumer sizing
                // buffers against it needs the wire size.
                data_bytes = System.Text.Encoding.UTF8.GetByteCount(frame.Data),
            };
            writer.WriteLine(JsonSerializer.Serialize(obj));
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

        try
        {
            Render(envelope, writer);
        }
        catch (InvalidOperationException ex)
        {
            // One frame the privacy guard rejects (or that otherwise fails to
            // render) must not kill the subscription: the durable cursor already
            // advanced past it, so the rest of the stream still arrives.
            var err = new { v = Version, type = "error", message = "invalid_frame", detail = ex.Message };
            writer.WriteLine(JsonSerializer.Serialize(err));
        }
    }

    /// <summary>
    /// True when the document carries a forbidden raw-content field.
    /// </summary>
    /// <remarks>
    /// The check is structural: a forbidden name is a JSON property NAME, or a
    /// property name inside a JSON document embedded as a string value (raw
    /// content smuggled as text). Matching the quoted word anywhere in the
    /// serialized document dropped legitimate events whose summary merely
    /// quoted a word like "output", and a dropped terminal event left stream
    /// consumers waiting forever. Embedded documents are checked to a bounded
    /// depth; anything deeper fails closed. A document that does not parse
    /// cannot be vouched for and is treated as forbidden.
    /// </remarks>
    public static bool ContainsForbidden(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return ContainsForbidden(document.RootElement, depth: 0);
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static bool ContainsForbidden(JsonElement element, int depth) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Any(property =>
            ForbiddenFields.Contains(property.Name)
            || ContainsForbidden(property.Value, depth)),
        JsonValueKind.Array => element.EnumerateArray().Any(item => ContainsForbidden(item, depth)),
        JsonValueKind.String => ContainsForbiddenString(element.GetString(), depth),
        _ => false,
    };

    private static bool ContainsForbiddenString(string? value, int depth)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var trimmed = value.AsSpan().TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            // Ordinary text: prose that happens to quote a field name is not a
            // smuggled payload.
            return false;
        }

        // A string that *is* a JSON document is raw content smuggled as text:
        // check its structure too. The depth bound stops a nest built only to
        // exhaust the stack; beyond it the document fails closed.
        const int MaximumEmbeddedDepth = 4;
        if (depth >= MaximumEmbeddedDepth)
        {
            return true;
        }

        try
        {
            using var inner = JsonDocument.Parse(value);
            return ContainsForbidden(inner.RootElement, depth + 1);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static readonly HashSet<string> ForbiddenFields = new(
        ["prompt", "command", "output", "secret", "argv", "diff", "stdout", "stderr", "environment", "exception"],
        StringComparer.OrdinalIgnoreCase);
}
