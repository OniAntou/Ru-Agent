using System.Text;
using System.Text.Json.Nodes;

namespace RuAgent.Broker.Tools.Files;

internal static class FileReadOutputBudget
{
    // Source bytes and encoded JSON bytes differ (Unicode/control escaping).
    // Fit the actual envelope before Broker normalization, preserving a source-byte cursor.
    public static JsonObject Fit(JsonObject output)
    {
        if (Fits(output)) return output;
        var content = output["content"]!.GetValue<string>();
        output["truncated"] = true;
        var low = 0;
        var high = content.Length;
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            SetPrefix(output, content, middle);
            if (Fits(output)) low = middle;
            else high = middle - 1;
        }
        SetPrefix(output, content, low);
        return output;
    }

    private static void SetPrefix(JsonObject output, string source, int length)
    {
        if (length > 0 && length < source.Length && char.IsHighSurrogate(source[length - 1])) length--;
        var prefix = source[..length];
        output["content"] = prefix;
        output["bytes_read"] = Encoding.UTF8.GetByteCount(prefix);
    }

    private static bool Fits(JsonObject output) =>
        Encoding.UTF8.GetByteCount(output.ToJsonString()) <= FileToolLimits.MaxReadOutputBytes;
}
