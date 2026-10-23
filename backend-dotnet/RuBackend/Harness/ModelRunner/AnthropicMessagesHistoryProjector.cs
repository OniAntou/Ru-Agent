using System.Text.Json.Nodes;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Runtime.Adapters.Models;

namespace RuBackend.Harness.ModelRunner;

internal static class AnthropicMessagesHistoryProjector
{
    public static IReadOnlyList<JsonObject> Project(
        IReadOnlyList<ModelTurn> turns,
        IReadOnlyList<ProviderContinuation> continuations,
        string model)
    {
        var used = new HashSet<ProviderContinuation>();
        var messages = new List<JsonObject>();
        var pendingCalls = new Queue<ModelFunctionCall>();
        for (var index = 0; index < turns.Count; index++)
        {
            var turn = turns[index];
            if (turn.Role == "system") continue;
            if (turn.Role == "tool")
            {
                index = AppendToolResults(turns, index, messages, pendingCalls);
                continue;
            }
            if (pendingCalls.Count > 0) throw new ModelRunnerProtocolException("provider_continuation_invalid");
            AppendAssistantOrUser(turn, continuations, used, messages, pendingCalls, model);
        }
        if (pendingCalls.Count > 0)
            throw new ModelRunnerProtocolException("provider_continuation_invalid");
        return messages.AsReadOnly();
    }

    private static void AppendAssistantOrUser(
        ModelTurn turn,
        IReadOnlyList<ProviderContinuation> continuations,
        HashSet<ProviderContinuation> used,
        List<JsonObject> messages,
        Queue<ModelFunctionCall> pendingCalls,
        string model)
    {
        if (turn.Role == "assistant" && turn.ToolCalls.Count > 0)
        {
            var continuation = ProviderContinuationMatch.FindAnthropic(continuations, used, turn.ToolCalls, model)
                ?? throw new ModelRunnerProtocolException("provider_continuation_missing");
            used.Add(continuation);
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(continuation.OutputItems.Select(item => item.DeepClone()).ToArray()) });
            foreach (var call in turn.ToolCalls) pendingCalls.Enqueue(call);
            return;
        }
        messages.Add(TextMessage(turn.Role, turn.Content));
    }

    private static int AppendToolResults(
        IReadOnlyList<ModelTurn> turns,
        int start,
        List<JsonObject> messages,
        Queue<ModelFunctionCall> pendingCalls)
    {
        var results = new JsonArray();
        var index = start;
        while (index < turns.Count && turns[index].Role == "tool")
        {
            var result = turns[index];
            if (pendingCalls.Count == 0 || pendingCalls.Dequeue().Id != result.ToolCallId)
                throw new ModelRunnerProtocolException("provider_continuation_invalid");
            results.Add(new JsonObject
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = result.ToolCallId,
                ["content"] = ToolImageProjection.AnthropicContent(result),
                ["is_error"] = result.ToolResultCode != "ok",
            });
            index++;
        }
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = results });
        return index - 1;
    }

    private static JsonObject TextMessage(string role, string text) => new()
    {
        ["role"] = role,
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
    };
}
