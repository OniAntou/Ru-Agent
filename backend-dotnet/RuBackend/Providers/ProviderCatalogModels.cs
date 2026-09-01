using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RuBackend.Providers;

public sealed record CatalogReasoning(string State, IReadOnlyList<string> Options);
public sealed record CatalogModel(string Id, bool Supported, string Protocol, CatalogReasoning Reasoning)
{
    [JsonIgnore] public IReadOnlyDictionary<string, JsonObject> WireOptions { get; init; }
        = new Dictionary<string, JsonObject>();
}

public sealed record ProviderCatalogSnapshot(
    string Status, string Backend, string Identity, string SelectedModel, string SelectedEffort,
    IReadOnlyList<string> Models, IReadOnlyList<CatalogModel> Catalog, bool Stale,
    string? ErrorCode, DateTimeOffset? LastSuccessUtc)
{
    [JsonIgnore] public string CredentialRevision { get; init; } = "";
    public JsonObject ToJson() => new()
    {
        ["status"] = Status, ["backend"] = Backend, ["identity"] = Identity,
        ["selected_model"] = SelectedModel, ["selected_effort"] = SelectedEffort,
        ["models"] = new JsonArray(Models.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
        ["catalog"] = new JsonArray(Catalog.Select(model => (JsonNode?)new JsonObject
        {
            ["id"] = model.Id, ["supported"] = model.Supported, ["protocol"] = model.Protocol,
            ["reasoning"] = new JsonObject
            {
                ["state"] = model.Reasoning.State,
                ["options"] = new JsonArray(model.Reasoning.Options.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            },
        }).ToArray()),
        ["stale"] = Stale, ["error_code"] = ErrorCode,
        ["last_success_utc"] = LastSuccessUtc?.ToString("O"),
    };
}

public sealed class ProviderCatalogException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
