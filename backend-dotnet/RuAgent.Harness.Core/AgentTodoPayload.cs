using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuAgent.Harness.Core;

/// <summary>
/// Reads the agent's task list out of a tool result payload.
/// </summary>
/// <remarks>
/// Shared by the live pipeline, which publishes the list as an activity, and the durable
/// replay, which rebuilds that activity when a session is opened again. Two copies of this
/// rule is how the two would drift, and a drift here is invisible: a replay that fails to
/// recognise the payload simply produces no row, and an absent row looks the same as a turn
/// that never wrote a list.
/// </remarks>
public static class AgentTodoPayload
{
    /// <summary>
    /// The rows in a tool result, or null when the payload carries no list.
    /// </summary>
    /// <remarks>
    /// Returns null rather than throwing for anything unexpected. The plan is a display
    /// aid: a malformed payload should cost a checklist, never a turn or a replay.
    /// </remarks>
    public static IReadOnlyList<AgentTodoRow>? TryRead(ReadOnlySpan<byte> modelContent)
    {
        if (modelContent.IsEmpty)
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(modelContent) is not JsonObject payload
                || payload["todos"] is not JsonArray todos)
            {
                return null;
            }

            var rows = new List<AgentTodoRow>(todos.Count);
            foreach (var node in todos)
            {
                if (node is JsonObject row
                    && row["content"]?.GetValue<string>() is { Length: > 0 } content
                    && row["status"]?.GetValue<string>() is { Length: > 0 } status)
                {
                    rows.Add(new AgentTodoRow(content, status));
                }
            }

            return rows;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            // A value that is present but not the expected type, for example a number
            // where a string belongs.
            return null;
        }
    }
}
