using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuAgent.Harness.Core;

public static class ToolArgumentsCodec
{
    public static byte[] Serialize(string providerCallId, string toolId, string toolVersion, JsonElement arguments)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("provider_call_id", providerCallId);
            writer.WriteString("tool_id", toolId);
            writer.WriteString("tool_version", toolVersion);
            writer.WritePropertyName("arguments");
            arguments.WriteTo(writer);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    public static ToolArgumentsEnvelope Deserialize(ReadOnlyMemory<byte> json)
    {
        using var document = JsonDocument.Parse(json);
        return new ToolArgumentsEnvelope(document.RootElement.Clone());
    }
}

public sealed record ToolArgumentsEnvelope(JsonElement Root)
{
    public bool IsObject => Root.ValueKind == JsonValueKind.Object;
    public string? ProviderCallId => ReadString("provider_call_id");
    public string? ToolId => ReadString("tool_id");
    public string? ToolVersion => ReadString("tool_version");
    public JsonElement Arguments => IsObject && Root.TryGetProperty("arguments", out var arguments)
        ? arguments : default;
    public bool HasObjectArguments => Arguments.ValueKind == JsonValueKind.Object;

    public bool Matches(string providerCallId, string toolId, string toolVersion, JsonElement arguments) =>
        IsObject && HasObjectArguments
        && !string.IsNullOrWhiteSpace(ProviderCallId)
        && !string.IsNullOrWhiteSpace(ToolId)
        && !string.IsNullOrWhiteSpace(ToolVersion)
        && string.Equals(ProviderCallId, providerCallId, StringComparison.Ordinal)
        && string.Equals(ToolId, toolId, StringComparison.Ordinal)
        && string.Equals(ToolVersion, toolVersion, StringComparison.Ordinal)
        && JsonNode.DeepEquals(JsonNode.Parse(Arguments.GetRawText()), JsonNode.Parse(arguments.GetRawText()));

    private string? ReadString(string name) => IsObject
        && Root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
