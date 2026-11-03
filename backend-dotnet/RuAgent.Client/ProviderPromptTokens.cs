using System.Text.Json;

namespace RuAgent.Client;

/// <summary>Prompt occupancy uses the provider's accounting convention, never its input+output total.</summary>
public static class ProviderPromptTokens
{
    public static long? Resolve(string? protocol, long? input, long? cacheRead, long? cacheWrite, bool? includesCache = null)
    {
        if (protocol is not ("chat_completions" or "openai_responses" or "anthropic_messages")) return null;
        if (includesCache == true || includesCache is null && protocol != "anthropic_messages") return input;
        if (input is null && cacheRead is null && cacheWrite is null) return null;
        return Math.Min(int.MaxValue, Clamp(input) + Clamp(cacheRead) + Clamp(cacheWrite));
    }

    private static long Clamp(long? count) => Math.Clamp(count ?? 0, 0, int.MaxValue);

    public static bool TryRead(string content, string? protocol, out long? promptTokens,
        out RuAgent.Protocol.Harness.ContextTokenBreakdown? breakdown)
    {
        promptTokens = null; breakdown = null;
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            int? Count(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var count) && count >= 0 ? count : null;
            bool? inclusive = root.TryGetProperty("input_tokens_include_cache", out var flag)
                && flag.ValueKind is JsonValueKind.True or JsonValueKind.False ? flag.GetBoolean() : null;
            promptTokens = Resolve(protocol, Count("input_tokens"), Count("cache_read_input_tokens"), Count("cache_write_input_tokens"), inclusive);
            breakdown = RuAgent.Protocol.Harness.ContextTokenBreakdown.ReadOptional(root);
            return promptTokens is not null || breakdown is not null;
        }
        catch (JsonException) { return false; }
    }
}
