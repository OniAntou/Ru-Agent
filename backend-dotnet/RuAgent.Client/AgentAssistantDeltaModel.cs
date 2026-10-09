using System.Text.Json;

namespace RuAgent.Client;

/// <summary>
/// Validated transient assistant text for one Agent model step. The model exposes
/// only bounded text and validated execution identity; raw envelope JSON never reaches
/// the desktop UI.
/// </summary>
public sealed record AgentAssistantDeltaModel(
    string Text,
    string ModelStepId,
    string? ExecutionId,
    long TransientSequence,
    bool IsReset = false)
{
    public const int MaximumTextCharacters = 64 * 1024;

    public static bool TryParse(SseFrame frame, out AgentAssistantDeltaModel? delta)
    {
        delta = null;
        // AgentSseClient exposes the transport event name ("transient"), while
        // direct callers may pass the logical event name for deterministic tests.
        if (!frame.IsTransient
            || (frame.EventType is not "transient" and not "assistant.delta")
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
            if (!TryReadInner(inner.RootElement, out var text, out var modelStepId, out var executionId, out var isReset))
            {
                return false;
            }

            delta = new AgentAssistantDeltaModel(
                text,
                modelStepId,
                executionId,
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

        return string.Equals(kind, "assistant.delta", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(innerJson);
    }

    private static bool TryReadInner(
        JsonElement root,
        out string text,
        out string modelStepId,
        out string? executionId,
        out bool isReset)
    {
        text = string.Empty;
        modelStepId = string.Empty;
        executionId = null;
        isReset = false;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hasText = false;
        var hasModelStepId = false;
        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                return false;
            }

            switch (property.Name)
            {
                case "text" when property.Value.ValueKind == JsonValueKind.String:
                    text = property.Value.GetString() ?? string.Empty;
                    hasText = true;
                    break;
                case "model_step_id" when property.Value.ValueKind == JsonValueKind.String:
                    modelStepId = property.Value.GetString() ?? string.Empty;
                    hasModelStepId = true;
                    break;
                case "execution_id" when property.Value.ValueKind == JsonValueKind.String:
                    executionId = property.Value.GetString();
                    break;
                case "execution_id" when property.Value.ValueKind == JsonValueKind.Null:
                    executionId = null;
                    break;
                case "reset" when property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    isReset = property.Value.GetBoolean();
                    break;
                case "reset" when property.Value.ValueKind == JsonValueKind.Null:
                    // The model step serializes unset optionals explicitly; a null
                    // reset is an ordinary delta, not a reset frame.
                    isReset = false;
                    break;
                default:
                    return false;
            }
        }

        return hasText
            && hasModelStepId
            // A reset frame is deliberately empty: it carries no answer text.
            && (isReset
                ? text.Length == 0
                : text.Length is >= 1 and <= MaximumTextCharacters && !text.Contains('\0'))
            && IsGeneratedId(modelStepId, "step_")
            && (executionId is null || IsGeneratedId(executionId, "exec_"));
    }

    private static bool IsGeneratedId(string value, string prefix) =>
        value.Length == prefix.Length + 32
        && value.StartsWith(prefix, StringComparison.Ordinal)
        && value.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;
}
