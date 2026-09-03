using System.Text.Json;
using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli.Tui;

public static class TuiEventRenderer
{
    public static void RenderFrame(SseFrame frame, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(writer);

        if (frame.IsTransient)
        {
            RenderTransientFrame(frame, writer);
            return;
        }

        if (frame.DurableSequence is null)
            return;

        HarnessSafeEventEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<HarnessSafeEventEnvelope>(frame.Data);
        }
        catch
        {
            writer.WriteLine(AnsiTheme.Red($"[{frame.DurableSequence}] invalid_envelope"));
            return;
        }

        if (envelope is null)
        {
            writer.WriteLine(AnsiTheme.Red($"[{frame.DurableSequence}] empty_envelope"));
            return;
        }

        RenderEnvelope(envelope, writer);
    }

    public static void RenderEnvelope(HarnessSafeEventEnvelope envelope, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(writer);

        var seq = envelope.Sequence;
        var type = envelope.EventType;
        var status = envelope.Status;
        var summary = Escape(envelope.SafeSummary);

        switch (type)
        {
            case "model.turn_started":
                writer.WriteLine(AnsiTheme.Cyan($"┌─ Turn #{seq}: Model Thinking..."));
                break;

            case "tool.execution_requested":
                writer.WriteLine(AnsiTheme.Yellow($"├─ [Tool Request #{seq}] ") + AnsiTheme.Bold(summary));
                break;

            case "tool.executed":
                if (status == "succeeded")
                {
                    writer.WriteLine(AnsiTheme.Green($"├─ [Tool Done #{seq}] ") + summary);
                }
                else
                {
                    writer.WriteLine(AnsiTheme.Red($"├─ [Tool Failed #{seq}] ") + summary + $" ({status})");
                }
                break;

            case "approval.requested":
                writer.WriteLine(AnsiTheme.Yellow($"├─ [APPROVAL NEEDED #{seq}] ") + AnsiTheme.Bold(summary));
                break;

            case "reconciliation.required":
            case "reconciliation.resolved":
                writer.WriteLine(AnsiTheme.Magenta($"├─ [{type} #{seq}] ") + summary);
                break;

            case "transient.frames_dropped":
                writer.WriteLine(AnsiTheme.Red($"├─ [WARNING #{seq}] Dropped frames: ") + summary);
                break;

            case "run.completed":
                if (status == "succeeded")
                {
                    writer.WriteLine(AnsiTheme.Green($"└─ [Run #{seq} Succeeded] ") + summary);
                }
                else
                {
                    writer.WriteLine(AnsiTheme.Red($"└─ [Run #{seq} {status.ToUpperInvariant()}] ") + summary);
                }
                break;

            default:
                var statusBadge = status == "succeeded" ? AnsiTheme.Green($"[{status}]")
                    : status == "failed" ? AnsiTheme.Red($"[{status}]")
                    : AnsiTheme.Gray($"[{status}]");
                writer.WriteLine($"├─ [{seq}] {AnsiTheme.Bold(type)} {statusBadge} {AnsiTheme.Dim(summary)}");
                break;
        }
    }

    private static void RenderTransientFrame(SseFrame frame, TextWriter writer)
    {
        var preview = frame.Data.Length > 200 ? frame.Data.Substring(0, 200) + "..." : frame.Data;
        if (ContainsForbidden(preview))
        {
            preview = "[redacted]";
        }
        writer.WriteLine(AnsiTheme.Dim($"   ... {frame.EventType}: {preview}"));
    }

    private static string Escape(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        if (s.Length > 500) s = s.Substring(0, 500) + "...";
        return s.Replace("\n", " ").Replace("\r", " ");
    }

    private static bool ContainsForbidden(string json)
    {
        var lower = json.ToLowerInvariant();
        return lower.Contains("\"prompt\"") || lower.Contains("\"command\"") || lower.Contains("\"output\"") || lower.Contains("\"secret\"") || lower.Contains("\"argv\"") || lower.Contains("\"diff\"");
    }
}
