using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuBackend.Providers;

/// <summary>Parses provider-owned metadata; never infers effort from a model name.</summary>
public static class ProviderCatalogParser
{
    private static readonly HashSet<string> Efforts = ["none", "minimal", "low", "medium", "high", "xhigh", "max"];

    public static IReadOnlyList<CatalogModel> Parse(string backend, string json)
    {
        try
        {
            var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 32 });
            if (root?["data"] is not JsonArray data || data.Count > 5000) throw new JsonException();
            var result = new List<CatalogModel>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var alias = backend switch
            {
                "OpenAI" => "openai",
                "Anthropic" => "anthropic",
                _ => null,
            };
            foreach (var node in data)
            {
                if (node is not JsonObject item || Text(item["id"]) is not { } id
                    || id.Length is 0 or > 256 || id.Any(char.IsControl)) throw new JsonException();
                if (!seen.Add(id)) continue;
                if (alias is not null && ProviderCapabilityRegistry.TryGet(alias, id, out var capability))
                {
                    result.Add(new(id, true, capability.Protocol, new("levels", capability.ReasoningOptions.Keys.ToArray())) { WireOptions = capability.ReasoningOptions });
                    continue;
                }
                if (alias is not null)
                {
                    result.Add(new(id, false, alias == "openai" ? "openai_responses" : "anthropic_messages", new("unsupported", [])));
                    continue;
                }
                var protocol = Text(item["protocol"]) ?? "chat_completions";
                if (protocol.Length is 0 or > 64 || protocol.Any(char.IsControl)) throw new JsonException();
                if (backend == "OpenCode Zen" && id.StartsWith("gpt-", StringComparison.Ordinal)) protocol = "responses";
                if (backend == "OpenCode Zen" && id.StartsWith("claude-", StringComparison.Ordinal)) protocol = "messages";
                var supported = protocol == "chat_completions";
                var options = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
                if (backend == "Kilo Gateway" && item["opencode"] is JsonObject opencode
                    && opencode["variants"] is JsonObject variants)
                {
                    foreach (var pair in variants.Take(16))
                    {
                        if ((!Efforts.Contains(pair.Key) && pair.Key is not ("enabled" or "disabled")) || pair.Value is not JsonObject variant
                            || variant["reasoning"] is not JsonObject reasoning
                            || !TryReasoning(reasoning, out var safe)) continue;
                        options[pair.Key] = new JsonObject { ["reasoning"] = safe };
                    }
                }
                if (item["supported_parameters"] is JsonArray parameters
                    && parameters.Any(p => Text(p) == "reasoning_effort")
                    && item["reasoning_efforts"] is JsonArray levels)
                {
                    foreach (var value in levels.Take(16))
                    {
                        if (Text(value) is { } effort && Efforts.Contains(effort))
                            options.TryAdd(effort, new JsonObject { ["reasoning_effort"] = effort });
                    }
                }
                var state = options.Count > 0 ? "levels" : "unknown";
                if (options.Count > 0 && options.Keys.All(key => key is "enabled" or "disabled")) state = "toggle";
                if (item["reasoning"] is JsonValue flag && flag.TryGetValue<bool>(out var enabled) && !enabled)
                {
                    state = "unsupported";
                    options.Clear();
                }
                if (!supported) { options.Clear(); state = "unsupported"; }
                result.Add(new(id, supported, protocol, new(state, options.Keys.ToArray())) { WireOptions = options });
            }
            return result;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            throw new ProviderCatalogException("catalog_malformed");
        }
    }

    private static bool TryReasoning(JsonObject input, out JsonObject safe)
    {
        safe = new JsonObject();
        foreach (var pair in input)
        {
            switch (pair.Key)
            {
                case "effort" when Text(pair.Value) is { } effort && Efforts.Contains(effort):
                    safe["effort"] = effort; break;
                case "enabled" when pair.Value is JsonValue flag && flag.TryGetValue<bool>(out var enabled):
                    safe["enabled"] = enabled; break;
                case "max_tokens" when pair.Value is JsonValue count && count.TryGetValue<int>(out var tokens) && tokens is > 0 and <= 1000000:
                    safe["max_tokens"] = tokens; break;
                default: return false;
            }
        }
        return safe.Count > 0;
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
