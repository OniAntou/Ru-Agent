using System.Text.Json;

namespace RuAgent.Client;

/// <summary>
/// Validated transient request-size estimate for one Agent model step: the measured
/// size of the exact request about to be dispatched, expressed in the same token
/// units the provider reports and bounded by the same prompt budget the admission
/// policy enforces.
///
/// Provider usage only arrives when a step ends, so without this frame a client's
/// context meter sits on the previous step's figure — or stays empty on the first
/// turn — for the whole reply. The reported usage supersedes the estimate as steps
/// complete.
/// </summary>
public sealed record AgentUsageEstimateModel(
    long EstimatedInputTokens,
    long MaxPromptTokens,
    string ModelStepId,
    long TransientSequence)
{
    public static bool TryParse(SseFrame frame, out AgentUsageEstimateModel? estimate)
    {
        estimate = null;
        // AgentSseClient exposes the transport event name ("transient"), while
        // direct callers may pass the logical event name for deterministic tests.
        if (!frame.IsTransient
            || (frame.EventType is not "transient" and not "agent.usage_estimate")
            || frame.TransientSequence is not > 0
            || string.IsNullOrWhiteSpace(frame.Data))
        {
            return false;
        }

        try
        {
            using var outer = JsonDocument.Parse(frame.Data);
            if (!TryReadOuter(outer.RootElement, out var innerJson))
            {
                return false;
            }

            using var inner = JsonDocument.Parse(innerJson);
            if (!TryReadInner(inner.RootElement, out var estimatedInput, out var maxPrompt, out var modelStepId))
            {
                return false;
            }

            estimate = new AgentUsageEstimateModel(
                estimatedInput,
                maxPrompt,
                modelStepId,
                frame.TransientSequence.Value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadOuter(JsonElement root, out string innerJson)
    {
        innerJson = string.Empty;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? kind = null;
        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                return false;
            }

            switch (property.Name)
            {
                case "kind" when property.Value.ValueKind == JsonValueKind.String:
                    kind = property.Value.GetString();
                    break;
                case "data" when property.Value.ValueKind == JsonValueKind.String:
                    innerJson = property.Value.GetString() ?? string.Empty;
                    break;
                default:
                    return false;
            }
        }

        return string.Equals(kind, "agent.usage_estimate", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(innerJson);
    }

    private static bool TryReadInner(
        JsonElement root,
        out long estimatedInputTokens,
        out long maxPromptTokens,
        out string modelStepId)
    {
        estimatedInputTokens = 0;
        maxPromptTokens = 0;
        modelStepId = string.Empty;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hasEstimated = false;
        var hasMaxPrompt = false;
        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                return false;
            }

            switch (property.Name)
            {
                case "estimated_input_tokens"
                    when property.Value.ValueKind == JsonValueKind.Number
                        && property.Value.TryGetInt64(out estimatedInputTokens):
                    hasEstimated = true;
                    break;
                case "max_prompt_tokens"
                    when property.Value.ValueKind == JsonValueKind.Number
                        && property.Value.TryGetInt64(out maxPromptTokens):
                    hasMaxPrompt = true;
                    break;
                case "model_step_id" when property.Value.ValueKind == JsonValueKind.String:
                    modelStepId = property.Value.GetString() ?? string.Empty;
                    break;
                default:
                    return false;
            }
        }

        return hasEstimated
            && hasMaxPrompt
            && estimatedInputTokens > 0
            && maxPromptTokens >= 0
            && IsGeneratedId(modelStepId, "step_");
    }

    private static bool IsGeneratedId(string value, string prefix) =>
        value.Length == prefix.Length + 32
        && value.StartsWith(prefix, StringComparison.Ordinal)
        && value.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;
}
