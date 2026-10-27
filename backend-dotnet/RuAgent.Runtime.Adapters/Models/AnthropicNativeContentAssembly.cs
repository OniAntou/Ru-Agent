using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Prompting;

namespace RuAgent.Runtime.Adapters.Models;

internal sealed class AnthropicNativeContentAssembly
{
    // A cited answer arrives as many short text blocks, one per citation span, so the
    // stream allows more blocks than a continuation keeps; CreateContinuation degrades.
    private const int MaximumBlocks = 256;
    private const int MaximumNativeBytes = 4 * 1024 * 1024;
    private const int MaximumInputBytes = 64 * 1024;
    private const int MaximumTextChars = 64 * 1024;
    private const int MaximumSignatureChars = 16_384;
    private const int MaximumToolNameChars = 128;
    private readonly List<JsonObject> _blocks = new();
    private readonly HashSet<string> _toolIds = new(StringComparer.Ordinal);
    private readonly List<ModelToolFragment> _toolFragments = new();
    private readonly StringBuilder _text = new();
    private readonly StringBuilder _signature = new();
    private readonly StringBuilder _input = new();
    private readonly List<JsonObject> _citations = new();
    private int _inputByteCount;
    private int _serializedBlockBytes;
    private bool _signatureStarted;

    public JsonObject? CurrentBlock { get; private set; }
    public string Text => _text.ToString();
    public IReadOnlyList<ModelToolFragment> ToolFragments => _toolFragments;
    public bool HasTools => _toolFragments.Count > 0;

    /// <summary>Every web search citation in this response, in arrival order (ADR 0033).</summary>
    public IReadOnlyList<JsonObject> Citations => _citations;

    /// <summary>The native blocks so far, as the assistant content of a resent paused turn.</summary>
    public JsonArray ContentForResend() => new(_blocks.Select(block => (JsonNode)block.DeepClone()).ToArray());

    /// <summary>Records a citation on the current text block; the block keeps it unchanged.</summary>
    public void AppendCitation(JsonObject citation, bool isNative)
    {
        if (ReadString(CurrentBlock!["type"]) != "text") throw new ModelProviderException("malformed_payload", false);
        if (!ProviderSearchContent.IsValidCitations(new JsonArray(citation.DeepClone())))
            throw new ModelProviderException("malformed_payload", false);
        if (_citations.Count < ProviderSearchContent.MaximumCitations * 4) _citations.Add((JsonObject)citation.DeepClone());
        if (!isNative) return;
        if (CurrentBlock["citations"] is not JsonArray list)
        {
            list = new JsonArray();
            CurrentBlock["citations"] = list;
        }
        if (list.Count >= ProviderSearchContent.MaximumCitations) throw new ModelProviderException("response_too_large", false);
        list.Add(citation.DeepClone());
    }

    public void BeginBlock(JsonObject source, string type, bool isNative)
    {
        if (_blocks.Count >= MaximumBlocks) throw new ModelProviderException("response_too_large", false);
        CurrentBlock = CreateBlock(source, type, isNative);
        if (isNative) _blocks.Add(CurrentBlock);
        _text.Clear();
        _signature.Clear();
        _signatureStarted = false;
        _input.Clear();
        _inputByteCount = 0;
        if (type == "text") _text.Append(ReadString(CurrentBlock["text"]));
        if (type == "thinking") _text.Append(ReadString(CurrentBlock["thinking"]));
    }

    public string AppendText(string text, bool isNative)
    {
        AppendBounded(_text, text);
        if (isNative) CurrentBlock!["text"] = _text.ToString();
        return text;
    }

    public string AppendThinking(string text, bool isNative)
    {
        if (isNative && _signatureStarted) throw new ModelProviderException("malformed_payload", false);
        AppendBounded(_text, text);
        if (isNative) CurrentBlock!["thinking"] = _text.ToString();
        return text;
    }

    public void AppendSignature(string signature, bool isNative)
    {
        if (!isNative) return;
        _signatureStarted = true;
        _signature.Append(signature);
        if (_signature.Length > MaximumSignatureChars) throw new ModelProviderException("response_too_large", false);
        CurrentBlock!["signature"] = _signature.ToString();
    }

    public void AppendInputJson(string partialJson)
    {
        _input.Append(partialJson);
        _inputByteCount += Encoding.UTF8.GetByteCount(partialJson);
        if (_inputByteCount > MaximumInputBytes)
            throw new ModelProviderException("response_too_large", false);
    }

    public void CompleteBlock(bool isNative)
    {
        if (!isNative) return;
        var type = ReadString(CurrentBlock!["type"]);
        if (type == "thinking" && _signature.Length == 0)
            throw new ModelProviderException("malformed_payload", false);
        if (type == "tool_use") CompleteToolUse();
        if (type == "server_tool_use") CurrentBlock["input"] = ParseInput();
        if (ProviderSearchContent.IsAnthropicSearchBlock(CurrentBlock) && !ProviderSearchContent.IsValidAnthropicSearchBlock(CurrentBlock))
            throw new ModelProviderException("malformed_payload", false);
        _serializedBlockBytes += Encoding.UTF8.GetByteCount(CurrentBlock.ToJsonString()) + (_serializedBlockBytes == 0 ? 0 : 1);
        if (_serializedBlockBytes + 2 > MaximumNativeBytes)
            throw new ModelProviderException("response_too_large", false);
    }

    public ProviderContinuation? CreateContinuation(string model)
    {
        if (!HasTools) return null;
        try
        {
            return new ProviderContinuation("anthropic", "anthropic_messages", model, 1, _blocks);
        }
        catch (ArgumentException) when (_blocks.Any(block => ProviderSearchContent.IsAnthropicSearchBlock(block) || block.ContainsKey("citations")))
        {
            // Search results and their citations are what overflows a continuation's bounds.
            // Keep the step replayable without them rather than failing the run (ADR 0033).
            try
            {
                return new ProviderContinuation("anthropic", "anthropic_messages", model, 1,
                    MergeAdjacentText(ProviderSearchContent.StripAnthropic(_blocks)));
            }
            catch (ArgumentException error)
            {
                throw new ModelProviderException("provider_continuation_too_large", false, error);
            }
        }
        catch (ArgumentException error)
        {
            // A continuation too large to keep is a size bound, not a broken stream.
            throw new ModelProviderException(
                _serializedBlockBytes + 2 > ProviderContinuation.MaximumBytes ? "response_too_large" : "malformed_payload",
                false, error);
        }
    }

    private static IReadOnlyList<JsonObject> MergeAdjacentText(IReadOnlyList<JsonObject> blocks)
    {
        var merged = new List<JsonObject>();
        foreach (var block in blocks)
        {
            if (ReadString(block["type"]) == "text" && merged.Count > 0 && ReadString(merged[^1]["type"]) == "text")
            {
                merged[^1] = new JsonObject { ["type"] = "text", ["text"] = ReadString(merged[^1]["text"]) + ReadString(block["text"]) };
                continue;
            }
            merged.Add(block);
        }
        return merged;
    }

    private void CompleteToolUse()
    {
        var arguments = ParseInput();
        CurrentBlock!["input"] = arguments.DeepClone();
        _toolFragments.Add(new ModelToolFragment(
            ReadString(CurrentBlock["id"]), ReadString(CurrentBlock["name"])!, arguments.ToJsonString()));
    }

    private JsonObject ParseInput()
    {
        if (_input.Length == 0) return (JsonObject)ReadObject(CurrentBlock!["input"])!.DeepClone();
        try
        {
            return JsonNode.Parse(_input.ToString()) as JsonObject ?? throw new JsonException();
        }
        catch (JsonException error)
        {
            throw new ModelProviderException("malformed_payload", false, error);
        }
    }

    private JsonObject CreateBlock(JsonObject source, string type, bool isNative)
    {
        if (!isNative) return new JsonObject { ["type"] = type };
        return type switch
        {
            "text" => CreateText(source),
            "thinking" => CreateThinking(source),
            "redacted_thinking" => CreateRedacted(source),
            "tool_use" => CreateToolUse(source),
            "server_tool_use" => CreateServerToolUse(source),
            "web_search_tool_result" => CreateSearchResult(source),
            _ => throw new ModelProviderException("malformed_payload", false),
        };
    }

    private static JsonObject CreateText(JsonObject source)
    {
        // Only the required fields are validated: a provider that adds a new
        // sibling field to a valid block (for example citations) must not fail
        // the step, matching how unknown stream events are ignored.
        if (ReadString(source["text"]) is not { Length: <= MaximumTextChars } text || text.Contains('\0'))
            throw new ModelProviderException("malformed_payload", false);
        return new JsonObject { ["type"] = "text", ["text"] = text };
    }

    private static JsonObject CreateThinking(JsonObject source)
    {
        if (ReadString(source["thinking"]) is not { Length: <= MaximumTextChars } text || text.Contains('\0'))
            throw new ModelProviderException("malformed_payload", false);
        return new JsonObject { ["type"] = "thinking", ["thinking"] = text };
    }

    private static JsonObject CreateRedacted(JsonObject source)
    {
        if (ReadString(source["data"]) is not { Length: > 0 and <= 32_768 } data || data.Contains('\0'))
            throw new ModelProviderException("malformed_payload", false);
        return new JsonObject { ["type"] = "redacted_thinking", ["data"] = data };
    }

    private JsonObject CreateToolUse(JsonObject source)
    {
        var id = ReadString(source["id"]);
        var name = ReadString(source["name"]);
        var input = ReadObject(source["input"]);
        if (id is null || id.Length > 256 || !_toolIds.Add(id)
            || name is null || name.Length > MaximumToolNameChars || input is null || input.Count > 0)
            throw new ModelProviderException("malformed_payload", false);
        return new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = new JsonObject() };
    }

    private JsonObject CreateServerToolUse(JsonObject source)
    {
        var id = ReadString(source["id"]);
        var input = ReadObject(source["input"]);
        if (id is null || id.Length > 256 || !_toolIds.Add(id) || ReadString(source["name"]) != ProviderSearchContent.AnthropicToolName)
            throw new ModelProviderException("malformed_payload", false);
        var block = (JsonObject)source.DeepClone();
        block["input"] = input is null ? new JsonObject() : (JsonObject)input.DeepClone();
        return block;
    }

    // A search result arrives complete in its start event; it travels back unchanged.
    private static JsonObject CreateSearchResult(JsonObject source) => (JsonObject)source.DeepClone();

    private static void AppendBounded(StringBuilder target, string value)
    {
        if (value.Contains('\0')) throw new ModelProviderException("malformed_payload", false);
        target.Append(value);
        if (target.Length > MaximumTextChars) throw new ModelProviderException("response_too_large", false);
    }

    private static string? ReadString(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        throw new ModelProviderException("malformed_payload", false);
    }

    private static JsonObject? ReadObject(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonObject value) return value;
        throw new ModelProviderException("malformed_payload", false);
    }
}
