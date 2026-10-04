using System.Text;
using System.Text.Json.Nodes;

namespace RuAgent.Runtime.Adapters.Models;

public sealed record ModelRequest
{
    private const int MaxModelMessages = 256;
    private const int MaxModelMessagesBytes = 512 * 1024;
    public ModelRequest(
        string model,
        string systemPrompt,
        string prompt,
        IReadOnlyList<ModelTurn> turns,
        JsonObject settings,
        Uri endpoint,
        string credential,
        int? maximumOutputTokens = null,
        IReadOnlyList<ModelImage>? images = null,
        IReadOnlyList<JsonObject>? responsesInputItems = null)
    {
        if (string.IsNullOrWhiteSpace(model) || model.Length > 256 || model.Contains('\0'))
        {
            throw new ArgumentException("Model is invalid.", nameof(model));
        }

        if (systemPrompt is null || systemPrompt.Length > 1_000_000 || systemPrompt.Contains('\0'))
        {
            throw new ArgumentException("System prompt exceeds the adapter cap.", nameof(systemPrompt));
        }

        if (images is null || images.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 16_000 || prompt.Contains('\0'))
            {
                throw new ArgumentException("Prompt is invalid.", nameof(prompt));
            }
        }
        else
        {
            if (prompt is null || prompt.Length > 16_000 || prompt.Contains('\0'))
            {
                throw new ArgumentException("Prompt is invalid.", nameof(prompt));
            }
        }

        ArgumentNullException.ThrowIfNull(turns);
        if (turns.Count > MaxModelMessages || turns.Any(turn => turn is null))
        {
            throw new ArgumentException("Model turns exceed the adapter cap.", nameof(turns));
        }

        ValidateModelHistory(turns);
        if (responsesInputItems is { Count: > 256 }
            || responsesInputItems?.Any(item => !IsSupportedResponsesInputItem(item)) == true)
            throw new ArgumentException("Responses input history is invalid.", nameof(responsesInputItems));
        var historyBytes = responsesInputItems is { Count: > 0 }
            ? MeasureResponsesInputBytes(responsesInputItems)
            : MeasureModelMessagesBytes(turns);
        if (historyBytes > MaxModelMessagesBytes)
            throw new ArgumentException("Model history exceeds the adapter byte cap.", nameof(responsesInputItems));

        ArgumentNullException.ThrowIfNull(settings);
        if (settings.ToJsonString().Length > 128 * 1024)
        {
            throw new ArgumentException("Model settings exceed the adapter cap.", nameof(settings));
        }

        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(endpoint.UserInfo))
        {
            throw new ArgumentException("Provider endpoint must be HTTP(S) without user info.", nameof(endpoint));
        }
        if (maximumOutputTokens is < 1 or > 128000) throw new ArgumentException("Maximum output tokens is invalid.", nameof(maximumOutputTokens));

        if (images is not null)
        {
            if (images.Count > 4)
            {
                throw new ArgumentException("Too many images. Maximum is 4.", nameof(images));
            }

            if (images.Any(img => img is null))
            {
                throw new ArgumentException("Images contains null entry.", nameof(images));
            }

            const int MaxImageBase64Chars = 5_600_000;
            const long MaxTotalImagesBytes = 20L * 1024 * 1024;

            long totalDecoded = 0;
            foreach (var img in images)
            {
                if (img.MimeType is not ("image/png" or "image/jpeg" or "image/webp"))
                {
                    throw new ArgumentException("MimeType is invalid. Only image/png, image/jpeg, image/webp are allowed.", nameof(images));
                }

                if (string.IsNullOrWhiteSpace(img.Base64Data) || img.Base64Data.Length > MaxImageBase64Chars)
                {
                    throw new ArgumentException("Base64Data is invalid.", nameof(images));
                }

                // Estimate decoded bytes: (len * 3 / 4) minus base64 padding.
                // Per-image size is enforced by ModelImage's character cap;
                // only the 20MB total is enforced here.
                var padding = 0;
                if (img.Base64Data.EndsWith("==", StringComparison.Ordinal)) padding = 2;
                else if (img.Base64Data.EndsWith("=", StringComparison.Ordinal)) padding = 1;
                var decoded = (img.Base64Data.Length * 3L) / 4 - padding;

                totalDecoded += decoded;
                if (totalDecoded > MaxTotalImagesBytes)
                {
                    throw new ArgumentException("Total decoded images exceed 20MB.", nameof(images));
                }
            }
        }

        Model = model;
        SystemPrompt = systemPrompt;
        Prompt = prompt;
        Turns = Array.AsReadOnly(turns.ToArray());
        Settings = (JsonObject)settings.DeepClone();
        Endpoint = endpoint;
        Credential = credential ?? throw new ArgumentNullException(nameof(credential));
        MaximumOutputTokens = maximumOutputTokens;
        Images = Array.AsReadOnly(images is null ? Array.Empty<ModelImage>() : images.ToArray());
        ResponsesInputItems = Array.AsReadOnly(responsesInputItems?.Select(item => (JsonObject)item.DeepClone()).ToArray() ?? Array.Empty<JsonObject>());
    }

    public string Model { get; }

    public string SystemPrompt { get; }

    public string Prompt { get; }

    public IReadOnlyList<ModelTurn> Turns { get; }

    public JsonObject Settings { get; }

    public Uri Endpoint { get; }

    public string Credential { get; }

    public int? MaximumOutputTokens { get; }

    public IReadOnlyList<ModelImage> Images { get; }

    public IReadOnlyList<JsonObject> ResponsesInputItems { get; }

    public static long MeasureResponsesInputBytes(IReadOnlyList<JsonObject>? items)
    {
        if (items is null || items.Count == 0) return 0;
        long bytes = 2;
        for (var index = 0; index < items.Count; index++)
        {
            bytes = checked(bytes + Encoding.UTF8.GetByteCount(items[index].ToJsonString()));
            if (index > 0) bytes = checked(bytes + 1);
        }

        return bytes;
    }

    private static bool IsSupportedResponsesInputItem(JsonObject? item)
    {
        if (item is null) return false;
        var type = item["type"]?.GetValue<string>();
        if (type is "function_call" or "function_call_output" or "reasoning" or "message") return true;
        var role = item["role"]?.GetValue<string>();
        return role is "user" or "assistant"
            && item["content"] is JsonValue content
            && content.TryGetValue<string>(out _);
    }

    public static long MeasureModelMessagesBytes(IReadOnlyList<ModelTurn> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        long bytes = 2;
        for (var index = 0; index < messages.Count; index++)
        {
            bytes = checked(bytes + Encoding.UTF8.GetByteCount(messages[index].ToJsonObject().ToJsonString()));
            if (index > 0)
            {
                bytes = checked(bytes + 1);
            }
        }

        return bytes;
    }

    private static void ValidateModelHistory(IReadOnlyList<ModelTurn> turns)
    {
        var pendingCallIds = new Queue<string>();
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var turn in turns)
        {
            ValidateTurnPairing(turn, pendingCallIds, callIds);
        }

        if (MeasureModelMessagesBytes(turns) > MaxModelMessagesBytes)
        {
            throw new ArgumentException("Model history exceeds the adapter byte cap.", nameof(turns));
        }

        if (pendingCallIds.Count > 0)
        {
            throw new ArgumentException("Model history contains an unmatched tool call batch.", nameof(turns));
        }
    }

    private static void ValidateTurnPairing(
        ModelTurn turn,
        Queue<string> pendingCallIds,
        HashSet<string> callIds)
    {
        if (turn.Role == "assistant" && turn.ToolCalls.Count > 0)
        {
            if (pendingCallIds.Count > 0)
            {
                throw new ArgumentException("Model history contains interleaved tool call batches.");
            }

            foreach (var call in turn.ToolCalls)
            {
                if (call.Id is null || !callIds.Add(call.Id))
                {
                    throw new ArgumentException("Model history contains a duplicate tool call ID.");
                }

                pendingCallIds.Enqueue(call.Id);
            }

            return;
        }

        if (turn.Role == "tool")
        {
            if (pendingCallIds.Count == 0
                || !string.Equals(pendingCallIds.Dequeue(), turn.ToolCallId, StringComparison.Ordinal))
            {
                throw new ArgumentException("Model history contains an unmatched tool result.");
            }

            return;
        }

        if (pendingCallIds.Count > 0)
        {
            throw new ArgumentException("Model history contains an unmatched tool call batch.");
        }
    }
}
