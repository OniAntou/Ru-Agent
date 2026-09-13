namespace RuApp.Models;

public sealed record ProviderGroupDefinition(
    string Backend,
    string Alias,
    string DisplayName,
    int Order);

public static class ProviderGroupDefinitions
{
    public static IReadOnlyList<ProviderGroupDefinition> All { get; } =
    [
        new("OpenCode Zen", "opencode", "OpenCode", 0),
        new("Kilo Gateway", "kilo", "Kilo", 1),
        new("OpenAI", "openai", "OpenAI", 2),
        new("Anthropic", "anthropic", "Anthropic", 3),
        new("Custom OpenAI", "custom", "Custom", 4),
    ];

    public static IReadOnlyDictionary<string, ProviderGroupDefinition> ByAlias { get; } =
        All.ToDictionary(definition => definition.Alias, StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, ProviderGroupDefinition> ByBackend { get; } =
        All.ToDictionary(definition => definition.Backend, StringComparer.Ordinal);
}
