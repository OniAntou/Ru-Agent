using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;

using RuApp.Services;

namespace RuApp.Models;

/// <summary>
/// Provider-reported counts for one assistant model request.
/// Null means the provider did not report that field; zero remains exact.
/// </summary>
/// <param name="InputTokens">Prompt tokens billed.</param>
/// <param name="OutputTokens">Completion tokens billed.</param>
/// <param name="TotalTokens">The provider's own total, when it published one.</param>
/// <param name="CacheReadInputTokens">Prompt tokens served from a warm cache.</param>
/// <param name="CacheWriteInputTokens">Prompt tokens written into the cache.</param>
/// <param name="Model">
/// The model this request billed, as the adapter sent it. Null for a run recorded
/// before the field existed, which is a run nobody can price rather than a broken one.
/// </param>
public sealed record AgentModelUsage(
    int? InputTokens,
    int? OutputTokens,
    int? TotalTokens,
    int? CacheReadInputTokens,
    int? CacheWriteInputTokens,
    string? Model = null,
    bool? InputTokensIncludeCache = null)
{
    /// <summary>Largest individual report when this value aggregates a run; never a context-window sum.</summary>
    public int? PeakStepTokens { get; init; }
    public int? UncachedInputTokens => InputTokens is { } input
        ? InputTokensIncludeCache == true
            ? (int)Math.Max(0L, (long)input - (CacheReadInputTokens ?? 0) - (CacheWriteInputTokens ?? 0))
            : input
        : null;

    public long? PromptInputTokens => InputTokens is { } input
        ? InputTokensIncludeCache == true ? input
            : InputTokensIncludeCache == false && CacheWriteInputTokens is null ? null
            : (long)input + (CacheReadInputTokens ?? 0) + (CacheWriteInputTokens ?? 0)
        : null;

    public double? CacheHitPercent => InputTokensIncludeCache is not null
        && CacheReadInputTokens is { } read && PromptInputTokens is > 0
        && read <= PromptInputTokens.Value
        ? read * 100.0 / PromptInputTokens.Value : null;

    public static string FormatCacheHit(double percent)
    {
        // Never round a partial hit up to a claimed full hit.
        double display = Math.Floor(percent * 1000) / 1000;
        return display.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "%";
    }

    public bool HasAny => InputTokens is not null
        || OutputTokens is not null
        || TotalTokens is not null
        || CacheReadInputTokens is not null
        || CacheWriteInputTokens is not null;

    /// <summary>
    /// The one figure that stands for "how big was this step": the provider's own
    /// total when it reported one, otherwise the parts added up. Cache reads and
    /// writes count, because they were billed as part of the same request.
    /// </summary>
    public int ResolvedTotal
    {
        get
        {
            if (TotalTokens is { } total && total > 0)
            {
                return total;
            }

            return (UncachedInputTokens ?? 0)
                + (OutputTokens ?? 0)
                + (CacheReadInputTokens ?? 0)
                + (CacheWriteInputTokens ?? 0);
        }
    }

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
                GuiDiagnosticWriter.AppendTraceLine(
                    $"Agent usage payload is not an object: {document.RootElement.ValueKind}");
                return null;
            }

            var usage = new AgentModelUsage(
                ReadOptional(document.RootElement, "input_tokens"),
                ReadOptional(document.RootElement, "output_tokens"),
                ReadOptional(document.RootElement, "total_tokens"),
                ReadOptional(document.RootElement, "cache_read_input_tokens"),
                ReadOptional(document.RootElement, "cache_write_input_tokens"),
                // The model is a name rather than a count, so it cannot go through
                // ReadOptional: a report recorded before the field existed has none and
                // must still parse, as a run whose price nobody can look up.
                ReadModel(document.RootElement),
                ReadInputAccounting(document.RootElement));
            return usage.HasAny ? usage : null;
        }
        catch (JsonException exception)
        {
            GuiDiagnosticWriter.AppendTraceLine(
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
            GuiDiagnosticWriter.AppendTraceLine(
                $"Agent usage field '{name}' ignored: {value.ValueKind}");
            return null;
        }

        return count;
    }

    private static bool? ReadInputAccounting(JsonElement root) =>
        root.TryGetProperty("input_tokens_include_cache", out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : null;

    /// <summary>
    /// The model a report names, or null when it names none.
    /// </summary>
    /// <remarks>
    /// Lenient in the same way as the counts: a name that is not a usable identifier is
    /// a run that cannot be priced, which is a fact the panel states rather than a
    /// reason to drop the report entirely.
    /// </remarks>
    private static string? ReadModel(JsonElement root)
    {
        if (!root.TryGetProperty("model", out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var model = value.GetString()?.Trim();
        return model is { Length: > 0 and <= 256 } && !model.Any(char.IsControl)
            ? model
            : null;
    }

    private static void Add(List<string> parts, int? value, string label)
    {
        if (value is { } count)
        {
            parts.Add($"{count} {label}");
        }
    }
}
