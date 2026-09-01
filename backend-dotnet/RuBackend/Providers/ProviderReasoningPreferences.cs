using System.Text.Json.Nodes;
using RuBackend.Business.Settings;

namespace RuBackend.Providers;

public static class ProviderReasoningPreferences
{
    private const string Field = "reasoning_preferences";
    public const string InvalidChoice = "invalid_saved_choice";

    public static string Read(SettingsSnapshot settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Backend)) return "default";
        var selection = ProviderSelection.From(settings);
        if (settings.Raw.TryGetPropertyValue(Field, out var preferencesNode))
        {
            if (preferencesNode is not JsonObject saved) return InvalidChoice;
            if (saved.TryGetPropertyValue(selection.PreferenceKey, out var savedNode))
            {
                if (savedNode is JsonValue value && value.TryGetValue<string>(out var choice)
                    && choice.Length is > 0 and <= 32 && !choice.Any(char.IsControl)) return choice;
                return InvalidChoice;
            }
        }
        // Legacy controls were scoped to their provider, not other accounts/models.
        if (settings.Backend == "OpenCode Zen" && !string.IsNullOrWhiteSpace(settings.ApiVariant)
            && !settings.ApiVariant.Equals("Default", StringComparison.OrdinalIgnoreCase)) return settings.ApiVariant;
        if (settings.Backend == "Kilo Gateway" && settings.KiloReasoningEnabled) return "enabled";
        return "default";
    }

    public static SettingsSnapshot Save(SettingsSnapshot settings, string effort)
    {
        var raw = settings.Raw.DeepClone().AsObject();
        var choices = raw[Field] as JsonObject ?? new JsonObject();
        if (choices.Count >= 256 && !choices.ContainsKey(ProviderSelection.From(settings).PreferenceKey))
            throw new ProviderCatalogException("reasoning_preferences_full");
        choices[ProviderSelection.From(settings).PreferenceKey] = effort;
        raw[Field] = choices.DeepClone();
        return settings with { Raw = raw };
    }

    public static SettingsSnapshot CaptureLegacy(SettingsSnapshot settings)
    {
        var result = settings;
        if (!string.IsNullOrWhiteSpace(settings.ApiVariant) && !settings.ApiVariant.Equals("Default", StringComparison.OrdinalIgnoreCase))
        {
            var legacy = result with { Backend = "OpenCode Zen" };
            if ((result.Raw[Field] as JsonObject)?[ProviderSelection.From(legacy).PreferenceKey] is null)
                result = result with { Raw = Save(legacy, settings.ApiVariant).Raw };
        }
        if (settings.KiloReasoningEnabled)
        {
            var legacy = result with { Backend = "Kilo Gateway" };
            if ((result.Raw[Field] as JsonObject)?[ProviderSelection.From(legacy).PreferenceKey] is null)
                result = result with { Raw = Save(legacy, "enabled").Raw };
        }
        return result with { ApiVariant = "Default", KiloReasoningEnabled = false };
    }

    public static JsonObject Resolve(ProviderCatalogSnapshot snapshot, string model, string effort)
    {
        if (snapshot.Stale || snapshot.Status != "ok") throw new ProviderCatalogException(snapshot.ErrorCode ?? "catalog_unavailable");
        var selected = snapshot.Catalog.FirstOrDefault(item => item.Id == model)
            ?? throw new ProviderCatalogException("model_unavailable");
        if (!selected.Supported) throw new ProviderCatalogException("model_protocol_unsupported");
        if (effort == "default") return new JsonObject();
        if (!selected.WireOptions.TryGetValue(effort, out var options)) throw new ProviderCatalogException("reasoning_unsupported");
        return options.DeepClone().AsObject();
    }
}
