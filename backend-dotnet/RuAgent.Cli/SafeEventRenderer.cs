using System.Text;
using System.Text.Json;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli;

/// <summary>
/// Human-readable rendering of the durable event stream for the append-only commands
/// (<c>agent run</c>, <c>agent attach</c>). Safe envelope fields only: the stream never
/// carries prompt, command, output or secret text, and a transient frame carries raw
/// model/tool content that this renderer deliberately withholds.
/// </summary>
public static class SafeEventRenderer
{
    public static void Render(HarnessSafeEventEnvelope envelope, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(writer);

        // Display only safe event fields. Never include prompt/command/output/secret.
        if (envelope.EventType == "transient.frames_dropped")
        {
            writer.WriteLine($"[{envelope.Sequence}] WARNING dropped-frames: {Escape(envelope.SafeSummary)} id={envelope.EventId}");
            return;
        }

        if (envelope.EventType is "reconciliation.required" or "reconciliation.resolved")
        {
            writer.WriteLine($"[{envelope.Sequence}] {envelope.EventType} intent={Escape(envelope.SafeSummary)} status={envelope.Status} id={envelope.EventId}");
            return;
        }

        var payloadPart = envelope.PayloadRef is not null ? $" payload_ref:{envelope.PayloadRef}" : string.Empty;
        writer.WriteLine($"[{envelope.Sequence}] {envelope.EventType} status={envelope.Status} summary={Escape(envelope.SafeSummary)}{payloadPart} id={envelope.EventId}");
    }

    public static void RenderFrame(RuAgent.Client.SseFrame frame, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(writer);

        if (frame.IsTransient)
        {
            // Transient frames are connection-local and carry raw model text or tool output
            // in their data. Printing even a prefix of it contradicts the tracer contract and
            // the "safe fields only" promise this class documents, and the substring guard
            // that used to police it both missed nested payloads and redacted ordinary prose
            // that merely quoted a field name. Report the shape, never the bytes.
            var bytes = Encoding.UTF8.GetByteCount(frame.Data ?? string.Empty);
            writer.WriteLine($"[transient:{frame.TransientSequence}] {frame.EventType} data_bytes={bytes}");
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
        if (s.Length > 500) return Tui.TuiText.SafeCut(s, 500) + "...";
        return s.Replace("\n", " ").Replace("\r", " ");
    }
}
