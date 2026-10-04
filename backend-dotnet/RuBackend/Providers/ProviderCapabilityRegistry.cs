using System.Text.Json.Nodes;

namespace RuBackend.Providers;

public sealed record ProviderCapability(
    string Alias,
    string ModelId,
    string Protocol,
    IReadOnlyDictionary<string, JsonObject> ReasoningOptions,
    int? DocumentedMaxOutputTokens,
    bool SupportsNativeTools = false);

public static class ProviderCapabilityRegistry
{
    public const string SourceDate = "2026-09-01";

    private static readonly Dictionary<string, ProviderCapability> Capabilities = new(StringComparer.Ordinal);

    static ProviderCapabilityRegistry()
    {
        RegisterOpenAi("gpt-5.6-sol");
        RegisterOpenAi("gpt-5.6-terra");
        RegisterOpenAi("gpt-5.6-luna");
        RegisterAnthropicAdaptive("claude-fable-5", ["low", "medium", "high", "xhigh", "max"], 128000);
        RegisterAnthropicAdaptive("claude-opus-5", ["low", "medium", "high", "xhigh", "max"], 128000);
        RegisterAnthropicAdaptive("claude-opus-4-8", ["low", "medium", "high", "xhigh", "max"], 128000);
        RegisterAnthropicAdaptive("claude-sonnet-5", ["low", "medium", "high", "xhigh", "max"], 128000);
        RegisterAnthropicAdaptive("claude-sonnet-4-6", ["low", "medium", "high", "max"], 64000);
        RegisterAnthropicDefaultOnly("claude-haiku-4-5-20251001", 64000);
    }

    public static bool TryGet(string alias, string modelId, out ProviderCapability capability)
        => Capabilities.TryGetValue(Key(alias, modelId), out capability!);

    private static void RegisterOpenAi(string modelId)
    {
        var options = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var effort in new[] { "none", "low", "medium", "high", "xhigh", "max" })
            options[effort] = new JsonObject { ["reasoning"] = new JsonObject { ["effort"] = effort } };
        Capabilities[Key("openai", modelId)] = new("openai", modelId, "openai_responses", options, null, SupportsNativeTools: true);
    }

    private static void RegisterAnthropicAdaptive(string modelId, string[] efforts, int maxOutput)
    {
        var options = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var effort in efforts)
            options[effort] = new JsonObject
            {
                ["thinking"] = new JsonObject { ["type"] = "adaptive", ["display"] = "omitted" },
                ["output_config"] = new JsonObject { ["effort"] = effort },
            };
        Capabilities[Key("anthropic", modelId)] = new("anthropic", modelId, "anthropic_messages", options, maxOutput);
    }

    private static void RegisterAnthropicDefaultOnly(string modelId, int maxOutput)
    {
        Capabilities[Key("anthropic", modelId)] = new("anthropic", modelId, "anthropic_messages", new Dictionary<string, JsonObject>(StringComparer.Ordinal), maxOutput);
    }

    private static string Key(string alias, string modelId) => alias + "\n" + modelId;
}
