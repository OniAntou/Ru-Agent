using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;

namespace RuApp.Models;

/// <summary>
/// Provider-reported counts for one assistant model request.
/// Null means the provider did not report that field; zero remains exact.
/// </summary>
public sealed record AgentModelUsage(
    int? InputTokens,
    int? OutputTokens,
    int? TotalTokens,
    int? CacheReadInputTokens,
    int? CacheWriteInputTokens)
{
    public bool HasAny => InputTokens is not null
        || OutputTokens is not null
        || TotalTokens is not null
        || CacheReadInputTokens is not null
        || CacheWriteInputTokens is not null;

    public string DisplayText
    {
        get
        {
            var parts = new List<string>(5);
            Add(parts, InputTokens, "input");
            Add(parts, OutputTokens, "output");
            Add(parts, TotalTokens, "total");
            Add(parts, CacheReadInputTokens, "cache read");
            Add(parts, CacheWriteInputTokens, "cache write");
            return string.Join(" · ", parts);
        }
    }

    public static AgentModelUsage? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                // Display path stays lenient, but record the shape mismatch so a
                // missing footer is traceable (field names/kinds only, no values).
                Debug.WriteLine(
                    $"Agent usage payload is not an object: {document.RootElement.ValueKind}");
                return null;
            }

            var usage = new AgentModelUsage(
                ReadOptional(document.RootElement, "input_tokens"),
                ReadOptional(document.RootElement, "output_tokens"),
                ReadOptional(document.RootElement, "total_tokens"),
                ReadOptional(document.RootElement, "cache_read_input_tokens"),
                ReadOptional(document.RootElement, "cache_write_input_tokens"));
            return usage.HasAny ? usage : null;
        }
        catch (JsonException exception)
        {
            Debug.WriteLine(
                $"Agent usage payload is not valid JSON: {exception.GetType().Name}");
            return null;
        }
    }

    private static int? ReadOptional(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var count)
            || count < 0)
        {
            // Lenient by design (partial footer still renders), but log the
            // skipped field so malformed provider data does not vanish silently.
            Debug.WriteLine(
                $"Agent usage field '{name}' ignored: {value.ValueKind}");
            return null;
        }

        return count;
    }

    private static void Add(List<string> parts, int? value, string label)
    {
        if (value is { } count)
        {
            parts.Add($"{count} {label}");
        }
    }
}
