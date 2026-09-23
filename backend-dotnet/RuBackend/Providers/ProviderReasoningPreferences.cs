using System.Text.Json.Nodes;
using RuBackend.Business.Settings;

namespace RuBackend.Providers;

public static class ProviderReasoningPreferences
{
    private const string Field = "reasoning_preferences";
    public const string InvalidChoice = "invalid_saved_choice";

    public static string Read(SettingsSnapshot settings, ProviderSelection? selection = null)
    {
        if (string.IsNullOrWhiteSpace(settings.Backend)) return "default";
        try
        {
            selection ??= ProviderSelection.From(settings);
        }
        catch (Exception error) when (error is ArgumentException or UnknownProviderAliasException)
        {
            // Unresolvable backend (generic without a fetched catalog): no
            // preference key can be built, so there is nothing to read.
            return "default";
        }
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
        // No stored preference: every provider starts at default effort.
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
