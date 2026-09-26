using RuApp.Models;
using Xunit;

namespace RuApp.Tests;

public sealed class ProviderModelPickerProjectionTests
{
    private const string ChatCompletions = "chat_completions";

    private static ProviderCatalogModel CatalogModel(
        string id,
        bool supported = true,
        string protocol = ChatCompletions) => new()
    {
        Id = id,
        Supported = supported,
        Protocol = protocol,
    };

    private static ProviderCatalogState State(
        string backend,
        string status = "ok",
        bool stale = false,
        string? errorCode = null,
        IReadOnlyList<ProviderCatalogModel>? catalog = null,
        IReadOnlyList<string>? models = null)
    {
        catalog ??= [];
        models ??= catalog.Select(model => model.Id).ToArray();
        return new ProviderCatalogState
        {
            Backend = backend,
            Status = status,
            Identity = "identity-" + backend,
            Models = models,
            Catalog = catalog,
            Stale = stale,
            ErrorCode = errorCode,
            LastSuccessUtc = status == "ok" ? DateTimeOffset.UtcNow : null,
        };
    }

    /// <summary>
    /// Definitions for the sample providers these tests project, in the order given.
    /// Only the custom endpoint is a fixed group now; every other provider reaches
    /// the picker as a catalog entry, so a test that groups by <c>openai</c> or
    /// <c>anthropic</c> has to declare it the same way the catalog does.
    /// </summary>
    private static IReadOnlyList<ProviderGroupDefinition> DefinitionsOf(
        params ProviderCatalogState[] states) =>
        states.Select((state, index) => ProviderGroupDefinitions.ByAlias.TryGetValue(
                state.Backend,
                out var definition)
            ? definition with { Order = index }
            : new ProviderGroupDefinition(
                state.Backend,
                state.Backend,
                DisplayNameOf(state.Backend),
                index))
            .ToArray();

    /// <summary>
    /// The name a catalog provider shows in the picker. Production takes this from the
    /// Models.dev entry; these fixtures use the bare alias, so title-case it the way a
    /// real catalog name reads.
    /// </summary>
    private static string DisplayNameOf(string alias) =>
        alias.Length == 0
            ? alias
            : char.ToUpperInvariant(alias[0]) + alias[1..];

    [Fact]
    public void Dynamic_models_dev_definitions_render_after_the_fixed_groups()
    {
        var generic = State("models.dev:openrouter", catalog: [CatalogModel("m1")]);
        var custom = State("custom", catalog: [CatalogModel("local-model")]);
        var definitions = ProviderGroupDefinitions.WithModelsDev([new ModelsDevEntry("openrouter", "OpenRouter")]);

        var projection = ProviderModelPickerProjection.Create(
            groups: [generic, custom],
            selectedBackend: "models.dev:openrouter",
            selectedModel: "m1",
            searchText: string.Empty,
            definitions: definitions);

        Assert.Collection(
            projection.Groups,
            group => Assert.Equal("custom", group.Provider.Alias),
            group => Assert.Equal("models.dev:openrouter", group.Provider.Alias));
    }

    [Fact]
    public void Groups_render_in_stable_definition_order_regardless_of_arrival_order()
    {
        var openai = State("openai", catalog: [CatalogModel("gpt-4o")]);
        var custom = State("custom", catalog: [CatalogModel("local-model")]);
        var anthropic = State("anthropic", catalog: [CatalogModel("claude-4-sonnet")]);

        var projection = ProviderModelPickerProjection.Create(
            groups: [anthropic, openai, custom],
            selectedBackend: "openai",
            selectedModel: "gpt-4o",
            searchText: string.Empty,
            definitions: DefinitionsOf(openai, anthropic, custom));

        Assert.Collection(
            projection.Groups,
            group => Assert.Equal("openai", group.Provider.Alias),
            group => Assert.Equal("anthropic", group.Provider.Alias),
            group => Assert.Equal("custom", group.Provider.Alias));
    }

    [Fact]
    public void Empty_failed_and_unconfigured_groups_without_data_are_hidden()
    {
        var custom = State("custom");
        var openai = State("openai", status: "error", errorCode: "catalog_unavailable");
        var emptyAnthropic = State("anthropic", status: "error", errorCode: "catalog_unauthorized");

        var projection = ProviderModelPickerProjection.Create(
            groups: [custom, openai, emptyAnthropic],
            selectedBackend: "openai",
            selectedModel: "gpt-4o",
            searchText: string.Empty,
            definitions: DefinitionsOf(custom, openai, emptyAnthropic));

        Assert.Empty(projection.Groups);
        Assert.Equal(ProviderModelPickerProjection.NoUsableModelsText, projection.EmptyStateText);
    }

    [Fact]
    public void Same_identity_stale_groups_with_usable_models_are_retained_and_marked_stale()
    {
        var kilo = State(
            "openai",
            status: "error",
            stale: true,
            errorCode: "catalog_unavailable",
            catalog: [CatalogModel("gpt-4o")]);

        var projection = ProviderModelPickerProjection.Create(
            groups: [kilo],
            selectedBackend: "openai",
            selectedModel: "gpt-4o",
            searchText: string.Empty,
            definitions: DefinitionsOf(kilo));

        var group = Assert.Single(projection.Groups);
        Assert.True(group.IsStale);
        Assert.All(group.Rows, row =>
        {
            Assert.True(row.IsEnabled);
            Assert.True(row.IsStale);
            Assert.Equal(ProviderModelPickerProjection.StaleAnnotation, row.Annotation);
        });
    }

    [Fact]
    public void Provider_name_search_retains_all_models_in_that_group()
    {
        var openai = State("openai", catalog: [CatalogModel("gpt-4o"), CatalogModel("gpt-4o-mini")]);
        var anthropic = State("anthropic", catalog: [CatalogModel("claude-4-sonnet"), CatalogModel("claude-4-opus")]);
        var emptyCustom = State("custom");

        var projection = ProviderModelPickerProjection.Create(
            groups: [openai, anthropic, emptyCustom],
            selectedBackend: "openai",
            selectedModel: "gpt-4o",
            searchText: "Anthropic",
            definitions: DefinitionsOf(openai, anthropic, emptyCustom));

        var group = Assert.Single(projection.Groups);
        Assert.Equal("Anthropic", group.Provider.DisplayName);
        Assert.Equal(anthropic.Models.Count, group.Rows.Count);
    }

    [Fact]
    public void Model_id_search_filters_rows_and_hides_empty_result_groups()
    {
        var kilo = State("openai", catalog: [CatalogModel("gpt-4o"), CatalogModel("gpt-4o-mini")]);
        var openCode = State("anthropic", catalog: [CatalogModel("claude-4-sonnet")]);

        var projection = ProviderModelPickerProjection.Create(
            groups: [openCode, kilo],
            selectedBackend: "openai",
            selectedModel: "gpt-4o-mini",
            searchText: "mini",
            definitions: DefinitionsOf(openCode, kilo));

        var group = Assert.Single(projection.Groups);
        Assert.Equal("openai", group.Provider.Alias);
        var row = Assert.Single(group.Rows);
        Assert.Equal("gpt-4o-mini", row.ModelId);
    }

    [Fact]
    public void Search_is_case_insensitive_and_does_not_mutate_source_state()
    {
        var kilo = State("openai", catalog: [CatalogModel("gpt-4o")]);
        var openCode = State("anthropic", catalog: [CatalogModel("claude-4-sonnet")]);

        var projection = ProviderModelPickerProjection.Create(
            groups: [kilo, openCode],
            selectedBackend: "openai",
            selectedModel: "gpt-4o",
            searchText: "ANTHROPIC",
            definitions: DefinitionsOf(kilo, openCode));

        var group = Assert.Single(projection.Groups);
        Assert.Equal("anthropic", group.Provider.Alias);
        Assert.Equal("gpt-4o", kilo.Models.Single());
        Assert.Equal(string.Empty, kilo.SearchText);
    }

    [Fact]
    public void Active_provider_model_row_is_selected()
    {
        var kilo = State("openai", catalog: [CatalogModel("gpt-4o"), CatalogModel("gpt-4o-mini")]);

        var projection = ProviderModelPickerProjection.Create(
            groups: [kilo],
            selectedBackend: "openai",
            selectedModel: "gpt-4o-mini",
            searchText: string.Empty,
            definitions: DefinitionsOf(kilo));

        var group = Assert.Single(projection.Groups);
        var selected = Assert.Single(group.Rows, row => row.IsSelected);
        Assert.Equal("gpt-4o-mini", selected.ModelId);
    }

    [Fact]
    public void Missing_selected_model_is_inserted_once_disabled_and_annotated_unavailable()
    {
        var kilo = State("openai", catalog: [CatalogModel("gpt-4o"), CatalogModel("gpt-4o-mini")]);

        var projection = ProviderModelPickerProjection.Create(
            groups: [kilo],
            selectedBackend: "openai",
            selectedModel: "retired-model",
            searchText: string.Empty,
            definitions: DefinitionsOf(kilo));

        var group = Assert.Single(projection.Groups);
        Assert.Equal(kilo.Models.Count + 1, group.Rows.Count);
        var unavailable = Assert.Single(group.Rows, row => !row.IsEnabled);
        Assert.True(unavailable.IsSelected);
        Assert.Equal("retired-model", unavailable.ModelId);
        Assert.Equal(ProviderModelPickerProjection.UnavailableAnnotation, unavailable.Annotation);
    }

    [Fact]
    public void Selected_model_is_not_fabricated_without_a_successful_non_empty_catalog()
    {
        var kilo = State(
            "openai",
            status: "error",
            stale: true,
            errorCode: "catalog_unavailable",
            catalog: [CatalogModel("gpt-4o")]);

        var projection = ProviderModelPickerProjection.Create(
            groups: [kilo],
            selectedBackend: "openai",
            selectedModel: "retired-model",
            searchText: string.Empty,
            definitions: DefinitionsOf(kilo));

        var group = Assert.Single(projection.Groups);
        Assert.DoesNotContain(group.Rows, row =>
            string.Equals(row.ModelId, "retired-model", StringComparison.Ordinal));
    }

    [Fact]
    public void Unsupported_rows_never_become_selectable()
    {
        var kilo = State("openai", catalog: [
            CatalogModel("gpt-4o"),
            CatalogModel("embedding-x", supported: false),
            CatalogModel("rerank-x", protocol: "rerank"),
        ]);

        var projection = ProviderModelPickerProjection.Create(
            groups: [kilo],
            selectedBackend: "openai",
            selectedModel: "gpt-4o",
            searchText: string.Empty,
            definitions: DefinitionsOf(kilo));

        var group = Assert.Single(projection.Groups);
        var row = Assert.Single(group.Rows);
        Assert.Equal("gpt-4o", row.ModelId);
        Assert.True(row.IsEnabled);
    }

    [Fact]
    public void Unsupported_only_group_is_hidden()
    {
        var kilo = State("openai", catalog: [CatalogModel("embedding-x", supported: false)]);

        var projection = ProviderModelPickerProjection.Create(
            groups: [kilo],
            selectedBackend: "openai",
            selectedModel: "gpt-4o",
            searchText: string.Empty,
            definitions: DefinitionsOf(kilo));

        Assert.Empty(projection.Groups);
        Assert.Equal(ProviderModelPickerProjection.NoUsableModelsText, projection.EmptyStateText);
    }

    [Fact]
    public void Search_that_matches_nothing_produces_the_empty_state_text()
    {
        var kilo = State("openai", catalog: [CatalogModel("gpt-4o")]);

        var projection = ProviderModelPickerProjection.Create(
            groups: [kilo],
            selectedBackend: "openai",
            selectedModel: "gpt-4o",
            searchText: "zzz",
            definitions: DefinitionsOf(kilo));

        Assert.Empty(projection.Groups);
        Assert.Equal(ProviderModelPickerProjection.NoUsableModelsText, projection.EmptyStateText);
    }

    [Fact]
    public void Row_automation_text_reflects_selected_unavailable_and_stale_states()
    {
        var selected = new ProviderModelPickerRow(
            "openai", "OpenAI", "gpt-4o", string.Empty, true, true, false, string.Empty);
        var unavailable = new ProviderModelPickerRow(
            "openai", "OpenAI", "retired-model", string.Empty, true, false, false,
            ProviderModelPickerProjection.UnavailableAnnotation);
        var stale = new ProviderModelPickerRow(
            "openai", "OpenAI", "gpt-4o-mini", string.Empty, false, true, true,
            ProviderModelPickerProjection.StaleAnnotation);

        Assert.Equal(
            "OpenAI, gpt-4o, selected",
            ProviderModelPickerProjection.RowAutomationText(selected));
        Assert.Equal(
            "OpenAI, retired-model, unavailable",
            ProviderModelPickerProjection.RowAutomationText(unavailable));
        Assert.Equal(
            "OpenAI, gpt-4o-mini, stale",
            ProviderModelPickerProjection.RowAutomationText(stale));
    }

    [Fact]
    public void Trigger_display_shows_model_or_placeholder()
    {
        Assert.Equal(
            "gpt-4o",
            ProviderModelPickerProjection.TriggerDisplay("openai", "gpt-4o"));
        Assert.Equal(
            "claude-4-sonnet",
            ProviderModelPickerProjection.TriggerDisplay("anthropic", "claude-4-sonnet"));
        Assert.Equal("Model...", ProviderModelPickerProjection.TriggerDisplay("openai", string.Empty));
        Assert.Equal("Model...", ProviderModelPickerProjection.TriggerDisplay(string.Empty, string.Empty));
        Assert.Equal("Model...", ProviderModelPickerProjection.TriggerDisplay("openai", "   "));
    }

    [Fact]
    public void Provider_group_definitions_are_presentation_only()
    {
        Assert.Single(ProviderGroupDefinitions.All);
        Assert.Equal(
            new[] { 0 },
            ProviderGroupDefinitions.All.Select(definition => definition.Order));
        Assert.Equal("custom", ProviderGroupDefinitions.ByAlias["custom"].Alias);
        Assert.Equal("Custom OpenAI", ProviderGroupDefinitions.ByBackend["Custom OpenAI"].Backend);
        Assert.All(ProviderGroupDefinitions.All, definition =>
        {
            Assert.DoesNotContain("://", definition.Backend, StringComparison.Ordinal);
            Assert.DoesNotContain("://", definition.Alias, StringComparison.Ordinal);
        });
    }
}
