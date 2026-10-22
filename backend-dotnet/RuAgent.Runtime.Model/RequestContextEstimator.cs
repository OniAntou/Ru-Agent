using System.Text;
using System.Text.Json;
using RuAgent.Protocol.Harness;

namespace RuAgent.Runtime.Model;

/// <summary>Partitions the already-serialized input, never infers categories from provider totals.</summary>
internal static class RequestContextEstimator
{
    internal const int ImageInputTokenAllowance = ContextTokenBreakdown.ImageTokenAllowance;
    internal static ContextTokenBreakdown? Measure(string body, long imageBase64Bytes = 0, int imageCount = 0)
    {
        if (imageBase64Bytes < 0 || imageCount < 0) return null;
        try { return MeasureCore(body, imageBase64Bytes, imageCount); }
        catch (JsonException) { return null; } // Observational metadata never invalidates an otherwise prepared request.
    }

    private static ContextTokenBreakdown? MeasureCore(string body, long imageBase64Bytes, int imageCount)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        long systemBytes = 0, toolBytes = 0, messageBytes = 0;
        if (root.TryGetProperty("instructions", out var instructions)) systemBytes += Bytes(instructions);
        if (root.TryGetProperty("system", out var system)) systemBytes += Bytes(system);
        if (root.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0)
            toolBytes = Bytes(tools);
        JsonElement input;
        if (!root.TryGetProperty("messages", out input) && !root.TryGetProperty("input", out input)) return null;
        if (input.ValueKind == JsonValueKind.String) messageBytes = Bytes(input);
        else if (input.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in input.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("role", out var role)
                    && role.ValueKind == JsonValueKind.String && role.GetString() is "system" or "developer")
                    systemBytes += Bytes(item);
                else messageBytes += Bytes(item);
            }
        }
        else return null;
        long messages = (Math.Max(0, messageBytes - imageBase64Bytes) + 3) / 4 + (long)imageCount * ImageInputTokenAllowance;
        long systemTokens = (systemBytes + 3) / 4, toolTokens = (toolBytes + 3) / 4;
        if (systemTokens + toolTokens + messages > int.MaxValue) return null;
        return new((int)systemTokens, (int)toolTokens, (int)messages);
    }

    private static long Bytes(JsonElement value) => Encoding.UTF8.GetByteCount(value.GetRawText());
}
