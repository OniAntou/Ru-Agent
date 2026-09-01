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
    string Annotation);

public sealed record ProviderModelPickerGroup(
    ProviderGroupDefinition Provider,
    IReadOnlyList<ProviderModelPickerRow> Rows,
    bool IsStale);

public sealed record ProviderModelPickerProjection(
    IReadOnlyList<ProviderModelPickerGroup> Groups,
    string EmptyStateText)
{
    public const string StaleAnnotation = "Dữ liệu cũ";
    public const string UnavailableAnnotation = "Không còn khả dụng";
    public const string NoUsableModelsText = "Không có model khả dụng";

    public static ProviderModelPickerProjection Create(
        IEnumerable<ProviderCatalogState>? groups,
        string selectedBackend,
        string selectedModel,
        string searchText)
    {
        var states = new Dictionary<string, ProviderCatalogState>(StringComparer.Ordinal);
        if (groups is not null)
        {
            foreach (var state in groups)
            {
                if (state is not null && ProviderGroupDefinitions.ByAlias.ContainsKey(state.Backend))
                {
                    states[state.Backend] = state;
                }
            }
        }

        var visible = new List<ProviderModelPickerGroup>();
        foreach (var definition in ProviderGroupDefinitions.All)
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
            isStale ? StaleAnnotation : string.Empty);
}
