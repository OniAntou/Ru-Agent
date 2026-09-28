namespace RuApp.Models;

public static class ProviderModelProtocols
{
    public const string ChatCompletions = "chat_completions";
    public const string OpenAiResponses = "openai_responses";
    public const string AnthropicMessages = "anthropic_messages";

    public static bool IsAllowed(string? protocol) => protocol is
        ChatCompletions or
        OpenAiResponses or
        AnthropicMessages;
}

public sealed record ProviderModelPickerRow(
    string Backend,
    string ProviderName,
    string ModelId,
    string ContextLabel,
    bool IsSelected,
    bool IsEnabled,
    bool IsStale,
    string Annotation,
    int? ContextLength = null);

public sealed record ProviderModelPickerGroup(
    ProviderGroupDefinition Provider,
    IReadOnlyList<ProviderModelPickerRow> Rows,
    bool IsStale);

public sealed record ProviderModelPickerProjection(
    IReadOnlyList<ProviderModelPickerGroup> Groups,
    string EmptyStateText)
{
    public const string StaleAnnotation = "Stale data";
    public const string UnavailableAnnotation = "Unavailable";
    public const string NoUsableModelsText = "No available models";
    public const string NoMatchText = "No model matches";
    public const string TriggerPlaceholderText = "Model...";

    public static string RowAutomationText(ProviderModelPickerRow row)
    {
        var state = !row.IsEnabled &&
                string.Equals(row.Annotation, UnavailableAnnotation, StringComparison.Ordinal)
            ? "unavailable"
            : row.IsStale ? "stale"
            : row.IsSelected ? "selected"
            : "available";

        // The context figure is spoken, not just drawn. It is the number a user picks a
        // model by, and it is the one part of the row that sighted and screen-reader
        // users would otherwise reach different conclusions about.
        var context = string.IsNullOrWhiteSpace(row.ContextLabel)
            ? string.Empty
            : $", {row.ContextLabel} context";
        return $"{row.ProviderName}, {row.ModelId}{context}, {state}";
    }

    public static string TriggerDisplay(string backend, string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return TriggerPlaceholderText;
        }

        return model;
    }

    public static ProviderModelPickerProjection Create(
        IEnumerable<ProviderCatalogState>? groups,
        string selectedBackend,
        string selectedModel,
        string searchText,
        IReadOnlyList<ProviderGroupDefinition>? definitions = null)
    {
        var list = definitions ?? ProviderGroupDefinitions.All;
        var known = list.Select(definition => definition.Alias).ToHashSet(StringComparer.Ordinal);
        var states = new Dictionary<string, ProviderCatalogState>(StringComparer.Ordinal);
        if (groups is not null)
        {
            foreach (var state in groups)
            {
                if (state is not null && known.Contains(state.Backend))
                {
                    states[state.Backend] = state;
                }
            }
        }

        var visible = new List<ProviderModelPickerGroup>();
        foreach (var definition in list)
        {
            if (states.TryGetValue(definition.Alias, out var state) &&
                BuildGroup(definition, state, selectedBackend, selectedModel, searchText) is { } group)
            {
                visible.Add(group);
            }
        }

        return new ProviderModelPickerProjection(
            visible,
            ResolveEmptyStateText(normalizedSearch: searchText, matchedAnything: visible.Count > 0));
    }

    /// <summary>
    /// Whether two projections would render the same tree.
    /// </summary>
    /// <remarks>
    /// A catalog poll can return exactly what the last one returned, and the picker
    /// is rebuilt whenever grouped state is published. Record equality does not help
    /// here: <see cref="IReadOnlyList{T}"/> compares by reference, so two projections
    /// built from identical catalogs are never <c>==</c>. Comparing the fields the
    /// view actually reads is what lets the view skip a redundant rebuild.
    /// </remarks>
    public bool HasSameContentAs(ProviderModelPickerProjection? other)
    {
        if (other is null ||
            EmptyStateText != other.EmptyStateText ||
            Groups.Count != other.Groups.Count)
        {
            return false;
        }

        for (int g = 0; g < Groups.Count; g++)
        {
            var mine = Groups[g];
            var theirs = other.Groups[g];
            if (mine.Provider.Alias != theirs.Provider.Alias ||
                mine.IsStale != theirs.IsStale ||
                mine.Rows.Count != theirs.Rows.Count)
            {
                return false;
            }

            for (int r = 0; r < mine.Rows.Count; r++)
            {
                if (!mine.Rows[r].Equals(theirs.Rows[r]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Why the list is empty, said in the terms of what the user actually did.
    /// </summary>
    /// <remarks>
    /// One sentence used to cover both cases: a typo in the search box and a provider
    /// with nothing to offer both read "No available models", so a mistyped query
    /// reported the catalog as empty. They are different facts about different actors,
    /// and the user can only fix one of them.
    /// </remarks>
    private static string ResolveEmptyStateText(string? normalizedSearch, bool matchedAnything)
    {
        if (matchedAnything)
        {
            return string.Empty;
        }

        return string.IsNullOrWhiteSpace(normalizedSearch)
            ? NoUsableModelsText
            : NoMatchText;
    }

    private static ProviderModelPickerGroup? BuildGroup(
        ProviderGroupDefinition definition,
        ProviderCatalogState state,
        string selectedBackend,
        string selectedModel,
        string searchText)
    {
        if (state.Catalog.Count == 0)
        {
            return null;
        }

        var usable = state.Catalog
            .Where(model => model.Supported && ProviderModelProtocols.IsAllowed(model.Protocol))
            .ToArray();
        if (usable.Length == 0)
        {
            return null;
        }

        var normalizedSearch = (searchText ?? string.Empty).Trim();
        var matchesProvider = normalizedSearch.Length > 0 &&
            definition.DisplayName.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase);
        var filtered = normalizedSearch.Length == 0 || matchesProvider
            ? usable
            : usable.Where(model => model.Id.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        if (filtered.Length == 0)
        {
            return null;
        }

        var rows = filtered
            .Select(model => BuildRow(definition, model, selectedBackend, selectedModel, state.Stale))
            .ToList();

        if (string.Equals(definition.Alias, selectedBackend, StringComparison.Ordinal) &&
            state.IsSuccess &&
            state.Models.Count > 0 &&
            !string.IsNullOrWhiteSpace(selectedModel) &&
            !state.Models.Contains(selectedModel))
        {
            rows.Add(new ProviderModelPickerRow(
                definition.Alias,
                definition.DisplayName,
                selectedModel,
                string.Empty,
                IsSelected: true,
                IsEnabled: false,
                state.Stale,
                UnavailableAnnotation));
        }

        return new ProviderModelPickerGroup(definition, rows, state.Stale);
    }

    private static ProviderModelPickerRow BuildRow(
        ProviderGroupDefinition definition,
        ProviderCatalogModel model,
        string selectedBackend,
        string selectedModel,
        bool isStale) =>
        new(
            definition.Alias,
            definition.DisplayName,
            model.Id,
            // Only what the catalog published. ModelContextSpecs would have guessed a
            // figure from the model's name, and this column is what a user reads to
            // choose between two models — a guess there picks the wrong one.
            ModelContextSpecs.FormatPublishedContext(model.ContextLength) ?? string.Empty,
            string.Equals(definition.Alias, selectedBackend, StringComparison.Ordinal) &&
                string.Equals(model.Id, selectedModel, StringComparison.Ordinal),
            IsEnabled: true,
            isStale,
            // Staleness is a fact about the provider's catalog, not about one model, and
            // the group heading already carries it. Repeating it on every row printed
            // the same sentence N times and, on a long model id, put it behind the
            // ellipsis where it was never read.
            string.Empty,
            ContextLength: model.ContextLength);
}
