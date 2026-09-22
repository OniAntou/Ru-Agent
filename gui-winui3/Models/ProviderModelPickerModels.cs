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
    public const string TriggerPlaceholderText = "Model...";

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

        return new ProviderModelPickerProjection(
            visible,
            visible.Count == 0 ? NoUsableModelsText : string.Empty);
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
