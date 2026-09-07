using System.Text.Json;

namespace RuAgent.Harness.Core;

/// <summary>
/// Provider-reported token counts for one model request. Null means that the
/// provider did not report that field; numeric zero remains an exact value.
/// </summary>
public sealed record ModelUsage
{
    public ModelUsage(
        int? inputTokens,
        int? outputTokens,
        int? totalTokens,
        int? cacheReadInputTokens,
        int? cacheWriteInputTokens)
    {
        Validate(inputTokens, nameof(inputTokens));
        Validate(outputTokens, nameof(outputTokens));
        Validate(totalTokens, nameof(totalTokens));
        Validate(cacheReadInputTokens, nameof(cacheReadInputTokens));
        Validate(cacheWriteInputTokens, nameof(cacheWriteInputTokens));
        if (inputTokens is null
            && outputTokens is null
            && totalTokens is null
            && cacheReadInputTokens is null
            && cacheWriteInputTokens is null)
        {
            throw new ArgumentException("Usage must contain at least one reported count.");
        }

        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        TotalTokens = totalTokens;
        CacheReadInputTokens = cacheReadInputTokens;
        CacheWriteInputTokens = cacheWriteInputTokens;
    }

    public int? InputTokens { get; }

    public int? OutputTokens { get; }

    public int? TotalTokens { get; }

    public int? CacheReadInputTokens { get; }

    public int? CacheWriteInputTokens { get; }

    public void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        WriteOptional(writer, "input_tokens", InputTokens);
        WriteOptional(writer, "output_tokens", OutputTokens);
        WriteOptional(writer, "total_tokens", TotalTokens);
        WriteOptional(writer, "cache_read_input_tokens", CacheReadInputTokens);
        WriteOptional(writer, "cache_write_input_tokens", CacheWriteInputTokens);
        writer.WriteEndObject();
    }

    public static ModelUsage? Read(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("model_usage_json_invalid");
        }

        var usage = new ModelUsage(
            ReadOptional(element, "input_tokens"),
            ReadOptional(element, "output_tokens"),
            ReadOptional(element, "total_tokens"),
            ReadOptional(element, "cache_read_input_tokens"),
            ReadOptional(element, "cache_write_input_tokens"));
        return usage;
    }

    private static void Validate(int? value, string name)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is { } count)
        {
            writer.WriteNumber(name, count);
        }
    }

    private static int? ReadOptional(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var count) || count < 0)
        {
            throw new InvalidDataException("model_usage_json_invalid");
        }

        return count;
    }
}
