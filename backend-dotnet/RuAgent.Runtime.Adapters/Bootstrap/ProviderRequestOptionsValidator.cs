using System.Text.Json.Nodes;

namespace RuAgent.Runtime.Adapters.Bootstrap;

internal static class ProviderRequestOptionsValidator
{
    private static readonly IReadOnlySet<string> ReasoningFields =
        new HashSet<string>(["effort", "enabled", "max_tokens"], StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> AllowedEfforts =
        new HashSet<string>(["none", "minimal", "low", "medium", "high", "xhigh", "max"], StringComparer.Ordinal);

    public static JsonObject? ValidateAndClone(string protocol, JsonObject? requestOptions, string parameterName)
    {
        ProviderProtocols.Require(protocol);
        if (requestOptions is null) return null;
        if (requestOptions.Count == 0) return null;

        if (protocol == ProviderProtocols.ChatCompletions)
        {
            var fields = requestOptions.Select(property => property.Key).ToHashSet(StringComparer.Ordinal);
            if (fields.Count != 1 || (!fields.Contains("reasoning_effort") && !fields.Contains("reasoning")))
                throw Invalid(parameterName);
            if (requestOptions["reasoning_effort"] is JsonValue effortValue
                && effortValue.TryGetValue<string>(out var effort)
                && AllowedEfforts.Contains(effort))
                return new JsonObject { ["reasoning_effort"] = effort };
            if (requestOptions["reasoning"] is not JsonObject reasoning) throw Invalid(parameterName);
            var reasoningFields = reasoning.Select(property => property.Key).ToHashSet(StringComparer.Ordinal);
            if (reasoningFields.Count == 0 || !reasoningFields.IsSubsetOf(ReasoningFields)) throw Invalid(parameterName);
            var normalized = new JsonObject();
            if (reasoning.ContainsKey("effort"))
            {
                if (reasoning["effort"] is not JsonValue reasoningEffortValue
                    || !reasoningEffortValue.TryGetValue<string>(out var reasoningEffort)
                    || !AllowedEfforts.Contains(reasoningEffort)) throw Invalid(parameterName);
                normalized["effort"] = reasoningEffort;
            }
            if (reasoning.ContainsKey("enabled"))
            {
                if (reasoning["enabled"] is not JsonValue enabledValue
                    || !enabledValue.TryGetValue<bool>(out var enabled)) throw Invalid(parameterName);
                normalized["enabled"] = enabled;
            }
            if (reasoning.ContainsKey("max_tokens"))
            {
                if (reasoning["max_tokens"] is not JsonValue maxTokensValue
                    || !maxTokensValue.TryGetValue<int>(out var maxTokens)
                    || maxTokens is < 1 or > 1_000_000) throw Invalid(parameterName);
                normalized["max_tokens"] = maxTokens;
            }
            return new JsonObject { ["reasoning"] = normalized };
        }

        if (protocol == ProviderProtocols.OpenAiResponses)
        {
            if (requestOptions.Count != 1 || requestOptions["reasoning"] is not JsonObject reasoning) throw Invalid(parameterName);
            if (reasoning.Count != 1 || reasoning["effort"] is not JsonValue effortValue
                || !effortValue.TryGetValue<string>(out var effort)
                || !AllowedEfforts.Contains(effort)) throw Invalid(parameterName);
            return new JsonObject { ["reasoning"] = new JsonObject { ["effort"] = effort } };
        }

        if (protocol == ProviderProtocols.AnthropicMessages)
        {
            var keys = requestOptions.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
            if (keys.Count == 0 || keys.Count > 2) throw Invalid(parameterName);
            JsonObject? outputConfig = null;
            JsonObject? thinking = null;
            if (requestOptions["output_config"] is JsonObject oc)
            {
                if (oc.Count != 1 || oc["effort"] is not JsonValue effortValue
                    || !effortValue.TryGetValue<string>(out var effort)
                    || !AllowedEfforts.Contains(effort)) throw Invalid(parameterName);
                outputConfig = new JsonObject { ["effort"] = effort };
            }
            if (requestOptions["thinking"] is JsonObject th)
            {
                if (th.Count != 2 || th["type"] is not JsonValue typeValue || !typeValue.TryGetValue<string>(out var type) || type != "adaptive"
                    || th["display"] is not JsonValue displayValue || !displayValue.TryGetValue<string>(out var display) || display != "omitted") throw Invalid(parameterName);
                thinking = new JsonObject { ["type"] = "adaptive", ["display"] = "omitted" };
            }
            if (outputConfig is null && thinking is null) throw Invalid(parameterName);
            if (keys.Any(k => k != "output_config" && k != "thinking")) throw Invalid(parameterName);
            var result = new JsonObject();
            if (thinking is not null) result["thinking"] = thinking;
            if (outputConfig is not null) result["output_config"] = outputConfig;
            if (thinking is not null && outputConfig is null) throw Invalid(parameterName);
            return result;
        }

        throw Invalid(parameterName);
    }

    private static ArgumentException Invalid(string parameterName) =>
        new("Provider request options are invalid.", parameterName);
}
