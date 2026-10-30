using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Persistence;
using RuAgent.Runtime.Adapters.Models;

namespace RuBackend.Providers;

/// <summary>
/// Whether a run's pinned model accepts image input, decided once when the provider
/// selection is pinned (ADRs 0026 and 0040). The catalog's declaration wins;
/// the model-name check is the fallback. Text-only runs retain client images but
/// receive an explicit unavailable-image notice instead of image wire parts.
/// The decision is stored as a marker in the pinned request options, like the chat
/// reasoning capability, and removed before any option reaches a provider.
/// </summary>
internal static class ImageInputPolicy
{
    internal const string PinField = "_ru_image_input_v1";

    internal static async Task<bool> AcceptsAsync(
        ProviderCatalogService catalog,
        ProviderSelection selection,
        CancellationToken cancellationToken)
    {
        bool? declared;
        try
        {
            declared = await catalog.DeclaresVisionAsync(selection.Backend, selection.Model, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // An unreadable catalog says nothing about the model; the name check decides.
            declared = null;
        }

        return ModelImage.IsVisionModel(selection.Model, declared);
    }

    /// <summary>Removes the marker from options bound for a provider; a malformed marker fails closed.</summary>
    internal static bool TakePinnedCapability(JsonObject options)
    {
        if (!options.Remove(PinField, out var marker)) return false;
        if (marker is not JsonValue value || !value.TryGetValue<bool>(out var accepted) || !accepted)
            throw new ProviderCatalogException("provider_selection_invalid");
        return true;
    }

    /// <summary>The run's pinned decision; a run without a pin, or pinned before this marker, accepts no images.</summary>
    internal static bool IsPinned(AgentRunProviderSelection? selection)
    {
        if (selection is null) return false;
        try
        {
            return JsonNode.Parse(selection.RequestOptionsJson) is JsonObject options
                && options[PinField] is JsonValue value
                && value.TryGetValue<bool>(out var accepted)
                && accepted;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
