namespace RuBackend.Providers;

/// <summary>
/// One Models.dev provider entry: its endpoint root and the wire protocol Ru reaches it
/// with (<see cref="ProviderRoutes"/>, ADR 0052).
/// </summary>
public sealed record ModelsDevProvider(
    string Id,
    string Name,
    string BaseUrl,
    IReadOnlyList<string> Models,
    IReadOnlyDictionary<string, CatalogReasoning>? Reasoning = null,
    IReadOnlyDictionary<string, CatalogCost>? Costs = null,
    IReadOnlyDictionary<string, bool>? Vision = null,
    IReadOnlyDictionary<string, int>? Contexts = null,
    IReadOnlyDictionary<string, int>? OutputLimits = null,
    IReadOnlyCollection<string>? ReasoningContentReplay = null,
    string Protocol = RuAgent.Runtime.Adapters.Bootstrap.ProviderProtocols.ChatCompletions);
