namespace RuApp.Models;

public sealed record ProviderGroupDefinition(
    string Backend,
    string Alias,
    string DisplayName,
    int Order);

public sealed record ModelsDevEntry(string Id, string Name);

public static class ProviderGroupDefinitions
{
    /// <summary>
    /// The fixed groups the picker always shows. Only the custom endpoint is fixed:
    /// it is the escape hatch for a server Models.dev cannot list. Every other
    /// provider comes from the catalog, so OpenAI and Anthropic are no longer
    /// declared here -- they arrive as catalog entries like any other provider.
    /// </summary>
    public static IReadOnlyList<ProviderGroupDefinition> All { get; } =
    [
        new("Custom OpenAI", "custom", "Custom", 0),
    ];

    public static IReadOnlyDictionary<string, ProviderGroupDefinition> ByAlias { get; } =
        All.ToDictionary(definition => definition.Alias, StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, ProviderGroupDefinition> ByBackend { get; } =
        All.ToDictionary(definition => definition.Backend, StringComparer.Ordinal);

    /// <summary>
    /// The fixed group plus one group per Models.dev provider, in stable order.
    /// Unknown entries are skipped; the fixed set never moves. An entry whose id is
    /// already a <c>custom:&lt;id&gt;</c> backend is a named custom provider
    /// (ADR 0034) and keeps that backend as its alias.
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
            var name = string.IsNullOrWhiteSpace(entry.Name) ? id : entry.Name.Trim();
            if (Services.CustomProviderPolicy.IsCustomChoice(id))
            {
                if (seen.Add(id)) result.Add(new(id, id, name, result.Count));
                continue;
            }

            if (id.Length == 0 || id.Length > 64 || !seen.Add("models.dev:" + id))
            {
                continue;
            }

            result.Add(new("models.dev:" + id, "models.dev:" + id, name, result.Count));
        }

        return result;
    }
}
