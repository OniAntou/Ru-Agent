using System.Text.Json;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli;

public static class SafeEventRenderer
{
    public static void Render(HarnessSafeEventEnvelope envelope, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(writer);

        // Display only safe event fields. Never include prompt/command/output/secret.
        // Format is human-readable bounded safe summary.
        // Example: [1] run.state_changed (running) ok - payload:payload_...?...
        var payloadPart = envelope.PayloadRef is not null ? $" payload_ref:{envelope.PayloadRef}" : string.Empty;
        writer.WriteLine($"[{envelope.Sequence}] {envelope.EventType} status={envelope.Status} summary={Escape(envelope.SafeSummary)}{payloadPart} id={envelope.EventId}");
    }

    public static void RenderFrame(RuAgent.Client.SseFrame frame, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(writer);
        if (frame.IsTransient)
        {
            // Transient frames are connection-local and do not advance durable cursor.
            // Render only safe bounded preview, never raw private content.
            var preview = frame.Data.Length > 200 ? frame.Data.Substring(0, 200) + "..." : frame.Data;
            // Ensure no forbidden fields leak (defensive)
            if (ContainsForbidden(preview))
                preview = "[redacted]";
            writer.WriteLine($"[transient:{frame.TransientSequence}] {frame.EventType} {preview}");
            return;
        }

        if (frame.DurableSequence is null)
            return;

        // For durable, data should be HarnessSafeEventEnvelope JSON
        HarnessSafeEventEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<HarnessSafeEventEnvelope>(frame.Data);
        }
        catch
        {
            writer.WriteLine($"[{frame.DurableSequence}] invalid_envelope");
            return;
        }
        if (envelope is null)
        {
            writer.WriteLine($"[{frame.DurableSequence}] empty_envelope");
            return;
        }
        // Validate protocol version is supported
        if (envelope.ProtocolVersion != HarnessSafeEventEnvelope.CurrentProtocolVersion)
        {
            writer.WriteLine($"[{frame.DurableSequence}] unsupported_protocol_version {envelope.ProtocolVersion}");
            return;
        }
        Render(envelope, writer);
    }

    private static string Escape(string s)
    {
        if (s.Length > 500) return s.Substring(0, 500) + "...";
        return s.Replace("\n", " ").Replace("\r", " ");
    }

    private static bool ContainsForbidden(string json)
    {
        // Quick check for forbidden field names that must never appear in safe output
        var lower = json.ToLowerInvariant();
        return lower.Contains("\"prompt\"") || lower.Contains("\"command\"") || lower.Contains("\"output\"") || lower.Contains("\"secret\"") || lower.Contains("\"argv\"") || lower.Contains("\"diff\"");
    }
}
