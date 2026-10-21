using System.Text.Json;

namespace RuAgent.Harness.Core;

/// <summary>
/// Provider-reported token counts for one model request. Null means that the
/// provider did not report that field; numeric zero remains an exact value.
/// </summary>
/// <remarks>
/// The model id travels with the counts because a session can switch models between
/// runs, and a token count is only worth anything next to what that model charges.
/// It is optional and written last so a payload recorded before this field existed
/// still reads back, as a run whose price nobody can name.
/// </remarks>
public sealed record ModelUsage
{
    public ModelUsage(
        int? inputTokens,
        int? outputTokens,
        int? totalTokens,
        int? cacheReadInputTokens,
        int? cacheWriteInputTokens,
        string? model = null,
        bool? inputTokensIncludeCache = null)
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
        Model = Clean(model);
        InputTokensIncludeCache = inputTokensIncludeCache;
    }

    /// <summary>
    /// A usable model name, or null. Bounded and control-char free so the field can be
    /// written into a stored payload and looked up in a price table without either
    /// side having to defend itself again.
    /// </summary>
    private static string? Clean(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var trimmed = model.Trim();
        return trimmed.Length <= 256 && !trimmed.Any(char.IsControl) ? trimmed : null;
    }

    public int? InputTokens { get; }

    public int? OutputTokens { get; }

    public int? TotalTokens { get; }

    public int? CacheReadInputTokens { get; }

    public int? CacheWriteInputTokens { get; }

    /// <summary>
    /// The model this request billed, as the adapter sent it. Null when the run
    /// predates this field or the adapter did not name a model.
    /// </summary>
    public string? Model { get; }

    /// <summary>True for inclusive Chat/Responses input; false for Messages buckets.
    /// Null preserves legacy records whose accounting was not identified.</summary>
    public bool? InputTokensIncludeCache { get; }

    public void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        WriteOptional(writer, "input_tokens", InputTokens);
        WriteOptional(writer, "output_tokens", OutputTokens);
        WriteOptional(writer, "total_tokens", TotalTokens);
        WriteOptional(writer, "cache_read_input_tokens", CacheReadInputTokens);
        WriteOptional(writer, "cache_write_input_tokens", CacheWriteInputTokens);
        if (InputTokensIncludeCache is { } inclusive)
            writer.WriteBoolean("input_tokens_include_cache", inclusive);
        if (Model is not null)
        {
            writer.WriteString("model", Model);
        }

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
            ReadOptional(element, "cache_write_input_tokens"),
            ReadModel(element),
            ReadInputAccounting(element));
        return usage;
    }

    private static void Validate(int? value, string name)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }

    private static bool? ReadInputAccounting(JsonElement element)
    {
        if (!element.TryGetProperty("input_tokens_include_cache", out var value)
            || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("model_usage_json_invalid");
        return value.GetBoolean();
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

    /// <summary>
    /// Reads the model id. Anything that is not a short clean string is treated as
    /// absent: a model name is never worth failing a usage payload over, and a name
    /// the price table cannot match is the same as no name.
    /// </summary>
    private static string? ReadModel(JsonElement element)
    {
        if (!element.TryGetProperty("model", out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var model = value.GetString()?.Trim();
        return model is { Length: > 0 and <= 256 } && !model.Any(char.IsControl) ? model : null;
    }
}
