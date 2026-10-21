using System.Text.Json.Nodes;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Runtime.Adapters.Models;

namespace RuBackend.Harness.ModelRunner;

internal static class ChatCompletionsHistoryProjector
{
    public static IReadOnlyList<ModelTurn> Project(IReadOnlyList<ModelTurn> turns,
        IReadOnlyList<ProviderContinuation> continuations, string model)
    {
        var used = new HashSet<ProviderContinuation>();
        return turns.Select(turn =>
        {
            if (turn.Role != "assistant" || turn.ToolCalls.Count == 0) return turn;
            var continuation = continuations.FirstOrDefault(candidate => !used.Contains(candidate)
                && candidate.Provider == "chat" && candidate.Protocol == "chat_completions" && candidate.Model == model
                && Matches(candidate.OutputItems[0], turn));
            if (continuation is null) throw new ModelRunnerProtocolException("provider_continuation_missing");
            used.Add(continuation);
            return new ModelTurn(turn.Role, turn.Content, turn.ToolCalls,
                reasoningContent: continuation.OutputItems[0]["reasoning_content"]!.GetValue<string>());
        }).ToArray();
    }

    private static bool Matches(JsonObject message, ModelTurn turn)
    {
        if (message["text"]?.GetValue<string>() != turn.Content || message["tool_calls"] is not JsonArray calls
            || calls.Count != turn.ToolCalls.Count) return false;
        for (int i = 0; i < calls.Count; i++)
        {
            var call = turn.ToolCalls[i];
            if (calls[i]!["id"]!.GetValue<string>() != call.Id || calls[i]!["name"]!.GetValue<string>() != call.Name
                || !JsonNode.DeepEquals(JsonNode.Parse(calls[i]!["arguments"]!.GetValue<string>()),
                    JsonNode.Parse(call.Arguments.GetRawText()))) return false;
        }
        return true;
    }
}
