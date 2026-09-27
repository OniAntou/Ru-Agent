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
    string EmptyStateText,
    int TruncatedRowCount = 0,
    string TruncationNotice = "")
{
    public const string StaleAnnotation = "Stale data";
    public const string UnavailableAnnotation = "Unavailable";
    public const string NoUsableModelsText = "No available models";
    public const string TriggerPlaceholderText = "Model...";

    /// <summary>
    /// How many rows the view may realize. A models.dev-shaped catalog carries
    /// thousands of models, and the picker builds a Button per row with its own
    /// badge and pointer handlers, so an uncapped list costs tens of thousands of
    /// controls on the UI thread every time the search text or a poll changes.
    /// Bounded like every other listing; the search box is how a user reaches past
    /// it, and the selected model is never the row that gets dropped.
    /// </summary>
    public const int MaxRows = 200;

    public static string RowAutomationText(ProviderModelPickerRow row)
    {
        var state = !row.IsEnabled &&
                string.Equals(row.Annotation, UnavailableAnnotation, StringComparison.Ordinal)
            ? "unavailable"
            : row.IsStale ? "stale"
            : row.IsSelected ? "selected"
            : "available";
        return $"{row.ProviderName}, {row.ModelId}, {state}";
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

        return Bounded(visible, visible.Count == 0 ? NoUsableModelsText : string.Empty);
    }

    /// <summary>
    /// Trims the projection to <see cref="MaxRows"/> rows, keeping the group order and
    /// the selected row. One slot is reserved for the selection up front so it still
    /// fits when its provider sorts after everything the budget was spent on, and
    /// groups emptied by the trim are dropped so the view never renders a heading
    /// with nothing under it.
    /// </summary>
    private static ProviderModelPickerProjection Bounded(
        List<ProviderModelPickerGroup> groups,
        string emptyStateText)
    {
        int total = 0;
        foreach (var group in groups)
        {
            total += group.Rows.Count;
        }

        if (total <= MaxRows)
        {
            return new ProviderModelPickerProjection(groups, emptyStateText);
        }

        var selected = FindSelectedRow(groups);
        var selectedRows = selected?.Rows;
        var selectedRow = selected?.Row;
        int budget = MaxRows - (selectedRow is null ? 0 : 1);
        var kept = new List<ProviderModelPickerGroup>(groups.Count);

        foreach (var group in groups)
        {
            bool holdsSelection = ReferenceEquals(group.Rows, selectedRows);

            // Skip, never break: a zero budget must not stop the walk before the
            // group that carries the selection, which is exactly the case the
            // reserved slot was taken out for.
            if (budget <= 0 && !(holdsSelection && selectedRow is not null))
            {
                continue;
            }

            var candidates = holdsSelection
                ? group.Rows.Where(row => !row.IsSelected).ToArray()
                : group.Rows;
            var taken = candidates.Take(budget).ToList();

            if (holdsSelection && selectedRow is not null)
            {
                taken.Add(selectedRow);
                budget -= taken.Count - 1;
            }
            else
            {
                budget -= taken.Count;
            }

            kept.Add(group with { Rows = taken });
        }

        int keptRows = 0;
        foreach (var group in kept)
        {
            keptRows += group.Rows.Count;
        }

        int truncated = total - keptRows;
        return new ProviderModelPickerProjection(
            kept,
            emptyStateText,
            truncated,
            $"Showing {keptRows} of {total} \u2014 refine your search.");
    }

    private static (IReadOnlyList<ProviderModelPickerRow> Rows, ProviderModelPickerRow Row)? FindSelectedRow(
        List<ProviderModelPickerGroup> groups)
    {
        foreach (var group in groups)
        {
            foreach (var row in group.Rows)
            {
                if (row.IsSelected)
                {
                    return (group.Rows, row);
                }
            }
        }

        return null;
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
            TruncatedRowCount != other.TruncatedRowCount ||
            TruncationNotice != other.TruncationNotice ||
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
            string.Empty,
            string.Equals(definition.Alias, selectedBackend, StringComparison.Ordinal) &&
                string.Equals(model.Id, selectedModel, StringComparison.Ordinal),
            IsEnabled: true,
            isStale,
            isStale ? StaleAnnotation : string.Empty,
            ContextLength: model.ContextLength);
}
