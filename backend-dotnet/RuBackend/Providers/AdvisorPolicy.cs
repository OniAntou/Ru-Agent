using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Persistence;
using RuBackend.Business.Settings;

namespace RuBackend.Providers;

/// <summary>
/// A run's pinned advisor: a secret-free selection, the named reason the selected one is
/// unavailable, or neither when the user selected none (a user-named model can still be consulted).
/// </summary>
internal sealed record AdvisorPin(AgentRunProviderSelection? Selection, string? UnavailableCode)
{
    internal static AdvisorPin NotSelected { get; } = new(null, null);

    internal bool IsSelected => Selection is not null || UnavailableCode is not null;
}

/// <summary>
/// The advisor opt-in (ADR 0047). Each session names its own advisor, a signed-in provider
/// and model, or none (where a new session starts); when a new root run pins its selection,
/// the session's advisor is resolved once and stored in the <c>_ru_advisor_v1</c> marker,
/// which is removed before any option reaches a provider. A change applies to the session's
/// new runs only.
/// </summary>
internal static class AdvisorPolicy
{
    internal const string BackendField = "advisor_backend";
    internal const string ModelField = "advisor_model";
    internal const string PinField = "_ru_advisor_v1";
    internal const string UnavailableCode = "advisor_unavailable";
    private const int MaximumModelChars = 256;

    /// <summary>
    /// A session advisor request (<c>PUT /agent/v1/sessions/{id}/advisor</c>). Both fields
    /// travel together: two strings select an advisor and two nulls clear it; anything else
    /// is invalid. The backend is normalized like a settings save.
    /// </summary>
    internal static RuAgent.Harness.Core.RunProviderIntent? ParseSessionRequest(JsonObject payload, Func<string, string> normalizeBackend)
    {
        if (!payload.TryGetPropertyValue(BackendField, out var backendNode) || !payload.TryGetPropertyValue(ModelField, out var modelNode))
            throw new ArgumentException("advisor_backend and advisor_model are sent together.", nameof(payload));
        if (backendNode is null && modelNode is null) return null;
        if (backendNode is not JsonValue backendValue || !backendValue.TryGetValue<string>(out var backend)
            || modelNode is not JsonValue modelValue || !modelValue.TryGetValue<string>(out var model))
            throw new ArgumentException("advisor_backend and advisor_model must be strings or both null.", nameof(payload));
        model = model.Trim();
        if (model.Length == 0 || model.Length > MaximumModelChars || model.Any(char.IsControl))
            throw new ArgumentException("advisor_model is invalid.", nameof(payload));
        return new RuAgent.Harness.Core.RunProviderIntent(normalizeBackend(backend.Trim()), model);
    }

    /// <summary>The settings a resolver uses to describe the advisor as if it were the selected model.</summary>
    internal static SettingsSnapshot CandidateFor(SettingsSnapshot current, string backend, string model) =>
        current with
        {
            Backend = backend,
            Model = model,
            CustomModel = model,
            OpenAiModel = model,
            AnthropicModel = model,
        };

    /// <summary>The marker for a resolved advisor. Its request options carry no Ru markers.</summary>
    internal static JsonObject Marker(AgentRunProviderSelection advisor)
    {
        JsonObject options;
        try { options = JsonNode.Parse(advisor.RequestOptionsJson) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { throw new ProviderCatalogException("provider_selection_invalid"); }
        foreach (var key in options.Select(entry => entry.Key).Where(IsRuMarker).ToArray()) options.Remove(key);
        return new JsonObject
        {
            ["backend"] = advisor.Backend,
            ["alias"] = advisor.Alias,
            ["protocol"] = advisor.Protocol,
            ["base_uri"] = advisor.BaseUri,
            ["model"] = advisor.Model,
            ["account"] = advisor.Account,
            ["request_options"] = options,
            ["context_window_tokens"] = advisor.ContextWindowTokens,
            ["context_window_source"] = advisor.ContextWindowSource,
            ["context_window_version"] = advisor.ContextWindowVersion,
            ["output_token_reserve"] = advisor.OutputTokenReserve,
        };
    }

    /// <summary>The marker for an advisor that could not be resolved when the run was pinned.</summary>
    internal static JsonObject UnavailableMarker() => new() { ["unavailable"] = UnavailableCode };

    /// <summary>The marker for a run whose user selected no advisor; it can still consult a model they name.</summary>
    internal static JsonObject NotSelectedMarker() => new() { ["selected"] = false };

    /// <summary>Removes the marker from options bound for a provider; a malformed marker fails closed.</summary>
    internal static AdvisorPin? TakePinnedCapability(JsonObject options)
    {
        if (!options.Remove(PinField, out var marker)) return null;
        return Parse(marker) ?? throw new ProviderCatalogException("provider_selection_invalid");
    }

    /// <summary>The run's pinned advisor; runs pinned before this marker have none.</summary>
    internal static AdvisorPin? ReadPinned(AgentRunProviderSelection? selection)
    {
        if (selection is null) return null;
        try
        {
            return JsonNode.Parse(selection.RequestOptionsJson) is JsonObject options
                && options.TryGetPropertyValue(PinField, out var marker)
                ? Parse(marker) ?? throw new ProviderCatalogException("provider_selection_invalid")
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static AdvisorPin? Parse(JsonNode? marker)
    {
        if (marker is not JsonObject value) return null;
        if (value.Count == 1 && Text(value, "unavailable") == UnavailableCode)
            return new AdvisorPin(null, UnavailableCode);
        if (value.Count == 1 && value["selected"] is JsonValue selected
            && selected.TryGetValue<bool>(out var isSelected) && !isSelected)
            return AdvisorPin.NotSelected;
        var backend = Text(value, "backend");
        var alias = Text(value, "alias");
        var protocol = Text(value, "protocol");
        var baseUri = Text(value, "base_uri");
        var model = Text(value, "model");
        var source = Text(value, "context_window_source");
        var version = Text(value, "context_window_version");
        if (backend is null || alias is null || protocol is null || baseUri is null || model is null
            || source is null || version is null
            || value["request_options"] is not JsonObject options
            || value["output_token_reserve"] is not JsonValue reserveValue || !reserveValue.TryGetValue<int>(out var reserve)
            || reserve <= 0)
            return null;
        string? account = null;
        if (value["account"] is not null && (account = Text(value, "account")) is null) return null;
        long? window = null;
        if (value["context_window_tokens"] is not null)
        {
            if (value["context_window_tokens"] is not JsonValue windowValue || !windowValue.TryGetValue<long>(out var tokens)
                || tokens <= 0)
                return null;
            window = tokens;
        }

        return new AdvisorPin(new AgentRunProviderSelection(backend, alias, protocol, baseUri, model, account,
            options.ToJsonString(), null, model, AgentRunCallingModeCodec.Native, window, source, version, reserve), null);
    }

    private static string? Text(JsonObject value, string key) =>
        value[key] is JsonValue node && node.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static bool IsRuMarker(string key) => key.StartsWith("_ru_", StringComparison.Ordinal);
}
