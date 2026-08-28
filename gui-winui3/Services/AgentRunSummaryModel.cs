using System.Text.Json;
using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuApp.Services;

/// <summary>
/// Validated transient terminal summary. The model exposes only typed safe fields and
/// never retains the raw SSE payload.
/// </summary>
public sealed record AgentRunSummaryModel(
    string Status,
    string VerificationState,
    string? WorkspaceLabel,
    int? FilesScanned,
    int? FilesChanged,
    int? FilesCreated,
    int? FilesDeleted,
    long? DurableSequence,
    long? TransientSequence)
{
    public static bool TryParse(SseFrame frame, out AgentRunSummaryModel? summary)
    {
        summary = null;
        if (!frame.IsTransient || string.IsNullOrWhiteSpace(frame.Data))
        {
            return false;
        }

        try
        {
            using var outer = JsonDocument.Parse(frame.Data);
            if (outer.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var properties = new HashSet<string>(StringComparer.Ordinal);
            string? kind = null;
            string? innerJson = null;
            foreach (var property in outer.RootElement.EnumerateObject())
            {
                if (!properties.Add(property.Name))
                {
                    return false;
                }

                switch (property.Name)
                {
                    case "kind" when property.Value.ValueKind == JsonValueKind.String:
                        kind = property.Value.GetString();
                        break;
                    case "data" when property.Value.ValueKind == JsonValueKind.String:
                        innerJson = property.Value.GetString();
                        break;
                    default:
                        return false;
                }
            }

            if (!string.Equals(kind, "agent.summary", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(innerJson))
            {
                return false;
            }

            var wire = AgentRunSummaryWireCodec.Parse(innerJson);
            summary = new AgentRunSummaryModel(
                wire.Status,
                wire.VerificationState,
                wire.WorkspaceLabel,
                wire.FilesScanned,
                wire.FilesChanged,
                wire.FilesCreated,
                wire.FilesDeleted,
                frame.DurableSequence,
                frame.TransientSequence);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
