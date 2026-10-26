using System.Text.Json.Nodes;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Runtime.Adapters.Models;

namespace RuBackend.Harness.ModelRunner;

internal static class OpenAiResponsesHistoryProjector
{
    public static IReadOnlyList<JsonObject> Project(
        IReadOnlyList<ModelTurn> turns,
        IReadOnlyList<ProviderContinuation> continuations,
        string model)
    {
        var used = new HashSet<ProviderContinuation>();
        var items = new List<JsonObject>();
        foreach (var turn in turns)
        {
            if (turn.Role == "system") continue;
            if (turn.Role == "tool")
            {
                items.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = turn.ToolCallId, ["output"] = ToolImageProjection.ResponsesOutput(turn) });
                continue;
            }
            if (turn.Role == "assistant" && turn.ToolCalls.Count > 0)
            {
                var continuation = ProviderContinuationMatch.FindOpenAi(continuations, used, turn.ToolCalls, model)
                    ?? throw new ModelRunnerProtocolException("provider_continuation_missing");
                used.Add(continuation);
                if (!continuation.OutputItems.Any(item => item["type"]?.GetValue<string>() == "message"))
                {
                    if (turn.ResponsesMessages is { } messages)
                        items.AddRange(messages.Messages.Select(message => (JsonObject)message.DeepClone()));
                    else if (!string.IsNullOrWhiteSpace(turn.Content))
                        items.Add(new JsonObject { ["role"] = "assistant", ["content"] = turn.Content });
                }
                foreach (var output in continuation.OutputItems) items.Add((JsonObject)output.DeepClone());
                continue;
            }
            if (turn.Role == "assistant" && turn.ResponsesMessages is { } responseMessages)
                items.AddRange(responseMessages.Messages.Select(message => (JsonObject)message.DeepClone()));
            else items.Add(new JsonObject { ["role"] = turn.Role, ["content"] = turn.Content });
        }
        return items.AsReadOnly();
    }
}
