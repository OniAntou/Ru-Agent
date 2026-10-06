using RuAgent.Harness.Core.Prompting;
using RuAgent.Runtime.Adapters.Models;

namespace RuBackend.Harness.Worker;

internal static class OpenAiResponsesHistoryProjector
{
    public static IReadOnlyList<System.Text.Json.Nodes.JsonObject> Project(
        IReadOnlyList<ModelTurn> turns,
        IReadOnlyList<ProviderContinuation> continuations)
    {
        var used = new HashSet<ProviderContinuation>();
        var items = new List<System.Text.Json.Nodes.JsonObject>();
        foreach (var turn in turns)
        {
            if (turn.Role == "system") continue;
            if (turn.Role == "tool")
            {
                items.Add(new System.Text.Json.Nodes.JsonObject { ["type"] = "function_call_output", ["call_id"] = turn.ToolCallId, ["output"] = turn.Content });
                continue;
            }
            if (turn.Role == "assistant" && turn.ToolCalls.Count > 0)
            {
                var continuation = continuations.FirstOrDefault(candidate => !used.Contains(candidate)
                    && MatchesCallBatch(candidate, turn.ToolCalls));
                if (continuation is null) throw new WorkerProtocolException("provider_continuation_missing");
                used.Add(continuation);
                if (!string.IsNullOrWhiteSpace(turn.Content)) items.Add(new System.Text.Json.Nodes.JsonObject { ["role"] = "assistant", ["content"] = turn.Content });
                foreach (var output in continuation.OutputItems) items.Add((System.Text.Json.Nodes.JsonObject)output.DeepClone());
                continue;
            }
            items.Add(new System.Text.Json.Nodes.JsonObject { ["role"] = turn.Role, ["content"] = turn.Content });
        }
        return items.AsReadOnly();
    }

    private static bool MatchesCallBatch(ProviderContinuation continuation, IReadOnlyList<ModelFunctionCall> calls)
    {
        var providerCalls = continuation.OutputItems
            .Where(item => item["type"]?.GetValue<string>() == "function_call")
            .ToArray();
        if (providerCalls.Length != calls.Count) return false;
        for (var index = 0; index < calls.Count; index++)
        {
            var arguments = providerCalls[index]["arguments"]?.GetValue<string>();
            if (providerCalls[index]["call_id"]?.GetValue<string>() != calls[index].Id
                || providerCalls[index]["name"]?.GetValue<string>() != calls[index].Name
                || arguments is null
                || !System.Text.Json.Nodes.JsonNode.DeepEquals(
                    System.Text.Json.Nodes.JsonNode.Parse(arguments),
                    System.Text.Json.Nodes.JsonNode.Parse(calls[index].Arguments.GetRawText())))
                return false;
        }

        return true;
    }
}
