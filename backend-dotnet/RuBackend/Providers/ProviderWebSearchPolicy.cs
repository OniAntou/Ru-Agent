using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RuAgent.Harness.Persistence;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuBackend.Business.Settings;

namespace RuBackend.Providers;

/// <summary>
/// Provider-hosted web search (ADR 0033). Whether a run may search through its provider
/// is decided once, when its selection is pinned, and stored as a marker that is removed
/// before any option reaches a provider. The run's policy then decides whether the
/// marker is used.
/// </summary>
internal static partial class ProviderWebSearchPolicy
{
    internal const string SettingField = "provider_web_search_enabled";
    internal const string PinField = "_ru_provider_web_search_v1";

    /// <summary>On unless the user turned it off.</summary>
    internal static bool IsEnabled(SettingsSnapshot settings) =>
        settings.Raw[SettingField] is not JsonValue value || !value.TryGetValue<bool>(out var enabled) || enabled;

    /// <summary>Applies a settings save's choice; anything but a boolean is an invalid payload.</summary>
    internal static void Apply(JsonObject payload, JsonObject raw)
    {
        if (!payload.TryGetPropertyValue(SettingField, out var node)) return;
        if (node is not JsonValue value || !value.TryGetValue<bool>(out var enabled))
            throw new ArgumentException("provider_web_search_enabled must be a boolean.", nameof(payload));
        raw[SettingField] = enabled;
    }

    /// <summary>
    /// Whether a selection's provider runs web search for this model. The model rule is
    /// Ru's own conservative choice: the provider catalog Ru reads does not record the
    /// capability, and a model the provider rejects would fail the run.
    /// </summary>
    internal static bool Supports(ProviderSelection selection, JsonObject options)
    {
        var model = selection.Model ?? string.Empty;
        if (selection.Alias == "anthropic" && selection.Protocol == ProviderProtocols.AnthropicMessages)
            return IsFirstParty(selection.BaseUri, "api.anthropic.com") && AnthropicModel().IsMatch(model);
        if (selection.Alias == "openai" && selection.Protocol == ProviderProtocols.OpenAiResponses)
        {
            // OpenAI does not run search for gpt-5 at minimal reasoning effort.
            var effort = options["reasoning"] is JsonObject reasoning && reasoning["effort"] is JsonValue value
                && value.TryGetValue<string>(out var text) ? text : null;
            return IsFirstParty(selection.BaseUri, "api.openai.com")
                && OpenAiModel().IsMatch(model)
                && !OpenAiExcludedModel().IsMatch(model)
                && effort != "minimal";
        }
        return false;
    }

    /// <summary>Removes the marker from options bound for a provider; a malformed marker fails closed.</summary>
    internal static bool TakePinnedCapability(JsonObject options)
    {
        if (!options.Remove(PinField, out var marker)) return false;
        if (marker is not JsonValue value || !value.TryGetValue<bool>(out var pinned) || !pinned)
            throw new ProviderCatalogException("provider_selection_invalid");
        return true;
    }

    /// <summary>Whether a run pinned provider search; runs pinned before this marker never use it.</summary>
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

    private static bool IsFirstParty(Uri baseUri, string host) =>
        baseUri.Scheme == Uri.UriSchemeHttps && string.Equals(baseUri.Host, host, StringComparison.OrdinalIgnoreCase);

    // Claude models of generation 4 and later ("claude-opus-4-1", "claude-sonnet-5-5").
    [GeneratedRegex(@"^claude-[a-z]+-([4-9]|[1-9][0-9])(\b|[-.])", RegexOptions.CultureInvariant)]
    private static partial Regex AnthropicModel();

    [GeneratedRegex(@"^(gpt-([5-9]|[1-9][0-9])(\.[0-9]+)?|gpt-4\.1|gpt-4o)(-|$)", RegexOptions.CultureInvariant)]
    private static partial Regex OpenAiModel();

    // Sizes and modalities Ru does not trust to support the hosted tool.
    [GeneratedRegex(@"(nano|audio|realtime|transcribe|tts|image|search)", RegexOptions.CultureInvariant)]
    private static partial Regex OpenAiExcludedModel();
}
