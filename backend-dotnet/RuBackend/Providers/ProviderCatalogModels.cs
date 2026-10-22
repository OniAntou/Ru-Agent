using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RuBackend.Providers;

public sealed record CatalogReasoning(string State, IReadOnlyList<string> Options);

/// <summary>
/// What a provider publishes for one million tokens, in US dollars.
/// </summary>
/// <remarks>
/// A zero here is a real price — models.dev publishes <c>{input:0, output:0}</c> for
/// a free model — so absent and zero are different answers and must not collapse into
/// each other. <see cref="CacheRead"/> and <see cref="CacheWrite"/> are null when the
/// provider publishes no rate for them at all, which is different again from publishing
/// a rate of zero.
/// </remarks>
public sealed record CatalogCost(
    double Input,
    double Output,
    double? CacheRead = null,
    double? CacheWrite = null);

public sealed record CatalogModel(string Id, bool Supported, string Protocol, CatalogReasoning Reasoning)
{
    [JsonIgnore] public IReadOnlyDictionary<string, JsonObject> WireOptions { get; init; }
        = new Dictionary<string, JsonObject>();
    public int? ContextLength { get; init; }

    /// <summary>The largest output the model accepts, when its catalog publishes one.</summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>
    /// The published price, or null when nothing published one. Only Models.dev
    /// supplies this; a provider's own <c>/models</c> list does not.
    /// </summary>
    public CatalogCost? Cost { get; init; }
}

public sealed record ProviderCatalogSnapshot(
    string Status, string Backend, string Identity, string SelectedModel, string SelectedEffort,
    IReadOnlyList<string> Models, IReadOnlyList<CatalogModel> Catalog, bool Stale,
    string? ErrorCode, DateTimeOffset? LastSuccessUtc)
{
    [JsonIgnore] public string CredentialRevision { get; init; } = "";

    /// <summary>
    /// When <see cref="ErrorCode"/> was recorded, or null when there is no error.
    /// </summary>
    /// <remarks>
    /// A failed read stays reported for as long as the retry throttle holds, during
    /// which the provider may already be healthy again. Without this timestamp a
    /// fifteen-second-old rejection reads exactly like one happening now, which sends
    /// the user to re-enter a key that was never the problem.
    /// </remarks>
    public DateTimeOffset? ErrorAtUtc { get; init; }

    public JsonObject ToJson() => new()
    {
        ["status"] = Status, ["backend"] = Backend, ["identity"] = Identity,
        ["selected_model"] = SelectedModel, ["selected_effort"] = SelectedEffort,
        ["models"] = new JsonArray(Models.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
        ["catalog"] = new JsonArray(Catalog.Select(model =>
        {
            var node = new JsonObject
            {
                ["id"] = model.Id,
                ["supported"] = model.Supported,
                ["protocol"] = model.Protocol,
                ["reasoning"] = new JsonObject
                {
                    ["state"] = model.Reasoning.State,
                    ["options"] = new JsonArray(model.Reasoning.Options.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                },
            };
            if (model.ContextLength.HasValue)
            {
                node["context_length"] = model.ContextLength.Value;
            }
            if (model.Cost is { } cost)
            {
                // Written whole, including a rate of zero: a free model that published
                // zero must read as free, not as unpublished. A null cache rate is left
                // out, so "this provider prices cache reads" and "it does not" stay
                // distinguishable on the wire.
                var costNode = new JsonObject
                {
                    ["input"] = cost.Input,
                    ["output"] = cost.Output,
                };
                if (cost.CacheRead is { } cacheRead) costNode["cache_read"] = cacheRead;
                if (cost.CacheWrite is { } cacheWrite) costNode["cache_write"] = cacheWrite;
                node["cost"] = costNode;
            }
            return (JsonNode?)node;
        }).ToArray()),
        ["stale"] = Stale, ["error_code"] = ErrorCode,
        ["last_success_utc"] = LastSuccessUtc?.ToString("O"),
        ["error_at_utc"] = ErrorAtUtc?.ToString("O"),
    };
}

public sealed class ProviderCatalogException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>The only reasoning states the picker knows how to present.</summary>
internal static class CatalogReasoningStates
{
    public const string Levels = "levels";
    public const string Unknown = "unknown";
}
