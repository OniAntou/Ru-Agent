namespace RuBackend.Providers;

/// <summary>One Models.dev provider entry: generic chat_completions base URL.</summary>
public sealed record ModelsDevProvider(
    string Id,
    string Name,
    string BaseUrl,
    IReadOnlyList<string> Models,
    IReadOnlyDictionary<string, CatalogReasoning>? Reasoning = null,
    IReadOnlyDictionary<string, CatalogCost>? Costs = null,
    IReadOnlyDictionary<string, bool>? Vision = null,
    IReadOnlyDictionary<string, int>? Contexts = null);
