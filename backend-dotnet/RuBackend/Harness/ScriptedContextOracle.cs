using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;

namespace RuBackend.Harness;

/// <summary>
/// One scripted step's bounded assertion about the model context the runtime assembled.
/// </summary>
/// <remarks>
/// The oracle is fixture-only test infrastructure: it proves that the context a scripted
/// step actually received contains (or excludes) the markers a workflow claims to
/// exercise, without exposing private payloads or hidden reasoning. A malformed oracle is
/// a fixture error and fails the host at startup.
/// </remarks>
internal sealed record ScriptedContextOracle(
    string? SourceId,
    string? Contains,
    string? AlsoContains,
    string? NotContains,
    string? CurrentPrompt,
    string? ProjectionSourceCallId,
    string? ProjectionResultCallId,
    string? ProjectionField,
    string? ProjectionExcludes,
    bool ProjectionCursorMatchesVisibleBytes)
{
    private const int MaxAssertionLength = 512;

    internal static ScriptedContextOracle? Read(JsonObject step)
    {
        if (!step.ContainsKey("context_oracle")) return null;
        if (step["context_oracle"] is not JsonObject oracle)
            throw new InvalidOperationException("A scripted context oracle must be an object.");
        var sourceId = oracle["source_id"]?.GetValue<string>();
        var contains = oracle["contains"]?.GetValue<string>();
        var alsoContains = oracle["also_contains"]?.GetValue<string>();
        var notContains = oracle["not_contains"]?.GetValue<string>();
        var currentPrompt = oracle["current_prompt"]?.GetValue<string>();
        var projectionSourceCallId = oracle["projection_source_call_id"]?.GetValue<string>();
        var projectionResultCallId = oracle["projection_result_call_id"]?.GetValue<string>();
        var projectionField = oracle["projection_field"]?.GetValue<string>();
        var projectionExcludes = oracle["projection_excludes"]?.GetValue<string>();
        var projectionCursorMatchesVisibleBytes = oracle["projection_cursor_matches_visible_bytes"]?.GetValue<bool>();
        var allowedProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "source_id", "contains", "also_contains", "not_contains", "current_prompt",
            "projection_source_call_id", "projection_field", "projection_excludes",
            "projection_cursor_matches_visible_bytes", "projection_result_call_id",
        };
        var hasProjectionOracle = projectionSourceCallId is not null
            || projectionField is not null
            || projectionExcludes is not null
            || projectionCursorMatchesVisibleBytes is not null
            || projectionResultCallId is not null;
        var hasEntryAssertion = sourceId is not null
            || alsoContains is not null
            || notContains is not null
            || currentPrompt is not null;
        if ((sourceId is null) != (contains is null)
            || (sourceId is not null && sourceId is not { Length: > 0 and <= 128 })
            || (contains is not null && contains is not { Length: > 0 and <= MaxAssertionLength })
            || alsoContains is { Length: 0 or > MaxAssertionLength }
            || notContains is { Length: 0 or > MaxAssertionLength }
            || currentPrompt is { Length: 0 or > MaxAssertionLength }
            || (!hasEntryAssertion && !hasProjectionOracle)
            || oracle.Any(property => !allowedProperties.Contains(property.Key)))
            throw new InvalidOperationException("A scripted context oracle needs a bounded source assertion or exact projected cursor assertion.");
        if (hasProjectionOracle
            && (projectionSourceCallId is not { Length: > 0 and <= 128 }
                || projectionField is not "content"
                || projectionExcludes is not { Length: > 0 and <= MaxAssertionLength }
                || projectionCursorMatchesVisibleBytes != true
                || (projectionResultCallId is not null && projectionResultCallId is not { Length: > 0 and <= 128 })))
            throw new InvalidOperationException("A projected cursor oracle needs a bounded exact source, field, marker, and byte-cursor assertion.");
        return new ScriptedContextOracle(sourceId, contains, alsoContains, notContains, currentPrompt,
            projectionSourceCallId, projectionResultCallId, projectionField, projectionExcludes, projectionCursorMatchesVisibleBytes == true);
    }

    internal void Validate(ModelContextSnapshot context, SystemPromptContext promptContext)
    {
        var matched = SourceId is null || context.Entries.Any(entry =>
            string.Equals(entry.SourceId, SourceId, StringComparison.Ordinal)
            && entry.Content is { } content
            && content.Contains(Contains!, StringComparison.Ordinal));
        var additionalMatched = AlsoContains is null || context.Entries.Any(entry =>
            entry.Content is { } content
            && content.Contains(AlsoContains, StringComparison.Ordinal));
        var exclusionMatched = NotContains is null || !context.Entries.Any(entry =>
            entry.Content is { } content
            && content.Contains(NotContains, StringComparison.Ordinal));
        var currentPromptMatched = CurrentPrompt is null || context.Entries.Any(entry =>
            entry.Kind == ModelContextEntryKind.Text
            && entry.Role == ModelContextRole.User
            && entry.SourceId.StartsWith("event:evt_", StringComparison.Ordinal)
            && string.Equals(entry.Content, CurrentPrompt, StringComparison.Ordinal));
        var projectionMatched = ProjectionSourceCallId is null
            || ValidateProjectedReference(context, promptContext);
        if (!matched || !additionalMatched || !exclusionMatched || !currentPromptMatched || !projectionMatched)
            throw new InvalidOperationException("scripted_context_oracle_failed");
    }

    private bool ValidateProjectedReference(ModelContextSnapshot context, SystemPromptContext promptContext)
    {
        var sourceEntries = context.Entries.Where(entry =>
            entry.Kind == ModelContextEntryKind.ToolResult
            && string.Equals(entry.ToolReceipt?.ProviderCallId, ProjectionSourceCallId, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (sourceEntries.Length != 1)
            return false;
        var source = sourceEntries[0];
        if (source?.ToolReceipt is not { } receipt || source.Content is null)
            return false;
        var references = promptContext.RuntimeContext.References.Where(item =>
            item.Kind == "tool_result"
            && HasProjectionCallIdentity(item.Content, receipt.ProviderCallId, receipt.ToolCallId.Value))
            .Take(2)
            .ToArray();
        if (references.Length != 1)
            return false;
        var reference = references[0];

        try
        {
            using var sourceDocument = JsonDocument.Parse(source.Content);
            using var projectedDocument = JsonDocument.Parse(reference.Content);
            var sourceRoot = sourceDocument.RootElement;
            var projectedRoot = projectedDocument.RootElement;
            var sourceText = sourceRoot.GetProperty(ProjectionField!).GetString();
            var projectedText = projectedRoot.GetProperty(ProjectionField!).GetString();
            var sourceOffset = sourceRoot.GetProperty("offset").GetInt32();
            var projectedOffset = projectedRoot.GetProperty("next_offset").GetInt32();
            var expectedOffset = checked(sourceOffset + System.Text.Encoding.UTF8.GetByteCount(projectedText!));
            return sourceText is not null
                && projectedText is not null
                && sourceText.StartsWith(projectedText, StringComparison.Ordinal)
                && sourceText.Contains(ProjectionExcludes!, StringComparison.Ordinal)
                && !projectedText.Contains(ProjectionExcludes!, StringComparison.Ordinal)
                && projectedOffset == expectedOffset
                && ProjectionCursorMatchesVisibleBytes
                && projectedOffset > sourceOffset
                && (ProjectionResultCallId is null
                    || ValidateDownstreamProjectionReceipt(ProjectionResultCallId, projectedOffset, context));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or OverflowException or FormatException)
        {
            return false;
        }
    }

    private static bool ValidateDownstreamProjectionReceipt(
        string providerCallId,
        int expectedOffset,
        ModelContextSnapshot context)
    {
        var receipts = context.Entries.Where(entry =>
                entry.Kind == ModelContextEntryKind.ToolResult
                && string.Equals(entry.ToolReceipt?.ProviderCallId, providerCallId, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (receipts.Length != 1 || receipts[0].Content is not { } content
            || receipts[0].ToolReceipt is not { } receipt
            || receipt.ProviderCallId is null
            || string.IsNullOrWhiteSpace(receipt.ToolCallId.Value))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            return string.Equals(receipt.ProviderCallId, providerCallId, StringComparison.Ordinal)
                && document.RootElement.TryGetProperty("offset", out var offset)
                && offset.ValueKind == JsonValueKind.Number
                && offset.TryGetInt32(out var actualOffset)
                && actualOffset == expectedOffset;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool HasProjectionCallIdentity(
        string content,
        string? expectedProviderCallId,
        string? expectedToolCallId)
    {
        if (expectedProviderCallId is null || expectedToolCallId is null) return false;
        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.TryGetProperty("projection_provider_call_id", out var providerCallId)
                && providerCallId.ValueKind == JsonValueKind.String
                && string.Equals(providerCallId.GetString(), expectedProviderCallId, StringComparison.Ordinal)
                && document.RootElement.TryGetProperty("projection_tool_call_id", out var toolCallId)
                && toolCallId.ValueKind == JsonValueKind.String
                && string.Equals(toolCallId.GetString(), expectedToolCallId, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool HasProjectionIdentity(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.TryGetProperty("projection_tool_call_id", out _)
                || document.RootElement.TryGetProperty("projection_provider_call_id", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
