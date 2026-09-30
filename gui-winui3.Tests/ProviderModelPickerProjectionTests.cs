using RuApp.Models;
using Xunit;

namespace RuApp.Tests;

public sealed class ProviderModelPickerProjectionTests
{
    private const string ChatCompletions = "chat_completions";

    /// <summary>
    /// A Models.dev provider keeps its model in the backend's generic <c>model</c> field.
    /// </summary>
    /// <remarks>
    /// The selector had no arm for those providers, so it answered empty and the picker
    /// trigger fell back to its placeholder while a model was configured and the catalog
    /// listed it. The response's <c>model</c> was also missing from the settings DTO, so
    /// the value never even arrived.
    /// </remarks>
    [Theory]
    [InlineData("models.dev:openrouter", "cohere/north-mini-code:free")]
    [InlineData("models.dev:groq", "llama-3.3-70b")]
    public void A_models_dev_backend_reports_the_generic_model_field(string backend, string model)
    {
        var settings = new AppSettingsDto { BackendChoice = backend, Model = model };

        Assert.Equal(model, ProviderModelSelection.GetSelectedModel(settings, backend));
    }

    [Fact]
    public void The_generic_model_field_deserializes_from_the_settings_response()
    {
        // The backend's own JSON shape, as /settings returns it.
        const string json = """
            {"backend":"models.dev:openrouter","backend_choice":"models.dev:openrouter","model":"cohere/north-mini-code:free","custom_model":"default","openai_model":"","anthropic_model":""}
            """;

        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettingsDto>(json);

        Assert.NotNull(settings);
        Assert.Equal("cohere/north-mini-code:free", settings!.Model);
        Assert.Equal(
            "cohere/north-mini-code:free",
            ProviderModelSelection.GetSelectedModel(settings, settings.BackendChoice));
    }

    [Theory]
    [InlineData("openai", "gpt-4o", "gpt-4o")]
    [InlineData("custom", "local", "local")]
    public void A_fixed_backend_still_reports_its_own_field(string backend, string value, string expected)
    {
        var settings = new AppSettingsDto
        {
            BackendChoice = backend,
            OpenAiModel = value,
            CustomModel = value,
            Model = "should-not-be-read",
        };

        Assert.Equal(expected, ProviderModelSelection.GetSelectedModel(settings, backend));
    }

    private static ProviderCatalogModel CatalogModel(
        string id,
        bool supported = true,
        string protocol = ChatCompletions,
        int? contextLength = null) => new()
    {
        Id = id,
        Supported = supported,
        Protocol = protocol,
        ContextLength = contextLength,
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
            // Staleness is a fact about the provider's catalog, so it is carried by the
            // group and the view prints it on the group's heading. The rows used to
            // repeat it, which put the same sentence on every line and, on a long model
            // id, pushed it past the ellipsis where nothing read it.
            Assert.Equal(string.Empty, row.Annotation);
        });
    }

    [Fact]
    public void A_typed_query_that_matches_nothing_says_the_query_missed()
    {
        // Both cases used to read "No available models", so a typo reported the
        // provider's catalog as empty. Only one of them is the user's to fix.
        var openai = State("openai", catalog: [CatalogModel("gpt-4o")]);

        var projection = ProviderModelPickerProjection.Create(
            groups: [openai],
            selectedBackend: "openai",
            selectedModel: "gpt-4o",
            searchText: "qzx-nonexistent",
            definitions: DefinitionsOf(openai));

        Assert.Empty(projection.Groups);
        Assert.Equal(ProviderModelPickerProjection.NoMatchText, projection.EmptyStateText);
        Assert.NotEqual(ProviderModelPickerProjection.NoUsableModelsText, projection.EmptyStateText);
    }

    [Fact]
    public void The_context_column_shows_the_figure_the_catalog_published()
    {
        var openai = State("openai", catalog: [CatalogModel("gpt-4o", contextLength: 128_000)]);

        var projection = ProviderModelPickerProjection.Create(
            groups: [openai],
            selectedBackend: "openai",
            selectedModel: "gpt-4o",
            searchText: string.Empty,
            definitions: DefinitionsOf(openai));

        Assert.Equal("128K", Assert.Single(Assert.Single(projection.Groups).Rows).ContextLabel);
    }

    [Fact]
    public void The_context_column_is_empty_when_the_catalog_published_no_figure()
    {
        // ModelContextSpecs answers "128K" for a model it has never heard of, by
        // substring-matching the vendor's name and falling through to a default. This
        // column is what someone picks a model by, so a guess in it is worse than a
        // blank: models.dev publishes no context for most models, and a filled column
        // would have claimed a figure for every one of them.
        var openai = State("openai", catalog: [CatalogModel("some-unheard-of-model")]);

        var projection = ProviderModelPickerProjection.Create(
            groups: [openai],
            selectedBackend: "openai",
            selectedModel: "some-unheard-of-model",
            searchText: string.Empty,
            definitions: DefinitionsOf(openai));

        var row = Assert.Single(Assert.Single(projection.Groups).Rows);
        Assert.Equal(string.Empty, row.ContextLabel);
        Assert.Equal("128K", ModelContextSpecs.GetContextSpec(row.ModelId, null).DisplayText);
    }

    /// <summary>
    /// Free-tier models carry the same tag the TUI gives them: OpenRouter's
    /// <c>:free</c> suffix is the provider's own marker, and the picker is where a
    /// user sees it before ever reaching the price.
    /// </summary>
    [Fact]
    public void A_free_model_carries_the_free_tag_and_a_paid_one_does_not()
    {
        var openrouter = State(
            "models.dev:openrouter",
            catalog:
            [
                CatalogModel("cohere/north-mini-code:free"),
                CatalogModel("openai/gpt-4o"),
            ]);
        var definitions = ProviderGroupDefinitions.WithModelsDev(
            [new ModelsDevEntry("openrouter", "OpenRouter")]);

        var projection = ProviderModelPickerProjection.Create(
            groups: [openrouter],
            selectedBackend: "models.dev:openrouter",
            selectedModel: "cohere/north-mini-code:free",
            searchText: string.Empty,
            definitions: definitions);

        var rows = Assert.Single(projection.Groups).Rows;
        var free = Assert.Single(rows, row => row.ModelId == "cohere/north-mini-code:free");
        var paid = Assert.Single(rows, row => row.ModelId == "openai/gpt-4o");
        Assert.True(free.IsFree);
        Assert.False(paid.IsFree);

        // The tag is spoken, not just drawn: the automation name is the whole row a
        // screen reader gets.
        Assert.Contains(", free", ProviderModelPickerProjection.RowAutomationText(free), StringComparison.Ordinal);
        Assert.DoesNotContain(", free", ProviderModelPickerProjection.RowAutomationText(paid), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("x:free", true)]
    [InlineData("x:FREE", true)]
    [InlineData("  x:free  ", true)]
    [InlineData("x-model", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void The_free_rule_matches_the_tui_suffix_rule(string? id, bool expected)
    {
        Assert.Equal(expected, ProviderModelPickerProjection.IsFreeModel(id));
    }

    [Fact]
    public void A_row_announces_its_context_to_a_screen_reader()
    {
        var row = new ProviderModelPickerRow(
            "openai", "OpenAI", "gpt-4o", "128K", IsSelected: true, IsEnabled: true, false, string.Empty);

        Assert.Equal("OpenAI, gpt-4o, 128K context, selected", ProviderModelPickerProjection.RowAutomationText(row));
    }

    [Fact]
    public void A_row_with_no_published_context_announces_no_number()
    {
        var row = new ProviderModelPickerRow(
            "openai", "OpenAI", "gpt-4o", string.Empty, IsSelected: false, IsEnabled: true, false, string.Empty);

        Assert.Equal("OpenAI, gpt-4o, available", ProviderModelPickerProjection.RowAutomationText(row));
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
    public void Search_that_matches_nothing_blames_the_query_not_the_catalog()
    {
        var kilo = State("openai", catalog: [CatalogModel("gpt-4o")]);

        var projection = ProviderModelPickerProjection.Create(
            groups: [kilo],
            selectedBackend: "openai",
            selectedModel: "gpt-4o",
            searchText: "zzz",
            definitions: DefinitionsOf(kilo));

        Assert.Empty(projection.Groups);
        Assert.Equal(ProviderModelPickerProjection.NoMatchText, projection.EmptyStateText);
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

    [Fact]
    public void Two_projections_over_the_same_catalog_render_the_same_tree()
    {
        var first = State("custom", catalog: [CatalogModel("a"), CatalogModel("b")]);
        var second = State("models.dev:openrouter", catalog: [CatalogModel("c")]);
        var definitions = ProviderGroupDefinitions.WithModelsDev([new ModelsDevEntry("openrouter", "OpenRouter")]);

        var one = ProviderModelPickerProjection.Create(
            [first, second], "custom", "a", string.Empty, definitions);
        var same = ProviderModelPickerProjection.Create(
            [first, second], "custom", "a", string.Empty, definitions);

        Assert.NotSame(one, same);
        Assert.False(one == same);
        Assert.True(one.HasSameContentAs(same));
    }

    [Fact]
    public void Content_comparison_notices_everything_the_view_renders()
    {
        var state = State("custom", catalog: [CatalogModel("a"), CatalogModel("b")]);
        var definitions = DefinitionsOf(state);

        ProviderModelPickerProjection Build(string search, string selected) =>
            ProviderModelPickerProjection.Create([state], "custom", selected, search, definitions);

        var baseline = Build(string.Empty, "a");
        Assert.False(baseline.HasSameContentAs(Build("a", "a")));
        Assert.False(baseline.HasSameContentAs(Build(string.Empty, "b")));
        Assert.False(baseline.HasSameContentAs(null));
        Assert.True(baseline.HasSameContentAs(Build(string.Empty, "a")));

        var stale = new ProviderCatalogState
        {
            Backend = "custom",
            Status = "ok",
            Identity = "identity-custom",
            Models = ["a", "b"],
            Catalog = [CatalogModel("a"), CatalogModel("b")],
            Stale = true,
        };
        var staleProjection = ProviderModelPickerProjection.Create(
            [stale], "custom", "a", string.Empty, DefinitionsOf(stale));
        Assert.False(baseline.HasSameContentAs(staleProjection));
    }
}
