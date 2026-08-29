using System.Text.Json.Nodes;

namespace RuAgent.Broker.Skills;

/// <summary>
/// Code-owned, deterministic predicates used by declarative skills. Names are an
/// allowlist; manifests never resolve arbitrary methods or expressions.
/// </summary>
public static class SkillStepEvaluator
{
    private static readonly IReadOnlySet<string> PredicateNames = new HashSet<string>(
        ["always_true", "always_false", "non_empty", "is_object", "is_array", "has_field", "equals"],
        StringComparer.Ordinal);

    public static bool IsKnownPredicate(string? name) =>
        !string.IsNullOrWhiteSpace(name) && PredicateNames.Contains(name);

    public static bool Evaluate(
        string name,
        JsonNode? value,
        JsonObject? arguments,
        out string? errorCode)
    {
        errorCode = null;
        if (!IsKnownPredicate(name))
        {
            errorCode = "unknown_skill_predicate";
            return false;
        }

        switch (name)
        {
            case "always_true":
                return true;
            case "always_false":
                return false;
            case "non_empty":
                return value switch
                {
                    JsonObject obj => obj.Count > 0,
                    JsonArray array => array.Count > 0,
                    JsonValue scalar when scalar.TryGetValue<string>(out var text) => !string.IsNullOrEmpty(text),
                    null => false,
                    _ => true,
                };
            case "is_object":
                return value is JsonObject;
            case "is_array":
                return value is JsonArray;
            case "has_field":
                if (arguments?["field"] is not JsonValue fieldValue
                    || !fieldValue.TryGetValue<string>(out var field)
                    || string.IsNullOrWhiteSpace(field)
                    || value is not JsonObject objectValue)
                {
                    errorCode = "invalid_skill_predicate";
                    return false;
                }

                return objectValue.ContainsKey(field);
            case "equals":
                if (arguments is null || !arguments.TryGetPropertyValue("value", out var expected))
                {
                    errorCode = "invalid_skill_predicate";
                    return false;
                }

                return JsonNode.DeepEquals(value, expected);
            default:
                errorCode = "unknown_skill_predicate";
                return false;
        }
    }
}
