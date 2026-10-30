using RuAgent.Harness.Core;
using RuBackend.Business.Settings;

namespace RuBackend.Providers;

internal static class ProviderIntentSettings
{
    internal static SettingsSnapshot Apply(SettingsSnapshot current, RunProviderIntent? intent)
    {
        if (intent is null) return current;
        var raw = current.Raw.DeepClone().AsObject();
        raw["backend"] = intent.Backend;
        foreach (var field in new[] { "model", "custom_model", "openai_model", "anthropic_model" }) raw[field] = intent.Model;
        return current with { Raw = raw, Backend = intent.Backend, Model = intent.Model,
            CustomModel = intent.Model, OpenAiModel = intent.Model, AnthropicModel = intent.Model };
    }
}
