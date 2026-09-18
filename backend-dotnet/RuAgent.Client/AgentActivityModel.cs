using System.Text.Json;
using RuAgent.Protocol.Harness;

namespace RuAgent.Client;

/// <summary>
/// Validated client representation of the transient <c>agent.activity</c> frame. Raw
/// transient JSON is intentionally not retained.
/// </summary>
public sealed record AgentActivityModel(
    string ActivityId,
    AgentActivityKind Kind,
    AgentActivityState State,
    string? FileName,
    int? LineStart,
    int? LineEnd,
    string? Symbol,
    int? Count,
    int? Added,
    int? Removed,
    int? DurationMs,
    string SafeCode,
    string? ToolDisplay,
    long? DurableSequence,
    long? TransientSequence,
    string? Detail = null,
    IReadOnlyList<AgentTodoWire>? Todos = null)
{
    public static bool TryParse(SseFrame frame, out AgentActivityModel? activity)
    {
        activity = null;
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

            if (!string.Equals(kind, "agent.activity", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(innerJson))
            {
                return false;
            }

            var wire = AgentActivityWireCodec.Parse(innerJson);
            activity = new AgentActivityModel(
                wire.ActivityId,
                wire.Kind,
                wire.State,
                wire.FileName,
                wire.LineStart,
                wire.LineEnd,
                wire.Symbol,
                wire.Count,
                wire.Added,
                wire.Removed,
                wire.DurationMs,
                wire.SafeCode,
                wire.ToolDisplay,
                frame.DurableSequence,
                frame.TransientSequence,
                wire.Detail,
                wire.Todos);
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
