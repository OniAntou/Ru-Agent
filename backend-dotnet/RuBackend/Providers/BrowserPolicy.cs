using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Persistence;
using RuBackend.Business.Settings;

namespace RuBackend.Providers;

/// <summary>
/// The built-in browser capability marker (ADR 0055). Every new run pins it, so a run created
/// before the tool existed never sees it; the marker is removed before any option reaches a
/// provider and a malformed marker fails closed. The persistent-profile opt-in (amendment
/// 2026-11-25) is a Host setting read once, when a new run pins its selection, like the
/// computer-use opt-in; a setting change applies to new runs only.
/// </summary>
internal static class BrowserPolicy
{
    internal const string PinField = "_ru_browser_v1";
    internal const string ProfilePinField = "_ru_browser_profile_v1";
    internal const string ProfileSettingField = "browser_persistent_profile";

    internal static void PinNew(JsonObject options) => options[PinField] = true;

    /// <summary>Pins the browser and, when the user opted in, Ru's persistent profile.</summary>
    internal static void PinNew(JsonObject options, SettingsSnapshot settings)
    {
        PinNew(options);
        if (IsPersistentProfileEnabled(settings)) options[ProfilePinField] = true;
    }

    internal static bool IsPersistentProfileEnabled(SettingsSnapshot settings) =>
        settings.Raw[ProfileSettingField] is JsonValue value && value.TryGetValue<bool>(out var enabled) && enabled;

    /// <summary>Applies a settings save's persistent-profile opt-in; anything but a boolean is an invalid payload.</summary>
    internal static void Apply(JsonObject payload, JsonObject raw)
    {
        if (!payload.TryGetPropertyValue(ProfileSettingField, out var node)) return;
        if (node is not JsonValue value || !value.TryGetValue<bool>(out var enabled))
            throw new ArgumentException(ProfileSettingField + " must be a boolean.", nameof(payload));
        raw[ProfileSettingField] = enabled;
    }

    /// <summary>Removes the markers from options bound for a provider; a malformed marker fails closed.</summary>
    internal static bool TakePinnedCapability(JsonObject options)
    {
        if (options.Remove(ProfilePinField, out var profile)
            && (profile is not JsonValue profileValue || !profileValue.TryGetValue<bool>(out var persistent) || !persistent))
        {
            throw new ProviderCatalogException("provider_selection_invalid");
        }

        if (!options.Remove(PinField, out var marker)) return false;
        if (marker is not JsonValue value || !value.TryGetValue<bool>(out var pinned) || !pinned)
        {
            throw new ProviderCatalogException("provider_selection_invalid");
        }

        return true;
    }

    /// <summary>Whether a run pinned the built-in browser capability; earlier runs never did.</summary>
    internal static bool IsPinned(AgentRunProviderSelection? selection) => PinnedFlag(selection, PinField);

    /// <summary>Whether a run pinned Ru's persistent browser profile; every other run uses an ephemeral one.</summary>
    internal static bool IsPersistentProfilePinned(AgentRunProviderSelection? selection) =>
        PinnedFlag(selection, PinField) && PinnedFlag(selection, ProfilePinField);

    private static bool PinnedFlag(AgentRunProviderSelection? selection, string field)
    {
        if (selection is null) return false;
        try
        {
            return JsonNode.Parse(selection.RequestOptionsJson) is JsonObject options
                && options[field] is JsonValue value
                && value.TryGetValue<bool>(out var pinned)
                && pinned;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
