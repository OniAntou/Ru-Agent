using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RuAgent.Harness.Core.Prompting;

public sealed record ProviderContinuation
{
    public const int MaximumBytes = 128 * 1024;
    public const int MaximumEnvelopeBytes = MaximumBytes + 2048;
    public const int MaximumFunctionArgumentsBytes = 64 * 1024;
    private const int MaximumIdentityChars = 256;
    private const int MaximumToolNameChars = 128;
    private const int MaximumItems = 32;
    private readonly JsonObject[] _outputItems;
    private readonly byte[] _serializedOutputItems;

    public ProviderContinuation(string provider, string protocol, string model, int version, IReadOnlyList<JsonObject> outputItems)
    {
        if (provider is not ("openai" or "anthropic")
            || (provider == "openai" && protocol != "openai_responses")
            || (provider == "anthropic" && protocol != "anthropic_messages")
            || string.IsNullOrWhiteSpace(model) || model.Length > 256 || version != 1)
            throw new ArgumentException("Provider continuation identity is invalid.");
        ArgumentNullException.ThrowIfNull(outputItems);
        if (outputItems.Count is < 1 or > MaximumItems || outputItems.Any(item => item is null))
            throw new ArgumentException("Provider continuation item count is invalid.", nameof(outputItems));
        var cloned = outputItems.Select(item => (JsonObject)item.DeepClone()).ToArray();
        ValidateOutputItems(protocol, cloned);
        var bytes = Encoding.UTF8.GetBytes(new JsonArray(cloned).ToJsonString());
        if (bytes.Length > MaximumBytes) throw new ArgumentException("Provider continuation exceeds its byte cap.", nameof(outputItems));
        Provider = provider;
        Protocol = protocol;
        Model = model;
        Version = version;
        _outputItems = cloned;
        _serializedOutputItems = bytes;
    }

    public string Provider { get; }
    public string Protocol { get; }
    public string Model { get; }
    public int Version { get; }
    public IReadOnlyList<JsonObject> OutputItems => Array.AsReadOnly(_outputItems.Select(item => (JsonObject)item.DeepClone()).ToArray());
    public ReadOnlyMemory<byte> SerializedOutputItems => _serializedOutputItems.ToArray();

    private static void ValidateOutputItems(string protocol, IReadOnlyList<JsonObject> items)
    {
        if (protocol == "anthropic_messages")
        {
            ValidateAnthropicBlocks(items);
            return;
        }
        var itemIds = new HashSet<string>(StringComparer.Ordinal);
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var type = item["type"]?.GetValue<string>();
            var allowed = type switch
            {
                "reasoning" => new[] { "type", "id", "encrypted_content", "summary", "status" },
                "function_call" => new[] { "type", "id", "call_id", "name", "arguments", "status" },
                _ => Array.Empty<string>(),
            };
            if (allowed.Length == 0 || item.Any(property => !allowed.Contains(property.Key, StringComparer.Ordinal)))
                throw new ArgumentException("Provider continuation contains an unsupported output item.", nameof(items));
            var id = item["id"]?.GetValue<string>();
            if (id is null || id.Length > MaximumIdentityChars || !itemIds.Add(id))
                throw new ArgumentException("Provider continuation item identity is invalid.", nameof(items));
            if (type == "reasoning")
            {
                if (item["encrypted_content"]?.GetValueKind() is not (JsonValueKind.String or JsonValueKind.Null))
                    throw new ArgumentException("Provider reasoning continuation is invalid.", nameof(items));
                continue;
            }
            var callId = item["call_id"]?.GetValue<string>();
            var name = item["name"]?.GetValue<string>();
            var arguments = item["arguments"]?.GetValue<string>();
            if (callId is null || !ProviderCallIdContract.IsValid(callId) || !callIds.Add(callId)
                || name is null || name.Length > MaximumToolNameChars || arguments is null || Encoding.UTF8.GetByteCount(arguments) > MaximumFunctionArgumentsBytes)
                throw new ArgumentException("Provider function continuation identity is invalid.", nameof(items));
            try
            {
                if (JsonNode.Parse(arguments) is not JsonObject) throw new JsonException();
            }
            catch (JsonException error)
            {
                throw new ArgumentException("Provider function continuation arguments are invalid.", nameof(items), error);
            }
        }
        if (callIds.Count == 0) throw new ArgumentException("Provider continuation has no function call.", nameof(items));
    }

    private static void ValidateAnthropicBlocks(IReadOnlyList<JsonObject> items)
    {
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var type = item["type"]?.GetValue<string>();
            var expected = type switch
            {
                "text" => new[] { "type", "text" },
                "thinking" => new[] { "type", "thinking", "signature" },
                "redacted_thinking" => new[] { "type", "data" },
                "tool_use" => new[] { "type", "id", "name", "input" },
                _ => Array.Empty<string>(),
            };
            if (expected.Length == 0 || item.Count != expected.Length
                || item.Any(property => !expected.Contains(property.Key, StringComparer.Ordinal)))
                throw new ArgumentException("Anthropic continuation block shape is invalid.", nameof(items));
            if (type == "text")
            {
                if (item["text"]?.GetValue<string>() is not { Length: <= 64_000 } text || text.Contains('\0'))
                    throw new ArgumentException("Anthropic text block is invalid.", nameof(items));
            }
            if (type == "thinking" && (item["thinking"]?.GetValue<string>() is not { Length: <= 64_000 }
                || item["thinking"]!.GetValue<string>().Contains('\0')
                || item["signature"]?.GetValue<string>() is not { Length: > 0 and <= 16_384 }
                || item["signature"]!.GetValue<string>().Contains('\0')))
                throw new ArgumentException("Anthropic thinking block is invalid.", nameof(items));
            if (type == "redacted_thinking" && (item["data"]?.GetValue<string>() is not { Length: > 0 and <= 32_768 }
                || item["data"]!.GetValue<string>().Contains('\0')))
                throw new ArgumentException("Anthropic redacted block is invalid.", nameof(items));
            if (type == "tool_use")
            {
                var id = item["id"]?.GetValue<string>();
                var name = item["name"]?.GetValue<string>();
                if (id is null || !ProviderCallIdContract.IsValid(id) || !callIds.Add(id)
                    || name is null || name.Length > MaximumToolNameChars
                    || item["input"] is not JsonObject input
                    || Encoding.UTF8.GetByteCount(input.ToJsonString()) > MaximumFunctionArgumentsBytes)
                    throw new ArgumentException("Anthropic tool block identity is invalid.", nameof(items));
            }
        }
        if (callIds.Count == 0) throw new ArgumentException("Anthropic continuation has no tool call.", nameof(items));
    }
}
