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

    private ProviderReasoningCapabilities? _reasoning;

    [JsonPropertyName("reasoning")]
    public ProviderReasoningCapabilities Reasoning
    {
        get => _reasoning ?? new ProviderReasoningCapabilities();
        init => _reasoning = value;
    }
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
        SearchText = searchText,
    };
}

public sealed record ProviderReasoningOption(string Value, string Label, bool IsEnabled = true);

public static class ProviderReasoningOptions
{
    public static IReadOnlyList<ProviderReasoningOption> For(ProviderCatalogState state)
    {
        var options = new List<ProviderReasoningOption>
        {
            new("default", "Provider default")
        };

        if (!state.IsSuccess || state.Stale) return options;

        var model = state.Catalog.FirstOrDefault(item =>
            string.Equals(item.Id, state.SelectedModel, StringComparison.Ordinal));
        if (model is null || !model.Supported || model.Reasoning.State is not
            (ProviderReasoningCapabilities.Levels or ProviderReasoningCapabilities.Toggle))
        {
            return options;
        }

        foreach (var option in model.Reasoning.Options)
        {
            if (!string.IsNullOrWhiteSpace(option) &&
                !string.Equals(option, "default", StringComparison.Ordinal) &&
                options.All(existing => !string.Equals(existing.Value, option, StringComparison.Ordinal)))
            {
                options.Add(new ProviderReasoningOption(option, option));
            }
        }

        return options;
    }
}

public sealed class ProviderSettingsUpdateDto
{
    [JsonPropertyName("backend_choice")]
    public string BackendChoice { get; init; } = "opencode";

    [JsonPropertyName("opencode_api_key")]
    public string OpenCodeApiKey { get; init; } = string.Empty;

    [JsonPropertyName("kilo_api_key")]
    public string KiloApiKey { get; init; } = string.Empty;

    [JsonPropertyName("openai_api_key")]
    public string OpenAiApiKey { get; init; } = string.Empty;

    [JsonPropertyName("anthropic_api_key")]
    public string AnthropicApiKey { get; init; } = string.Empty;

    [JsonPropertyName("gemini_vision_api_key")]
    public string GeminiVisionApiKey { get; init; } = string.Empty;

    [JsonPropertyName("custom_url")]
    public string CustomUrl { get; init; } = "http://localhost:8000/v1";

    [JsonPropertyName("custom_api_key")]
    public string CustomApiKey { get; init; } = string.Empty;

    [JsonPropertyName("self_learning_enabled")]
    public bool SelfLearningEnabled { get; init; }

    [JsonPropertyName("cloud_memory_consent")]
    public bool CloudMemoryConsent { get; init; }
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

public sealed class ModelSettingsUpdateDto
{
    [JsonPropertyName("opencode_model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OpenCodeModel { get; init; }

    [JsonPropertyName("kilo_model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? KiloModel { get; init; }

    [JsonPropertyName("ollama_model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OllamaModel { get; init; }

    [JsonPropertyName("lmstudio_model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LmStudioModel { get; init; }

    [JsonPropertyName("custom_model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CustomModel { get; init; }

    [JsonPropertyName("openai_model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OpenAiModel { get; init; }

    [JsonPropertyName("anthropic_model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AnthropicModel { get; init; }
}
