using System.Text.Json;
using System.Text.Json.Serialization;

namespace RuApp.Models;

public sealed class ProviderReasoningCapabilities
{
    public const string Levels = "levels";
    public const string Toggle = "toggle";
    public const string Unsupported = "unsupported";
    public const string Unknown = "unknown";

    [JsonPropertyName("state")]
    public string State { get; init; } = Unknown;

    private IReadOnlyList<string>? _options;

    [JsonPropertyName("options")]
    public IReadOnlyList<string> Options
    {
        get => _options ?? Array.Empty<string>();
        init => _options = value;
    }
}

public sealed class ProviderCatalogModel
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("supported")]
    public bool Supported { get; init; }

    [JsonPropertyName("protocol")]
    public string Protocol { get; init; } = string.Empty;

    [JsonPropertyName("context_length")]
    public int? ContextLength { get; init; }

    /// <summary>
    /// What the provider publishes for a million tokens, in US dollars.
    /// </summary>
    /// <remarks>
    /// Null means nobody published a price, which is not the same as a price of zero:
    /// a free model publishes <c>{input: 0, output: 0}</c> and must read as free.
    /// Only Models.dev publishes one, so a session's older runs on another backend
    /// will often have no price to look up.
    /// </remarks>
    [JsonPropertyName("cost")]
    public ProviderCatalogCost? Cost { get; init; }

    private ProviderReasoningCapabilities? _reasoning;

    [JsonPropertyName("reasoning")]
    public ProviderReasoningCapabilities Reasoning
    {
        get => _reasoning ?? new ProviderReasoningCapabilities();
        init => _reasoning = value;
    }
}

/// <summary>
/// Published rates in US dollars per million tokens.
/// </summary>
/// <remarks>
/// The two cache rates are nullable because a provider may price input and output
/// while publishing nothing for cache at all. That is a different statement from
/// publishing a cache rate of zero, so the two cannot collapse into one value.
/// </remarks>
public sealed class ProviderCatalogCost
{
    [JsonPropertyName("input")]
    public double Input { get; init; }

    [JsonPropertyName("output")]
    public double Output { get; init; }

    [JsonPropertyName("cache_read")]
    public double? CacheRead { get; init; }

    [JsonPropertyName("cache_write")]
    public double? CacheWrite { get; init; }
}

public sealed class ProviderCatalogSnapshot
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = "error";

    [JsonPropertyName("backend")]
    public string Backend { get; init; } = string.Empty;

    [JsonPropertyName("identity")]
    public string Identity { get; init; } = string.Empty;

    [JsonPropertyName("selected_model")]
    public string? SelectedModel { get; init; }

    [JsonPropertyName("selected_effort")]
    public string SelectedEffort { get; init; } = "default";

    private IReadOnlyList<string>? _models;
    private IReadOnlyList<ProviderCatalogModel>? _catalog;

    [JsonPropertyName("models")]
    public IReadOnlyList<string> Models
    {
        get => _models ?? Array.Empty<string>();
        init => _models = value;
    }

    [JsonPropertyName("catalog")]
    public IReadOnlyList<ProviderCatalogModel> Catalog
    {
        get => _catalog ?? Array.Empty<ProviderCatalogModel>();
        init => _catalog = value;
    }

    [JsonPropertyName("stale")]
    public bool Stale { get; init; }

    [JsonPropertyName("error_code")]
    public string? ErrorCode { get; init; }

    [JsonPropertyName("last_success_utc")]
    public DateTimeOffset? LastSuccessUtc { get; init; }

    /// <summary>
    /// When <see cref="ErrorCode"/> was recorded, or null when there is no error.
    /// </summary>
    /// <remarks>
    /// A failed read stays reported while the backend's retry throttle holds, during
    /// which the provider may already be healthy. An error with an old timestamp is a
    /// stale report, not a live one.
    /// </remarks>
    [JsonPropertyName("error_at_utc")]
    public DateTimeOffset? ErrorAtUtc { get; init; }

    public bool IsSuccess => string.Equals(Status, "ok", StringComparison.Ordinal);
    public bool IsValidEmpty => IsSuccess && Models.Count == 0;

    public static ProviderCatalogSnapshot Parse(string json) =>
        JsonSerializer.Deserialize<ProviderCatalogSnapshot>(json)
        ?? throw new JsonException("Provider catalog response was null.");

    public static ProviderCatalogSnapshot Success(
        string backend,
        string identity,
        string selectedModel,
        string selectedEffort,
        IReadOnlyList<string> models) => new()
        {
            Status = "ok",
            Backend = backend,
            Identity = identity,
            SelectedModel = selectedModel,
            SelectedEffort = selectedEffort,
            Models = models,
            LastSuccessUtc = DateTimeOffset.UtcNow,
        };
}

public sealed record ProviderCatalogState
{
    public string Status { get; init; } = "error";
    public string Backend { get; init; } = string.Empty;
    public string Identity { get; init; } = string.Empty;
    public string SelectedModel { get; init; } = string.Empty;
    public string SelectedEffort { get; init; } = "default";
    public IReadOnlyList<string> Models { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ProviderCatalogModel> Catalog { get; init; } = Array.Empty<ProviderCatalogModel>();
    public bool Stale { get; init; }
    public string? ErrorCode { get; init; }
    public DateTimeOffset? LastSuccessUtc { get; init; }

    /// <summary>When <see cref="ErrorCode"/> was recorded; an old date means a stale report.</summary>
    public DateTimeOffset? ErrorAtUtc { get; init; }

    public string SearchText { get; init; } = string.Empty;

    public bool IsSuccess => string.Equals(Status, "ok", StringComparison.Ordinal);
    public bool IsValidEmpty => IsSuccess && Models.Count == 0;

    public static ProviderCatalogState Empty(string backend, string searchText = "") => new()
    {
        Backend = backend,
        SearchText = searchText,
    };

    public static ProviderCatalogState FromSnapshot(ProviderCatalogSnapshot snapshot, string searchText) => new()
    {
        Status = snapshot.Status,
        Backend = snapshot.Backend,
        Identity = snapshot.Identity,
        SelectedModel = snapshot.SelectedModel ?? string.Empty,
        SelectedEffort = string.IsNullOrWhiteSpace(snapshot.SelectedEffort) ? "default" : snapshot.SelectedEffort,
        Models = snapshot.Models,
        Catalog = snapshot.Catalog,
        Stale = snapshot.Stale,
        ErrorCode = snapshot.ErrorCode,
        LastSuccessUtc = snapshot.LastSuccessUtc,
        ErrorAtUtc = snapshot.ErrorAtUtc,
        SearchText = searchText,
    };
}

public sealed record ProviderReasoningOption(string Value, string Label, bool IsEnabled = true);

public static class ProviderReasoningOptions
{
    private static bool IsReasoningCapable(string? state) =>
        string.Equals(state?.Trim(), ProviderReasoningCapabilities.Levels, StringComparison.OrdinalIgnoreCase)
        || string.Equals(state?.Trim(), ProviderReasoningCapabilities.Toggle, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<ProviderReasoningOption> For(ProviderCatalogState state) =>
        For(state, selectedModelOverride: null);

    /// <summary>
    /// Drops a stored effort the catalog does not offer, but only once the
    /// catalog is actually there.
    /// </summary>
    /// <remarks>
    /// A catalog that has not loaded yet yields a "default"-only option list.
    /// Clamping against that erased a choice the backend had stored, so a
    /// selection that saved correctly still snapped the button back to "default"
    /// on the next UI update. When the catalog is ready the clamp is unchanged:
    /// a model that genuinely has no such level falls back to "default".
    /// </remarks>
    public static string Clamp(
        string current,
        IReadOnlyList<ProviderReasoningOption> options,
        bool isCatalogReady)
    {
        if (!isCatalogReady) return current;
        return options.Any(option => string.Equals(option.Value, current, StringComparison.OrdinalIgnoreCase))
            ? current
            : "default";
    }

    public static IReadOnlyList<ProviderReasoningOption> For(ProviderCatalogState state, string? selectedModelOverride)
    {
        var options = new List<ProviderReasoningOption>
        {
            new("default", "default")
        };

        if (!state.IsSuccess || state.Stale) return options;

        // Grouped selection can be newer than the child snapshot after a model
        // switch (server SelectedModel lags until refresh). Prefer the caller
        // override so the picker updates instantly without app restart.
        string lookup = !string.IsNullOrWhiteSpace(selectedModelOverride)
            ? selectedModelOverride!
            : state.SelectedModel;
        var model = state.Catalog.FirstOrDefault(item =>
            string.Equals(item.Id, lookup, StringComparison.OrdinalIgnoreCase));
        if (model is null || !model.Supported || !IsReasoningCapable(model.Reasoning.State))
        {
            return options;
        }

        foreach (var option in model.Reasoning.Options)
        {
            if (!string.IsNullOrWhiteSpace(option) &&
                !string.Equals(option, "default", StringComparison.OrdinalIgnoreCase) &&
                options.All(existing => !string.Equals(existing.Value, option, StringComparison.OrdinalIgnoreCase)))
            {
                options.Add(new ProviderReasoningOption(option, option));
            }
        }

        return options;
    }
}

/// <summary>
/// Picks the catalog that describes the selected backend, if one has arrived.
/// </summary>
/// <remarks>
/// The composer reads the single selected-backend controller first: the view
/// points it at the selected backend and refreshes it on load and on every
/// model switch, so it carries SelectedEffort, Identity and reasoning levels
/// without waiting for the grouped controller's two hundred children. The
/// grouped entry is the fallback for the window between a switch and the
/// single refresh landing, or when Settings repointed the shared controller.
/// A catalog that is missing, failed or stale says nothing: the caller keeps
/// whatever it had instead of concluding the effort is "default".
/// </remarks>
public static class ProviderSelectedCatalog
{
    public static bool TryResolve(
        ProviderCatalogState? single,
        IReadOnlyDictionary<string, ProviderCatalogState>? grouped,
        string? backend,
        out ProviderCatalogState? catalog)
    {
        if (!string.IsNullOrWhiteSpace(backend))
        {
            if (single is not null &&
                string.Equals(single.Backend, backend, StringComparison.Ordinal) &&
                single.IsSuccess &&
                !single.Stale)
            {
                catalog = single;
                return true;
            }

            if (grouped is not null &&
                grouped.TryGetValue(backend, out var entry) &&
                entry is not null &&
                entry.IsSuccess &&
                !entry.Stale)
            {
                catalog = entry;
                return true;
            }
        }

        catalog = null;
        return false;
    }
}

public sealed class ProviderSettingsUpdateDto
{
    [JsonPropertyName("backend_choice")]
    public string BackendChoice { get; init; } = "openai";

    [JsonPropertyName("openai_api_key")]
    public string OpenAiApiKey { get; init; } = string.Empty;

    [JsonPropertyName("anthropic_api_key")]
    public string AnthropicApiKey { get; init; } = string.Empty;


    [JsonPropertyName("custom_url")]
    public string CustomUrl { get; init; } = "http://localhost:8000/v1";

    [JsonPropertyName("custom_api_key")]
    public string CustomApiKey { get; init; } = string.Empty;

    [JsonPropertyName("cloudflare_account_id")]
    public string CloudflareAccountId { get; init; } = string.Empty;

}

public sealed class ModelsDevEntryDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("base_url")]
    public string BaseUrl { get; init; } = string.Empty;
}

public sealed class ModelsDevListDto
{
    [JsonPropertyName("providers")]
    public List<ModelsDevEntryDto> Providers { get; init; } = [];
}

public sealed class ModelsDevKeysDto
{
    [JsonPropertyName("provider_ids")]
    public List<string> ProviderIds { get; init; } = [];
}

public sealed class ReasoningSettingsUpdateDto
{
    [JsonPropertyName("reasoning_effort")]
    public string ReasoningEffort { get; init; } = "default";

    [JsonPropertyName("reasoning_model")]
    public string ReasoningModel { get; init; } = string.Empty;

    [JsonPropertyName("reasoning_identity")]
    public string ReasoningIdentity { get; init; } = string.Empty;
}

public sealed class ProviderModelSelectionUpdateDto
{
    [JsonPropertyName("backend_choice")]
    public string BackendChoice { get; init; } = string.Empty;

    [JsonPropertyName("selected_model")]
    public string SelectedModel { get; init; } = string.Empty;
}
