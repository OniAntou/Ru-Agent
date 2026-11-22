using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Persistence;

namespace RuBackend.Providers;

/// <summary>
/// The built-in browser capability marker (ADR 0055). Every new run pins it, so a run created
/// before the tool existed never sees it; the marker is removed before any option reaches a
/// provider and a malformed marker fails closed.
/// </summary>
internal static class BrowserPolicy
{
    internal const string PinField = "_ru_browser_v1";

    internal static void PinNew(JsonObject options) => options[PinField] = true;

    /// <summary>Removes the marker from options bound for a provider; a malformed marker fails closed.</summary>
    internal static bool TakePinnedCapability(JsonObject options)
    {
        if (!options.Remove(PinField, out var marker)) return false;
        if (marker is not JsonValue value || !value.TryGetValue<bool>(out var pinned) || !pinned)
        {
            throw new ProviderCatalogException("provider_selection_invalid");
        }

        return true;
    }

    /// <summary>Whether a run pinned the built-in browser capability; earlier runs never did.</summary>
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
