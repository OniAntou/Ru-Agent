namespace RuApp.Models;

public sealed record ProviderGroupDefinition(
    string Backend,
    string Alias,
    string DisplayName,
    int Order);

public sealed record ModelsDevEntry(string Id, string Name);

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

    /// <summary>
    /// The fixed five plus one group per Models.dev provider, in stable order.
    /// Unknown entries are skipped; the fixed set never moves.
    /// </summary>
    public static IReadOnlyList<ProviderGroupDefinition> WithModelsDev(IEnumerable<ModelsDevEntry>? entries)
    {
        var result = new List<ProviderGroupDefinition>(All);
        if (entries is null)
        {
            return result;
        }

        var seen = new HashSet<string>(All.Select(definition => definition.Alias), StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry is null)
            {
                continue;
            }

            var id = (entry.Id ?? string.Empty).Trim().ToLowerInvariant();
            if (id.Length == 0 || id.Length > 64 || !seen.Add("models.dev:" + id))
            {
                continue;
            }

            var name = string.IsNullOrWhiteSpace(entry.Name) ? id : entry.Name.Trim();
            result.Add(new("models.dev:" + id, "models.dev:" + id, name, result.Count));
        }

        return result;
    }
}
