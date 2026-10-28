using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuAgent.Harness.Core.Prompting;

/// <summary>Original public Responses assistant messages, including their per-message phase.
/// Private reasoning stays in ProviderContinuation, never in this text-history metadata.</summary>
public sealed record ResponsesAssistantMessages
{
    public const int MaximumMessages = 32;
    // Text-only steps store public text plus its native message shape within the
    // existing 2 MiB AssistantStep payload cap. Tool batches use the smaller
    // ProviderContinuation cap and do not duplicate this metadata in AssistantStep.
    public const int MaximumBytes = 512 * 1024;
    public const int MaximumTextCharacters = 1_000_000;

    public ResponsesAssistantMessages(IReadOnlyList<JsonObject> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count is < 1 or > MaximumMessages || messages.Any(message => message is null))
            throw new ArgumentException("Invalid Responses assistant message count.");
        var cloned = messages.Select(message => (JsonObject)message.DeepClone()).ToArray();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in cloned)
        {
            ValidateMessage(message);
            if (!identities.Add(message["id"]!.GetValue<string>()))
                throw new ArgumentException("Duplicate Responses message identity.");
        }
        var text = string.Concat(cloned.SelectMany(message => message["content"]!.AsArray())
            .Select(part => part![part["type"]!.GetValue<string>() == "refusal" ? "refusal" : "text"]!.GetValue<string>()));
        if (text.Length > MaximumTextCharacters
            || Encoding.UTF8.GetByteCount(new JsonArray(cloned.Select(message => (JsonNode)message.DeepClone()).ToArray()).ToJsonString()) > MaximumBytes)
            throw new ArgumentException("Responses assistant messages exceed their bound.");
        Messages = new ReadOnlyCollection<JsonObject>(cloned);
        Text = text;
    }

    // Read-only nodes follow the same ownership contract as ProviderContinuation.OutputItems.
    public IReadOnlyList<JsonObject> Messages { get; }
    public string Text { get; }
    public bool IsCommentaryOnly => Messages.All(message => message["phase"]?.GetValue<string>() == "commentary");

    public void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartArray();
        foreach (var message in Messages) message.WriteTo(writer);
        writer.WriteEndArray();
    }

    public static ResponsesAssistantMessages Read(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array) throw new ArgumentException("Invalid Responses messages.");
        return new(array.EnumerateArray().Select(item => JsonNode.Parse(item.GetRawText()) as JsonObject
            ?? throw new ArgumentException("Invalid Responses message.")).ToArray());
    }

    internal static void ValidateMessage(JsonObject message)
    {
        string[] fields = ["type", "id", "role", "content", "phase", "status"];
        if (message.Any(field => !fields.Contains(field.Key, StringComparer.Ordinal))
            || message["type"]?.GetValue<string>() != "message"
            || message["role"]?.GetValue<string>() != "assistant"
            || !ProviderCallIdContract.IsValid(message["id"]?.GetValue<string>())
            || message["phase"]?.GetValue<string>() is not (null or "commentary" or "final_answer")
            || message["status"]?.GetValue<string>() is not (null or "completed" or "in_progress" or "incomplete")
            || message["content"] is not JsonArray { Count: > 0 and <= 64 } content)
            throw new ArgumentException("Invalid Responses assistant message.");
        foreach (var part in content)
        {
            if (part is not JsonObject body) throw new ArgumentException("Invalid Responses message content.");
            var type = body["type"]?.GetValue<string>();
            var allowed = type == "output_text" ? new[] { "type", "text", "annotations", "logprobs" }
                : type == "refusal" ? new[] { "type", "refusal" } : [];
            if (allowed.Length == 0 || body.Any(field => !allowed.Contains(field.Key, StringComparer.Ordinal))
                || body[type == "refusal" ? "refusal" : "text"]?.GetValue<string>() is not { } text || text.Contains('\0')
                || body["annotations"] is not (null or JsonArray) || body["logprobs"] is not (null or JsonArray))
                throw new ArgumentException("Invalid Responses message content.");
        }
    }
}
