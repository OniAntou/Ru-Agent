using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Prompting;

namespace RuAgent.Runtime.Adapters.Models;

internal sealed class AnthropicNativeContentAssembly
{
    private const int MaximumBlocks = 32;
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
    private int _inputByteCount;
    private int _serializedBlockBytes;

    public JsonObject? CurrentBlock { get; private set; }
    public string Text => _text.ToString();
    public IReadOnlyList<ModelToolFragment> ToolFragments => _toolFragments;
    public bool HasTools => _toolFragments.Count > 0;

    public void BeginBlock(JsonObject source, string type, bool isNative)
    {
        if (_blocks.Count >= MaximumBlocks) throw new ModelProviderException("response_too_large", false);
        CurrentBlock = CreateBlock(source, type, isNative);
        if (isNative) _blocks.Add(CurrentBlock);
        _text.Clear();
        _signature.Clear();
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
        AppendBounded(_text, text);
        if (isNative) CurrentBlock!["thinking"] = _text.ToString();
        return text;
    }

    public void AppendSignature(string signature, bool isNative)
    {
        if (!isNative) return;
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
        _serializedBlockBytes += Encoding.UTF8.GetByteCount(CurrentBlock.ToJsonString()) + (_serializedBlockBytes == 0 ? 0 : 1);
        if (_serializedBlockBytes + 2 > ProviderContinuation.MaximumBytes)
            throw new ModelProviderException("response_too_large", false);
    }

    public ProviderContinuation? CreateContinuation(string model)
    {
        if (!HasTools) return null;
        try
        {
            return new ProviderContinuation("anthropic", "anthropic_messages", model, 1, _blocks);
        }
        catch (ArgumentException error)
        {
            throw new ModelProviderException("malformed_payload", false, error);
        }
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
            _ => throw new ModelProviderException("malformed_payload", false),
        };
    }

    private static JsonObject CreateText(JsonObject source)
    {
        if (source.Count != 2 || ReadString(source["text"]) is not { Length: <= MaximumTextChars } text || text.Contains('\0'))
            throw new ModelProviderException("malformed_payload", false);
        return new JsonObject { ["type"] = "text", ["text"] = text };
    }

    private static JsonObject CreateThinking(JsonObject source)
    {
        if (source.Count != 2 || ReadString(source["thinking"]) is not { Length: <= MaximumTextChars } text || text.Contains('\0'))
            throw new ModelProviderException("malformed_payload", false);
        return new JsonObject { ["type"] = "thinking", ["thinking"] = text };
    }

    private static JsonObject CreateRedacted(JsonObject source)
    {
        if (source.Count != 2 || ReadString(source["data"]) is not { Length: > 0 and <= 32_768 } data || data.Contains('\0'))
            throw new ModelProviderException("malformed_payload", false);
        return new JsonObject { ["type"] = "redacted_thinking", ["data"] = data };
    }

    private JsonObject CreateToolUse(JsonObject source)
    {
        var id = ReadString(source["id"]);
        var name = ReadString(source["name"]);
        var input = ReadObject(source["input"]);
        if (source.Count != 4 || id is null || id.Length > 256 || !_toolIds.Add(id)
            || name is null || name.Length > MaximumToolNameChars || input is null || input.Count > 0)
            throw new ModelProviderException("malformed_payload", false);
        return new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = new JsonObject() };
    }

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
