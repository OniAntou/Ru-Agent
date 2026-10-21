using System.Text.Json.Nodes;

namespace RuAgent.Harness.Core.Prompting;

/// <summary>Private, bounded DeepSeek Chat reasoning paired with the exact assistant tool batch.</summary>
public static class ChatProviderContinuation
{
    public static ProviderContinuation Create(string model, string? text, string reasoning, IReadOnlyList<ModelToolCall> calls) =>
        new("chat", "chat_completions", model, 1,
        [new JsonObject
        {
            ["type"] = "chat_assistant", ["text"] = text ?? string.Empty, ["reasoning_content"] = reasoning,
            ["tool_calls"] = new JsonArray(calls.Select(call => (JsonNode)new JsonObject
            {
                ["id"] = call.ProviderCallId, ["name"] = call.ToolId, ["arguments"] = call.Arguments.GetRawText(),
            }).ToArray()),
        }]);

    internal static void Validate(IReadOnlyList<JsonObject> items)
    {
        if (items.Count != 1) throw new ArgumentException("Invalid chat continuation count.");
        var item = items[0];
        if (item.Count != 4 || item.Any(pair => pair.Key is not ("type" or "text" or "reasoning_content" or "tool_calls"))
            || item["type"]?.GetValue<string>() != "chat_assistant"
            || item["text"]?.GetValue<string>() is not { } text || text.Contains('\0')
            || item["reasoning_content"]?.GetValue<string>() is not { } reasoning || reasoning.Contains('\0')
            || item["tool_calls"] is not JsonArray { Count: > 0 and <= 16 } calls)
            throw new ArgumentException("Invalid chat continuation shape.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in calls)
        {
            if (node is not JsonObject call || call.Count != 3
                || call.Any(pair => pair.Key is not ("id" or "name" or "arguments"))
                || call["id"]?.GetValue<string>() is not { } id || !ProviderCallIdContract.IsValid(id) || !ids.Add(id)
                || call["name"]?.GetValue<string>() is not { Length: > 0 and <= 128 }
                || call["arguments"]?.GetValue<string>() is not { } arguments
                || System.Text.Encoding.UTF8.GetByteCount(arguments) > ProviderContinuation.MaximumFunctionArgumentsBytes
                || JsonNode.Parse(arguments) is not JsonObject)
                throw new ArgumentException("Invalid chat continuation tool identity.");
        }
    }
}
