using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Persistence;
using RuBackend.Business.Settings;

namespace RuBackend.Providers;

/// <summary>
/// The computer-use opt-in (ADR 0031). The Host setting is read once, when a new run's
/// provider selection is pinned, and only together with image input; the run then
/// carries a marker that is removed before any option reaches a provider.
/// </summary>
internal static class ComputerUsePolicy
{
    internal const string SettingField = "computer_use_enabled";
    internal const string PinField = "_ru_computer_use_v1";

    internal static bool IsEnabled(SettingsSnapshot settings) =>
        settings.Raw[SettingField] is JsonValue value && value.TryGetValue<bool>(out var enabled) && enabled;

    /// <summary>Applies a settings save's opt-in; anything but a boolean is an invalid payload.</summary>
    internal static void Apply(JsonObject payload, JsonObject raw)
    {
        if (!payload.TryGetPropertyValue(SettingField, out var node)) return;
        if (node is not JsonValue value || !value.TryGetValue<bool>(out var enabled))
            throw new ArgumentException("computer_use_enabled must be a boolean.", nameof(payload));
        raw[SettingField] = enabled;
    }

    /// <summary>Removes the marker from options bound for a provider; a malformed marker fails closed.</summary>
    internal static bool TakePinnedCapability(JsonObject options)
    {
        if (!options.Remove(PinField, out var marker)) return false;
        if (marker is not JsonValue value || !value.TryGetValue<bool>(out var pinned) || !pinned)
            throw new ProviderCatalogException("provider_selection_invalid");
        return true;
    }

    /// <summary>Whether a run pinned computer use; runs pinned before this marker never use it.</summary>
    internal static bool IsPinned(AgentRunProviderSelection? selection)
    {
        if (selection is null) return false;
        try
        {
            return JsonNode.Parse(selection.RequestOptionsJson) is JsonObject options
                && options[PinField] is JsonValue value
                && value.TryGetValue<bool>(out var pinned)
                && pinned;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
