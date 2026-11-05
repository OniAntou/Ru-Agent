using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuAgent.Harness.Core.Context;

/// <summary>Stable model-only excerpts. Original durable receipts and structured cursors stay intact.</summary>
public static class ToolResultExcerpt
{
    public const int ThresholdChars = 8_192;
    private const int HeadChars = 4_096;
    private const int TailChars = 1_024;
    private const int Sha256HexChars = 64;

    // File tools return content hashes for durable evidence (session work state reads them
    // from the stored receipt). The model never passes them back: the Broker computes the
    // expected hash itself. Each one cost ~40 tokens of every file read and write.
    private static readonly string[] ReceiptOnlyHashFields = ["sha256", "before_sha256", "after_sha256"];

    private static readonly JsonSerializerOptions ModelJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Project(string content)
    {
        // Parse only when a hash field can be present or the excerpt rule needs to know the
        // shape; most small results (terminal output, listings) skip JSON parsing entirely.
        var mayCarryHash = content.Contains("sha256\":\"", StringComparison.Ordinal);
        if (!mayCarryHash && content.Length <= ThresholdChars) return content;
        if (TryParseStructured(content) is { } structured) return WithoutReceiptHashes(content, structured);
        if (content.Length <= ThresholdChars) return content;
        var head = HeadChars;
        var tail = content.Length - TailChars;
        if (char.IsHighSurrogate(content[head - 1])) head--;
        if (char.IsLowSurrogate(content[tail])) tail++;
        return content[..head] + "\n[tool result middle omitted; original durable receipt retained; read the source or output cursor for details]\n"
            + ToolDiagnosticExcerpt.Select(content, head, tail) + content[tail..];
    }

    /// <summary>Removes top-level content hashes; anything else, including the original text, is kept as is.</summary>
    private static string WithoutReceiptHashes(string content, JsonNode structured)
    {
        if (structured is not JsonObject root) return content;
        var removed = false;
        foreach (var field in ReceiptOnlyHashFields)
        {
            if (root[field] is JsonValue value && value.TryGetValue<string>(out var hash) && IsSha256Hex(hash))
                removed |= root.Remove(field);
        }

        return removed ? root.ToJsonString(ModelJson) : content;
    }

    private static bool IsSha256Hex(string value) =>
        value.Length == Sha256HexChars && value.All(char.IsAsciiHexDigit);

    private static JsonNode? TryParseStructured(string content)
    {
        if (content.AsSpan().TrimStart() is not { Length: > 0 } trimmed || trimmed[0] is not ('{' or '[')) return null;
        try { return JsonNode.Parse(content); }
        catch (JsonException) { return null; }
    }
}
