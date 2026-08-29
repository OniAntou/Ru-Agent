using System.Text.Json.Nodes;

namespace RuAgent.Broker.Skills;

/// <summary>
/// Small, deterministic transform allowlist for declarative skills.
/// </summary>
public static class SkillNamedTransformRegistry
{
    private static readonly IReadOnlySet<string> TransformNames = new HashSet<string>(
        ["identity", "pick_field", "pick_fields"],
        StringComparer.Ordinal);

    public static bool IsKnown(string? name) =>
        !string.IsNullOrWhiteSpace(name) && TransformNames.Contains(name);

    public static JsonNode? Apply(
        string name,
        JsonNode? value,
        JsonObject? arguments,
        out string? errorCode)
    {
        errorCode = null;
        if (!IsKnown(name))
        {
            errorCode = "unknown_skill_transform";
            return null;
        }

        switch (name)
        {
            case "identity":
                return value?.DeepClone();
            case "pick_field":
                if (arguments?["field"] is not JsonValue fieldValue
                    || !fieldValue.TryGetValue<string>(out var field)
                    || string.IsNullOrWhiteSpace(field)
                    || value is not JsonObject objectValue
                    || !objectValue.TryGetPropertyValue(field, out var fieldNode))
                {
                    errorCode = "invalid_skill_transform";
                    return null;
                }

                return fieldNode?.DeepClone();
            case "pick_fields":
                if (arguments?["fields"] is not JsonArray fields
                    || value is not JsonObject source)
                {
                    errorCode = "invalid_skill_transform";
                    return null;
                }

                var result = new JsonObject();
                foreach (var item in fields)
                {
                    if (item is not JsonValue fieldItem
                        || !fieldItem.TryGetValue<string>(out var fieldName)
                        || string.IsNullOrWhiteSpace(fieldName)
                        || !source.TryGetPropertyValue(fieldName, out var fieldValueNode))
                    {
                        errorCode = "invalid_skill_transform";
                        return null;
                    }

                    result[fieldName] = fieldValueNode?.DeepClone();
                }

                return result;
            default:
                errorCode = "unknown_skill_transform";
                return null;
        }
    }
}
