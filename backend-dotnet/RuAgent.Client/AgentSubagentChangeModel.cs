using System.Text.Json;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Client;

/// <summary>One child as the root's live hint reports it: the newest attempt's state.</summary>
public sealed record AgentSubagentState(string AgentId, string Status, int AttemptNumber, bool Canceled, int ResultCount);

/// <summary>
/// Validated <c>subagent.changed</c> hint for a root run: the whole child set at the moment
/// of the change. A hint is loss-tolerant and every one carries the full set, so a client that
/// missed earlier frames converges on the next one; the durable list endpoint stays authoritative.
/// </summary>
public sealed record AgentSubagentChangeModel(
    string RootRunId,
    IReadOnlyList<AgentSubagentState> Agents,
    long TransientSequence)
{
    private const int MaximumAgents = 64;
    private const string Kind = "subagent.changed";

    public static bool TryParse(SseFrame frame, out AgentSubagentChangeModel? change)
    {
        change = null;
        if (!frame.IsTransient
            || (frame.EventType is not "transient" and not Kind)
            || frame.TransientSequence is not > 0
            || string.IsNullOrWhiteSpace(frame.Data))
        {
            return false;
        }

        try
        {
            using var outer = JsonDocument.Parse(frame.Data);
            if (!TryReadOuter(outer.RootElement, out var innerJson))
            {
                return false;
            }

            using var inner = JsonDocument.Parse(innerJson);
            if (!TryReadInner(inner.RootElement, out var root, out var agents))
            {
                return false;
            }

            change = new AgentSubagentChangeModel(root, agents, frame.TransientSequence.Value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadOuter(JsonElement root, out string innerJson)
    {
        innerJson = string.Empty;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("kind", out var kind)
            || kind.ValueKind != JsonValueKind.String
            || !string.Equals(kind.GetString(), Kind, StringComparison.Ordinal)
            || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        innerJson = data.GetString() ?? string.Empty;
        return innerJson.Length > 0;
    }

    private static bool TryReadInner(JsonElement root, out string rootRunId, out IReadOnlyList<AgentSubagentState> agents)
    {
        rootRunId = string.Empty;
        agents = [];
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("root_run_id", out var rootProperty)
            || rootProperty.ValueKind != JsonValueKind.String
            || rootProperty.GetString() is not { } id
            || !id.StartsWith("run_", StringComparison.Ordinal)
            || !root.TryGetProperty("agents", out var list)
            || list.ValueKind != JsonValueKind.Array
            || list.GetArrayLength() > MaximumAgents)
        {
            return false;
        }

        var states = new List<AgentSubagentState>();
        foreach (var element in list.EnumerateArray())
        {
            if (!TryReadAgent(element, out var state))
            {
                return false;
            }

            states.Add(state);
        }

        rootRunId = id;
        agents = states;
        return true;
    }

    private static bool TryReadAgent(JsonElement element, out AgentSubagentState state)
    {
        state = null!;
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("agent_id", out var idProperty)
            || idProperty.ValueKind != JsonValueKind.String
            || idProperty.GetString() is not { } agentId
            || !SubagentContractValidator.IsValidId(agentId))
        {
            return false;
        }

        var canceled = element.TryGetProperty("canceled", out var canceledProperty)
            && canceledProperty.ValueKind == JsonValueKind.True;
        var status = "unavailable";
        var attemptNumber = 0;
        if (element.TryGetProperty("attempts", out var attempts) && attempts.ValueKind == JsonValueKind.Array)
        {
            foreach (var attempt in attempts.EnumerateArray())
            {
                if (attempt.ValueKind != JsonValueKind.Object
                    || !attempt.TryGetProperty("status", out var statusProperty)
                    || statusProperty.ValueKind != JsonValueKind.String
                    || statusProperty.GetString() is not { Length: > 0 and <= 64 } text)
                {
                    continue;
                }

                var number = attempt.TryGetProperty("attempt_number", out var numberProperty)
                    && numberProperty.TryGetInt32(out var parsed) ? parsed : 0;
                if (number >= attemptNumber)
                {
                    status = text;
                    attemptNumber = number;
                }
            }
        }

        var results = element.TryGetProperty("results", out var resultList) && resultList.ValueKind == JsonValueKind.Array
            ? resultList.GetArrayLength()
            : 0;
        state = new AgentSubagentState(agentId, status, attemptNumber, canceled, results);
        return true;
    }
}
