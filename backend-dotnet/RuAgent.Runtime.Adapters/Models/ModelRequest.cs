using System.Text;
using System.Text.Json.Nodes;

namespace RuAgent.Runtime.Adapters.Models;

public sealed record ModelRequest
{
    private const int MaxModelMessages = ModelInputLimits.MaxMessages;
    private const int MaxModelMessagesBytes = ModelInputLimits.MaxHistoryBytes;
    private const int MaxAnthropicContentBlocks = 64;
    private const int MaxNativeArgumentsBytes = 64 * 1024;
    private const int MaximumProviderHeaders = 8;
    private const int MaximumProviderHeaderValueChars = 1024;
    public const int MaximumSerializedBodyBytes = ModelInputLimits.MaxRequestBytes;

    private static readonly IReadOnlyDictionary<string, string> EmptyProviderHeaders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Headers the adapter itself owns; a provider cannot replace them.</summary>
    private static readonly HashSet<string> ReservedProviderHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Host", "Content-Length", "Content-Type", "Transfer-Encoding", "Connection",
    };

    /// <summary>The settings field that asks for the provider's hosted web search (ADR 0033).</summary>
    public const string ProviderWebSearchSetting = "provider_web_search";
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
        IReadOnlyList<JsonObject>? responsesInputItems = null,
        bool nativeToolCalling = false,
        IReadOnlyList<JsonObject>? anthropicMessages = null,
        bool useMaxCompletionTokens = false,
        string? preparedBodyJson = null,
        string? runtimeContext = null,
        bool promptInHistory = false,
        bool replayChatReasoning = false,
        bool? acceptsImageInput = null,
        IReadOnlyDictionary<string, string>? providerHeaders = null)
    {
        if (string.IsNullOrWhiteSpace(model) || model.Length > 256 || model.Contains('\0'))
        {
            throw new ArgumentException("Model is invalid.", nameof(model));
        }

        if (systemPrompt is null || systemPrompt.Length > ModelInputLimits.MaxSystemPromptChars || systemPrompt.Contains('\0'))
        {
            throw new ArgumentException("System prompt exceeds the adapter cap.", nameof(systemPrompt));
        }

        if (images is null || images.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > ModelInputLimits.MaxPromptChars || prompt.Contains('\0'))
            {
                throw new ArgumentException("Prompt is invalid.", nameof(prompt));
            }
        }
        else
        {
            if (prompt is null || prompt.Length > ModelInputLimits.MaxPromptChars || prompt.Contains('\0'))
            {
                throw new ArgumentException("Prompt is invalid.", nameof(prompt));
            }
        }

        ArgumentNullException.ThrowIfNull(turns);
        if (promptInHistory && (!nativeToolCalling || images is { Count: > 0 }))
            throw new ArgumentException("History-owned prompts require native text requests.", nameof(promptInHistory));
        if (runtimeContext is not null && (runtimeContext.Length > ModelInputLimits.MaxSystemPromptChars
            || runtimeContext.Contains('\0')))
            throw new ArgumentException("Runtime context exceeds the adapter cap.", nameof(runtimeContext));
        if (turns.Count > MaxModelMessages || turns.Any(turn => turn is null))
        {
            throw new ArgumentException("Model turns exceed the adapter cap.", nameof(turns));
        }

        ValidateModelHistory(turns);
        // Tool images have their own bound (ADR 0026); the history byte cap measures text,
        // so the projected histories subtract the image data they embed.
        var historyImages = turns.SelectMany(turn => turn.Images).ToArray();
        if (historyImages.Length > RuAgent.Protocol.ToolOutputImage.MaximumInContext)
            throw new ArgumentException("Model history carries too many images.", nameof(turns));
        var historyImageChars = historyImages.Sum(image => (long)image.Base64Data.Length);
        if (responsesInputItems is { Count: > MaxModelMessages }
            || responsesInputItems?.Any(item => !IsSupportedResponsesInputItem(item)) == true)
            throw new ArgumentException("Responses input history is invalid.", nameof(responsesInputItems));
        var historyBytes = responsesInputItems is { Count: > 0 }
            ? MeasureResponsesInputBytes(responsesInputItems) - historyImageChars
            : MeasureModelMessagesBytes(turns);
        if (historyBytes > MaxModelMessagesBytes)
            throw new ArgumentException("Model history exceeds the adapter byte cap.", nameof(responsesInputItems));
        if (anthropicMessages is { Count: > MaxModelMessages }
            || anthropicMessages?.Any(message => !IsSupportedAnthropicMessage(message)) == true
            || MeasureJsonArrayBytes(anthropicMessages) - historyImageChars > MaxModelMessagesBytes)
            throw new ArgumentException("Anthropic history exceeds the adapter cap.", nameof(anthropicMessages));
        if (anthropicMessages is { Count: > 0 } && responsesInputItems is { Count: > 0 })
            throw new ArgumentException("Provider history cannot mix protocol shapes.", nameof(anthropicMessages));

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
        AcceptsImageInput = ModelImage.IsVisionModel(model, acceptsImageInput);
        ProviderHeaders = ValidateProviderHeaders(providerHeaders);
        SystemPrompt = systemPrompt;
        RuntimeContext = runtimeContext;
        PromptInHistory = promptInHistory;
        ReplayChatReasoning = replayChatReasoning;
        Prompt = prompt;
        Turns = Array.AsReadOnly(turns.ToArray());
        Settings = (JsonObject)settings.DeepClone();
        Endpoint = endpoint;
        Credential = credential ?? throw new ArgumentNullException(nameof(credential));
        MaximumOutputTokens = maximumOutputTokens;
        UseMaxCompletionTokens = useMaxCompletionTokens;
        if (preparedBodyJson is not null
            && Encoding.UTF8.GetByteCount(preparedBodyJson) > MaximumSerializedBodyBytes)
            throw new ArgumentException("Serialized provider body exceeds the adapter cap.", nameof(preparedBodyJson));
        PreparedBodyJson = preparedBodyJson;
        Images = Array.AsReadOnly(images is null ? Array.Empty<ModelImage>() : images.ToArray());
        ResponsesInputItems = Array.AsReadOnly(responsesInputItems?.Select(item => (JsonObject)item.DeepClone()).ToArray() ?? Array.Empty<JsonObject>());
        NativeToolCalling = nativeToolCalling;
        AnthropicMessages = Array.AsReadOnly(anthropicMessages?.Select(item => (JsonObject)item.DeepClone()).ToArray() ?? Array.Empty<JsonObject>());
    }

    public string Model { get; }

    public bool AcceptsImageInput { get; }

    /// <summary>
    /// Provider-mandated request headers, already bounded and validated. The Host
    /// decides which provider needs which header; the adapter only applies them.
    /// </summary>
    public IReadOnlyDictionary<string, string> ProviderHeaders { get; }

    public string SystemPrompt { get; }

    /// <summary>Bounded untrusted runtime data placed after history, before the current prompt.</summary>
    public string? RuntimeContext { get; }
    public bool PromptInHistory { get; }
    public bool ReplayChatReasoning { get; }

    public string Prompt { get; }

    public IReadOnlyList<ModelTurn> Turns { get; }

    public JsonObject Settings { get; }

    public Uri Endpoint { get; }

    public string Credential { get; }

    public int? MaximumOutputTokens { get; }

    public bool UseMaxCompletionTokens { get; }

    public string? PreparedBodyJson { get; }

    public ModelRequest WithPreparedBodyJson(string body) => new(
        Model, SystemPrompt, Prompt, Turns, Settings, Endpoint, Credential, MaximumOutputTokens,
        Images, ResponsesInputItems, NativeToolCalling, AnthropicMessages, UseMaxCompletionTokens,
        body, RuntimeContext, PromptInHistory, ReplayChatReasoning, AcceptsImageInput, ProviderHeaders);

    public IReadOnlyList<ModelImage> Images { get; }

    /// <summary>Every image this request sends: the user's attachments and the history's tool images.</summary>
    public IEnumerable<ModelImage> AllImages => Images.Concat(Turns.SelectMany(turn => turn.Images));

    public IReadOnlyList<JsonObject> ResponsesInputItems { get; }

    public bool NativeToolCalling { get; }

    /// <summary>Whether this request declares the provider's hosted web search tool.</summary>
    public bool ProviderWebSearch => NativeToolCalling
        && Settings[ProviderWebSearchSetting] is JsonValue value && value.TryGetValue<bool>(out var enabled) && enabled;

    public IReadOnlyList<JsonObject> AnthropicMessages { get; }

    /// <summary>
    /// Provider-mandated headers are a closed, bounded set: an HTTP token name and a
    /// value with no control characters. The credential, framing and content headers
    /// stay owned by the adapter and cannot be replaced from here.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ValidateProviderHeaders(
        IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            return EmptyProviderHeaders;
        }

        if (headers.Count > MaximumProviderHeaders)
        {
            throw new ArgumentException("Provider headers exceed the adapter cap.", nameof(headers));
        }

        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 64
                || name.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-')))
            {
                throw new ArgumentException("Provider header name is invalid.", nameof(headers));
            }

            if (ReservedProviderHeaders.Contains(name))
            {
                throw new ArgumentException(
                    "Provider header would override the adapter's own request.", nameof(headers));
            }

            if (value is null || value.Length > MaximumProviderHeaderValueChars || value.Any(char.IsControl))
            {
                throw new ArgumentException("Provider header value is invalid.", nameof(headers));
            }

            copy[name] = value;
        }

        return copy;
    }

    public static long MeasureJsonArrayBytes(IReadOnlyList<JsonObject>? items)
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

    private static bool IsSupportedAnthropicMessage(JsonObject? message)
    {
        if (message is null || message.Count != 2
            || !message.Select(property => property.Key).Order(StringComparer.Ordinal).SequenceEqual(["content", "role"], StringComparer.Ordinal)
            || !TryGetString(message["role"], out var role)
            || role is not ("user" or "assistant")
            || message["content"] is not JsonArray content
            || content.Count is < 1 or > MaxAnthropicContentBlocks)
            return false;

        var hasToolResult = false;
        foreach (var node in content)
        {
            if (node is not JsonObject block || !IsSupportedAnthropicBlock(role, block, out var isToolResult)) return false;
            hasToolResult |= isToolResult;
        }
        return role != "assistant" || !hasToolResult;
    }

    private static bool IsSupportedAnthropicBlock(string role, JsonObject block, out bool isToolResult)
    {
        isToolResult = false;
        if (!TryGetString(block["type"], out var type)) return false;
        // Provider-hosted search content (ADR 0033) is replayed unchanged on assistant turns.
        if (RuAgent.Harness.Core.Prompting.ProviderSearchContent.IsAnthropicSearchBlock(block))
            return role == "assistant" && RuAgent.Harness.Core.Prompting.ProviderSearchContent.IsValidAnthropicSearchBlock(block);
        var cited = type == "text" && role == "assistant" && block.ContainsKey("citations");
        if (cited && !RuAgent.Harness.Core.Prompting.ProviderSearchContent.IsValidCitations(block["citations"])) return false;
        var fields = type switch
        {
            "text" when cited => new[] { "citations", "text", "type" },
            "text" when role == "assistant" || role == "user" => new[] { "text", "type" },
            "thinking" when role == "assistant" => new[] { "signature", "thinking", "type" },
            "redacted_thinking" when role == "assistant" => new[] { "data", "type" },
            "tool_use" when role == "assistant" => new[] { "id", "input", "name", "type" },
            "tool_result" when role == "user" => new[] { "content", "is_error", "tool_use_id", "type" },
            _ => Array.Empty<string>(),
        };
        if (fields.Length == 0 || !block.Select(property => property.Key).Order(StringComparer.Ordinal).SequenceEqual(fields, StringComparer.Ordinal))
            return false;
        if (type == "text") return TryGetBoundedString(block["text"], ModelInputLimits.MaxToolResultChars);
        if (type == "thinking") return TryGetBoundedString(block["thinking"], ModelInputLimits.MaxToolResultChars) && TryGetBoundedString(block["signature"], 16_384);
        if (type == "redacted_thinking") return TryGetBoundedString(block["data"], 32_768);
        if (type == "tool_use")
            return TryGetBoundedString(block["id"], 256) && TryGetBoundedString(block["name"], 128)
                && block["input"] is JsonObject input && Encoding.UTF8.GetByteCount(input.ToJsonString()) <= MaxNativeArgumentsBytes;
        isToolResult = true;
        return TryGetBoundedString(block["tool_use_id"], 256)
            && (TryGetBoundedString(block["content"], ModelInputLimits.MaxToolResultChars)
                || IsSupportedToolResultContent(block["content"]))
            && block["is_error"] is JsonValue error && error.TryGetValue<bool>(out _);
    }

    /// <summary>A tool result with an image (ADR 0026): one text block, then base64 image blocks.</summary>
    private static bool IsSupportedToolResultContent(JsonNode? node)
    {
        if (node is not JsonArray content || content.Count < 2
            || content[0] is not JsonObject text
            || !IsSupportedAnthropicBlock("user", text, out _)
            || text["type"]!.GetValue<string>() != "text")
            return false;
        for (var index = 1; index < content.Count; index++)
        {
            if (content[index] is not JsonObject image
                || !image.Select(property => property.Key).Order(StringComparer.Ordinal).SequenceEqual(["source", "type"], StringComparer.Ordinal)
                || !TryGetString(image["type"], out var type) || type != "image"
                || image["source"] is not JsonObject source
                || !source.Select(property => property.Key).Order(StringComparer.Ordinal).SequenceEqual(["data", "media_type", "type"], StringComparer.Ordinal)
                || !TryGetString(source["type"], out var sourceType) || sourceType != "base64"
                || !TryGetString(source["media_type"], out var mediaType) || !RuAgent.Protocol.ToolOutputImage.IsSupportedMediaType(mediaType)
                || !TryGetString(source["data"], out var data) || data.Length is 0 or > ModelImage.MaximumBase64Chars)
                return false;
        }

        return true;
    }

    private static bool TryGetString(JsonNode? node, out string value)
    {
        if (node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var stringValue))
        {
            value = stringValue;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static bool TryGetBoundedString(JsonNode? node, int maximum) =>
        TryGetString(node, out var value) && value.Length <= maximum && !value.Contains('\0');

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
        if (type == "web_search_call") return RuAgent.Harness.Core.Prompting.ProviderSearchContent.IsValidResponsesSearchCall(item);
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
            // The neutral wire shape intentionally omits Responses-only metadata.
            // Include it in admission bounds even for direct adapter projection.
            if (messages[index].ResponsesMessages is { } responseMessages)
                bytes = checked(bytes + Encoding.UTF8.GetByteCount(new JsonArray(
                    responseMessages.Messages.Select(message => message.DeepClone()).ToArray()).ToJsonString()));
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
