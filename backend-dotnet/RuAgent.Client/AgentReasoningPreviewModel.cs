using System.Text.Json;

namespace RuAgent.Client;

/// <summary>
/// One validated absolute line of the running thinking row, or a reset that clears it.
/// The model exposes only the bounded line and the row identity; raw frame JSON never
/// reaches the desktop UI.
/// </summary>
public sealed record AgentReasoningPreviewModel(
    string ActivityId,
    string? ModelStepId,
    string Text,
    long TransientSequence,
    bool IsReset)
{
    /// <summary>Longest line the host may publish, matching the core preview bound.</summary>
    public const int MaximumTextCharacters = 400;

    private const int MaximumActivityIdLength = 128;

    public static bool TryParse(SseFrame frame, out AgentReasoningPreviewModel? preview)
    {
        preview = null;
        // AgentSseClient exposes the transport event name ("transient"), while direct
        // callers may pass the logical event name for deterministic tests.
        if (!frame.IsTransient
            || (frame.EventType is not "transient" and not "agent.reasoning_preview")
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
            if (!TryReadInner(inner.RootElement, out var activityId, out var modelStepId, out var text, out var isReset))
            {
                return false;
            }

            preview = new AgentReasoningPreviewModel(
                activityId,
                modelStepId,
                isReset ? string.Empty : text,
                frame.TransientSequence.Value,
                isReset);
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

        return string.Equals(kind, "agent.reasoning_preview", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(innerJson);
    }

    private static bool TryReadInner(
        JsonElement root,
        out string activityId,
        out string? modelStepId,
        out string text,
        out bool isReset)
    {
        activityId = string.Empty;
        modelStepId = null;
        text = string.Empty;
        isReset = false;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hasActivityId = false;
        var hasText = false;
        var textWasNull = false;
        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                return false;
            }

            switch (property.Name)
            {
                case "activity_id" when property.Value.ValueKind == JsonValueKind.String:
                    activityId = property.Value.GetString() ?? string.Empty;
                    hasActivityId = true;
                    break;
                case "model_step_id" when property.Value.ValueKind == JsonValueKind.String:
                    modelStepId = property.Value.GetString() ?? string.Empty;
                    break;
                case "model_step_id" when property.Value.ValueKind == JsonValueKind.Null:
                    modelStepId = null;
                    break;
                case "text" when property.Value.ValueKind == JsonValueKind.String:
                    text = property.Value.GetString() ?? string.Empty;
                    hasText = true;
                    break;
                case "text" when property.Value.ValueKind == JsonValueKind.Null:
                    // A reset serializes the line as null; an ordinary frame must carry one.
                    textWasNull = true;
                    hasText = true;
                    break;
                case "reset" when property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    isReset = property.Value.GetBoolean();
                    break;
                case "reset" when property.Value.ValueKind == JsonValueKind.Null:
                    isReset = false;
                    break;
                default:
                    return false;
            }
        }

        return hasActivityId
            && IsValidActivityId(activityId)
            && hasText
            && (modelStepId is null || IsGeneratedId(modelStepId, "step_"))
            && (isReset
                ? textWasNull || text.Length == 0
                : !textWasNull
                    && text.Length is >= 1 and <= MaximumTextCharacters
                    && !text.Contains('\0'));
    }

    /// <summary>Mirrors the protocol's generated activity-row identity shape.</summary>
    private static bool IsValidActivityId(string value)
    {
        if (value.Length is < 5 or > MaximumActivityIdLength
            || !value.StartsWith("act_", StringComparison.Ordinal))
        {
            return false;
        }

        for (var index = 4; index < value.Length; index++)
        {
            var character = value[index];
            if (!char.IsAsciiLetterOrDigit(character) && character != '_')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsGeneratedId(string value, string prefix) =>
        value.Length == prefix.Length + 32
        && value.StartsWith(prefix, StringComparison.Ordinal)
        && value.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;
}
