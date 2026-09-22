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

    [Fact]
    public void Dynamic_models_dev_definitions_render_after_the_fixed_groups()
    {
        var generic = State("models.dev:openrouter", catalog: [CatalogModel("m1")]);
        var openCode = State("opencode", catalog: [CatalogModel("opencode/free")]);
        var definitions = ProviderGroupDefinitions.WithModelsDev([new ModelsDevEntry("openrouter", "OpenRouter")]);

        var projection = ProviderModelPickerProjection.Create(
            groups: [generic, openCode],
            selectedBackend: "models.dev:openrouter",
            selectedModel: "m1",
            searchText: string.Empty,
            definitions: definitions);

        Assert.Collection(
            projection.Groups,
            group => Assert.Equal("opencode", group.Provider.Alias),
            group => Assert.Equal("models.dev:openrouter", group.Provider.Alias));
    }

    [Fact]
    public void Groups_render_in_stable_definition_order_regardless_of_arrival_order()
    {
        var kilo = State("kilo", catalog: [CatalogModel("kilo-auto/frontier")]);
        var openCode = State("opencode", catalog: [CatalogModel("opencode/free")]);
        var anthropic = State("anthropic", catalog: [CatalogModel("claude-4-sonnet")]);

        var projection = ProviderModelPickerProjection.Create(
            groups: [anthropic, kilo, openCode],
            selectedBackend: "kilo",
            selectedModel: "kilo-auto/frontier",
            searchText: string.Empty);

        Assert.Collection(
            projection.Groups,
            group => Assert.Equal("opencode", group.Provider.Alias),
            group => Assert.Equal("kilo", group.Provider.Alias),
            group => Assert.Equal("anthropic", group.Provider.Alias));
    }

    [Fact]
    public void Empty_failed_and_unconfigured_groups_without_data_are_hidden()
    {
        var openCode = State("opencode");
        var kilo = State("kilo", status: "error", errorCode: "catalog_unavailable");
        var emptyOpenAi = State("openai", status: "error", errorCode: "catalog_unauthorized");

        var projection = ProviderModelPickerProjection.Create(
            groups: [openCode, kilo, emptyOpenAi],
            selectedBackend: "kilo",
            selectedModel: "kilo-auto/frontier",
            searchText: string.Empty);

        Assert.Empty(projection.Groups);
        Assert.Equal(ProviderModelPickerProjection.NoUsableModelsText, projection.EmptyStateText);
    }

    [Fact]
    public void Same_identity_stale_groups_with_usable_models_are_retained_and_marked_stale()
    {
        var kilo = State(
            "kilo",
            status: "error",
            stale: true,
            errorCode: "catalog_unavailable",
            catalog: [CatalogModel("kilo-auto/frontier")]);

        var projection = ProviderModelPickerProjection.Create(
            groups: [kilo],
            selectedBackend: "kilo",
            selectedModel: "kilo-auto/frontier",
            searchText: string.Empty);

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
        var kilo = State("kilo", catalog: [CatalogModel("kilo-auto/frontier"), CatalogModel("kilo-mini")]);
        var openCode = State("opencode", catalog: [CatalogModel("opencode/free"), CatalogModel("opencode/pro")]);
        var emptyOpenAi = State("openai");

        var projection = ProviderModelPickerProjection.Create(
            groups: [kilo, openCode, emptyOpenAi],
            selectedBackend: "kilo",
            selectedModel: "kilo-auto/frontier",
            searchText: "OpenCode");

        var group = Assert.Single(projection.Groups);
        Assert.Equal("OpenCode", group.Provider.DisplayName);
        Assert.Equal(openCode.Models.Count, group.Rows.Count);
    }

    [Fact]
    public void Model_id_search_filters_rows_and_hides_empty_result_groups()
    {
        var kilo = State("kilo", catalog: [CatalogModel("kilo-auto/frontier"), CatalogModel("kilo-mini")]);
        var openCode = State("opencode", catalog: [CatalogModel("opencode/free")]);

        var projection = ProviderModelPickerProjection.Create(
            groups: [openCode, kilo],
            selectedBackend: "kilo",
            selectedModel: "kilo-auto/frontier",
            searchText: "frontier");

        var group = Assert.Single(projection.Groups);
        Assert.Equal("kilo", group.Provider.Alias);
        var row = Assert.Single(group.Rows);
        Assert.Equal("kilo-auto/frontier", row.ModelId);
    }

    [Fact]
    public void Search_is_case_insensitive_and_does_not_mutate_source_state()
    {
        var kilo = State("kilo", catalog: [CatalogModel("kilo-auto/frontier")]);
        var openCode = State("opencode", catalog: [CatalogModel("opencode/free")]);

        var projection = ProviderModelPickerProjection.Create(
            groups: [kilo, openCode],
            selectedBackend: "kilo",
            selectedModel: "kilo-auto/frontier",
            searchText: "OPENCODE");

        var group = Assert.Single(projection.Groups);
        Assert.Equal("opencode", group.Provider.Alias);
        Assert.Equal("kilo-auto/frontier", kilo.Models.Single());
        Assert.Equal(string.Empty, kilo.SearchText);
    }

    [Fact]
    public void Active_provider_model_row_is_selected()
    {
        var kilo = State("kilo", catalog: [CatalogModel("kilo-auto/frontier"), CatalogModel("kilo-mini")]);

        var projection = ProviderModelPickerProjection.Create(
            groups: [kilo],
            selectedBackend: "kilo",
            selectedModel: "kilo-mini",
            searchText: string.Empty);

        var group = Assert.Single(projection.Groups);
        var selected = Assert.Single(group.Rows, row => row.IsSelected);
        Assert.Equal("kilo-mini", selected.ModelId);
    }

    [Fact]
    public void Missing_selected_model_is_inserted_once_disabled_and_annotated_unavailable()
    {
        var kilo = State("kilo", catalog: [CatalogModel("kilo-auto/frontier"), CatalogModel("kilo-mini")]);

        var projection = ProviderModelPickerProjection.Create(
            groups: [kilo],
            selectedBackend: "kilo",
            selectedModel: "retired-model",
            searchText: string.Empty);

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
            "kilo",
            status: "error",
            stale: true,
            errorCode: "catalog_unavailable",
            catalog: [CatalogModel("kilo-auto/frontier")]);

        var projection = ProviderModelPickerProjection.Create(
            groups: [kilo],
            selectedBackend: "kilo",
            selectedModel: "retired-model",
            searchText: string.Empty);

        var group = Assert.Single(projection.Groups);
        Assert.DoesNotContain(group.Rows, row =>
            string.Equals(row.ModelId, "retired-model", StringComparison.Ordinal));
    }

    [Fact]
    public void Unsupported_rows_never_become_selectable()
    {
        var kilo = State("kilo", catalog: [
            CatalogModel("kilo-auto/frontier"),
            CatalogModel("embedding-x", supported: false),
            CatalogModel("rerank-x", protocol: "rerank"),
        ]);

        var projection = ProviderModelPickerProjection.Create(
            groups: [kilo],
            selectedBackend: "kilo",
            selectedModel: "kilo-auto/frontier",
            searchText: string.Empty);

        var group = Assert.Single(projection.Groups);
        var row = Assert.Single(group.Rows);
        Assert.Equal("kilo-auto/frontier", row.ModelId);
        Assert.True(row.IsEnabled);
    }

    [Fact]
    public void Unsupported_only_group_is_hidden()
    {
        var kilo = State("kilo", catalog: [CatalogModel("embedding-x", supported: false)]);

        var projection = ProviderModelPickerProjection.Create(
            groups: [kilo],
            selectedBackend: "kilo",
            selectedModel: "kilo-auto/frontier",
            searchText: string.Empty);

        Assert.Empty(projection.Groups);
        Assert.Equal(ProviderModelPickerProjection.NoUsableModelsText, projection.EmptyStateText);
    }

    [Fact]
    public void Search_that_matches_nothing_produces_the_empty_state_text()
    {
        var kilo = State("kilo", catalog: [CatalogModel("kilo-auto/frontier")]);

        var projection = ProviderModelPickerProjection.Create(
            groups: [kilo],
            selectedBackend: "kilo",
            selectedModel: "kilo-auto/frontier",
            searchText: "zzz");

        Assert.Empty(projection.Groups);
        Assert.Equal(ProviderModelPickerProjection.NoUsableModelsText, projection.EmptyStateText);
    }

    [Fact]
    public void Row_automation_text_reflects_selected_unavailable_and_stale_states()
    {
        var selected = new ProviderModelPickerRow(
            "kilo", "Kilo", "kilo-auto/frontier", string.Empty, true, true, false, string.Empty);
        var unavailable = new ProviderModelPickerRow(
            "kilo", "Kilo", "retired-model", string.Empty, true, false, false,
            ProviderModelPickerProjection.UnavailableAnnotation);
        var stale = new ProviderModelPickerRow(
            "kilo", "Kilo", "kilo-mini", string.Empty, false, true, true,
            ProviderModelPickerProjection.StaleAnnotation);

        Assert.Equal(
            "Kilo, kilo-auto/frontier, selected",
            ProviderModelPickerProjection.RowAutomationText(selected));
        Assert.Equal(
            "Kilo, retired-model, unavailable",
            ProviderModelPickerProjection.RowAutomationText(unavailable));
        Assert.Equal(
            "Kilo, kilo-mini, stale",
            ProviderModelPickerProjection.RowAutomationText(stale));
    }

    [Fact]
    public void Trigger_display_shows_model_or_placeholder()
    {
        Assert.Equal(
            "kilo-auto/frontier",
            ProviderModelPickerProjection.TriggerDisplay("kilo", "kilo-auto/frontier"));
        Assert.Equal(
            "opencode/free",
            ProviderModelPickerProjection.TriggerDisplay("opencode", "opencode/free"));
        Assert.Equal("Model...", ProviderModelPickerProjection.TriggerDisplay("kilo", string.Empty));
        Assert.Equal("Model...", ProviderModelPickerProjection.TriggerDisplay(string.Empty, string.Empty));
        Assert.Equal("Model...", ProviderModelPickerProjection.TriggerDisplay("kilo", "   "));
    }

    [Fact]
    public void Provider_group_definitions_are_presentation_only()
    {
        Assert.Equal(5, ProviderGroupDefinitions.All.Count);
        Assert.Equal(
            new[] { 0, 1, 2, 3, 4 },
            ProviderGroupDefinitions.All.Select(definition => definition.Order));
        Assert.Equal("kilo", ProviderGroupDefinitions.ByAlias["kilo"].Alias);
        Assert.Equal("Kilo Gateway", ProviderGroupDefinitions.ByBackend["Kilo Gateway"].Backend);
        Assert.All(ProviderGroupDefinitions.All, definition =>
        {
            Assert.DoesNotContain("://", definition.Backend, StringComparison.Ordinal);
            Assert.DoesNotContain("://", definition.Alias, StringComparison.Ordinal);
        });
    }
}
