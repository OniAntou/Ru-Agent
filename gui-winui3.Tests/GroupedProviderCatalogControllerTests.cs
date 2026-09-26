using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class GroupedProviderCatalogControllerTests
{
    /// <summary>
    /// The two sample providers these tests group by. They are catalog aliases, not
    /// fixed rows: only the custom endpoint is fixed now, so a test that expects a
    /// group for one of them has to register it the way the catalog does.
    /// </summary>
    private const string OpenAiAlias = "models.dev:openai";

    private const string AnthropicAlias = "models.dev:anthropic";

    private static ModelsDevEntry[] ProviderEntries() =>
    [
        new("openai", "OpenAI"),
        new("anthropic", "Anthropic"),
    ];

    /// <summary>
    /// Registers the two sample providers as dynamic children and gives the fake
    /// service a source for each, which is what the controller will poll.
    /// </summary>
    private static async Task<GroupedProviderCatalogController> CreateControllerAsync(
        ControlledCatalogs controlled,
        IProviderCatalogClock? clock = null)
    {
        controlled.EnsureSource(OpenAiAlias);
        controlled.EnsureSource(AnthropicAlias);
        var controller = new GroupedProviderCatalogController(
            controlled.CreateService(),
            clock);
        await controller.SetModelsDevProvidersAsync(ProviderEntries());
        return controller;
    }

    private static ProviderCatalogSnapshot Snapshot(string alias, params string[] models) => new()
    {
        Status = "ok",
        Backend = alias,
        Identity = "identity-" + alias,
        Models = models,
        LastSuccessUtc = DateTimeOffset.UtcNow,
    };

    private static ProviderCatalogSnapshot SameIdentityFailure(string alias) => new()
    {
        Status = "error",
        Backend = alias,
        Identity = "identity-" + alias,
        ErrorCode = "catalog_unavailable",
    };

    private static ProviderCatalogSnapshot DifferentIdentityFailure(string alias) => new()
    {
        Status = "error",
        Backend = alias,
        Identity = "failed-" + alias,
        ErrorCode = "catalog_unavailable",
    };

    [Fact]
    public async Task Refresh_starts_every_alias_once_before_awaiting_completion()
    {
        var controlled = new ControlledCatalogs();
        await using var controller = new GroupedProviderCatalogController(controlled.CreateService());

        var refresh = controller.RefreshAsync(force: true);
        await WaitForAsync(
            () => controlled.StartedAliases().ToHashSet(StringComparer.Ordinal)
                .SetEquals(ProviderGroupDefinitions.All.Select(definition => definition.Alias)),
            "every provider refresh to start");
        // No completion has been awaited yet; all fetches are already in flight.
        controlled.CompleteAll(alias => Snapshot(alias, "model-" + alias));
        Assert.True(await refresh);
    }

    [Fact]
    public async Task SetModelsDevProviders_adds_dynamic_children_and_publishes_their_state()
    {
        var controlled = new ControlledCatalogs();
        controlled.EnsureSource("models.dev:openrouter");
        await using var controller = new GroupedProviderCatalogController(controlled.CreateService());
        await controller.SetModelsDevProvidersAsync([new ModelsDevEntry("openrouter", "OpenRouter")]);

        Assert.Contains(
            controller.CurrentDefinitions,
            definition => definition.Alias == "models.dev:openrouter");

        var refresh = controller.RefreshAsync(force: true);
        controlled.Complete("models.dev:openrouter", Snapshot("models.dev:openrouter", "m1"));
        controlled.CompleteAll(alias => Snapshot(alias, "model-" + alias));
        Assert.True(await refresh.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(controller.State.Catalogs.ContainsKey("models.dev:openrouter"));
    }

    /// <summary>
    /// A provider registered after polling was switched on polls like the fixed ones.
    /// </summary>
    /// <remarks>
    /// The window enables polling once, then registers Models.dev providers as they
    /// arrive. A child built after that starts with polling off, and the due-check
    /// answers "not due" for a child that is not polling — so every Models.dev provider
    /// silently never fetched its models. The picker showed only the fixed group and
    /// read "No available models" while the provider's own catalog was fine.
    ///
    /// The existing registration test cannot catch this: it refreshes with force, which
    /// bypasses the polling flag entirely. This one uses the due path the window uses.
    /// </remarks>
    [Fact]
    public async Task A_provider_registered_after_polling_started_is_polled()
    {
        var controlled = new ControlledCatalogs();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        await using var controller = new GroupedProviderCatalogController(controlled.CreateService(), clock);
        controller.SetPollingActive(true);

        controlled.EnsureSource("models.dev:openrouter");
        await controller.SetModelsDevProvidersAsync([new ModelsDevEntry("openrouter", "OpenRouter")]);

        var refresh = controller.RefreshIfDueAsync();
        await WaitForAsync(
            () => controlled.StartedAliases().Contains("models.dev:openrouter"),
            "the late provider to poll");
        controlled.Complete("models.dev:openrouter", Snapshot("models.dev:openrouter", "m1"));
        controlled.CompleteAll(alias => Snapshot(alias, "model-" + alias));
        Assert.True(await refresh.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(controller.State.Catalogs.ContainsKey("models.dev:openrouter"));
    }

    /// <summary>
    /// A response whose backend names the catalog provider is accepted, not discarded.
    /// </summary>
    /// <remarks>
    /// The controller keys grouped state by the alias the picker groups on
    /// ("models.dev:openrouter"), so a response that reports the bare provider id looks
    /// like it answered for somebody else and gets dropped — which is what happened:
    /// the fetch succeeded with 458 models and the picker still read "No available
    /// models". Only the fixed group survived, because there the id and the alias are
    /// the same string.
    /// </remarks>
    [Fact]
    public async Task A_response_naming_the_catalog_provider_is_published()
    {
        var controlled = new ControlledCatalogs();
        controlled.EnsureSource("models.dev:openrouter");
        await using var controller = new GroupedProviderCatalogController(controlled.CreateService());
        await controller.SetModelsDevProvidersAsync([new ModelsDevEntry("openrouter", "OpenRouter")]);

        var refresh = controller.RefreshAsync(force: true);
        // What the backend actually answers for a Models.dev provider.
        controlled.Complete("models.dev:openrouter", Snapshot("models.dev:openrouter", "m1"));
        controlled.CompleteAll(alias => Snapshot(alias, "model-" + alias));
        Assert.True(await refresh.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.True(controller.State.Catalogs.ContainsKey("models.dev:openrouter"));
        Assert.True(controller.State.Catalogs["models.dev:openrouter"].IsSuccess);
    }

    /// <summary>
    /// A response that names some other provider is discarded rather than merged.
    /// </summary>
    /// <remarks>
    /// This is the guard that caught the old backend: it answered a Models.dev request
    /// with the bare provider id, the controller asked for "models.dev:openrouter", and
    /// the mismatch dropped every response. The guard is correct — keep it — and the
    /// backend now reports the canonical form. Pinned so a future backend change that
    /// reintroduces the bare id fails here instead of silently emptying the picker.
    /// </remarks>
    [Fact]
    public async Task A_response_for_a_different_provider_is_discarded()
    {
        var controlled = new ControlledCatalogs();
        controlled.EnsureSource("models.dev:openrouter");
        await using var controller = new GroupedProviderCatalogController(controlled.CreateService());
        await controller.SetModelsDevProvidersAsync([new ModelsDevEntry("openrouter", "OpenRouter")]);

        var refresh = controller.RefreshAsync(force: true);
        // The bare id, as the backend used to answer.
        controlled.Complete("models.dev:openrouter", Snapshot("openrouter", "m1"));
        controlled.CompleteAll(alias => Snapshot(alias, "model-" + alias));
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.DoesNotContain("openrouter", controller.State.Catalogs.Keys);
        Assert.False(controller.State.Catalogs.TryGetValue("models.dev:openrouter", out var openrouter)
            && openrouter.IsSuccess);
    }

    /// <summary>A provider registered while polling is off stays off until it is switched on.</summary>
    [Fact]
    public async Task A_provider_registered_while_polling_is_off_is_not_polled()
    {
        var controlled = new ControlledCatalogs();
        await using var controller = new GroupedProviderCatalogController(controlled.CreateService());

        controlled.EnsureSource("models.dev:openrouter");
        await controller.SetModelsDevProvidersAsync([new ModelsDevEntry("openrouter", "OpenRouter")]);

        Assert.False(await controller.RefreshIfDueAsync());
        Assert.DoesNotContain("models.dev:openrouter", controlled.StartedAliases());
    }

    [Fact]
    public async Task SetModelsDevProviders_removes_retired_dynamic_children()
    {
        var controlled = new ControlledCatalogs();
        controlled.EnsureSource("models.dev:openrouter");
        await using var controller = new GroupedProviderCatalogController(controlled.CreateService());
        await controller.SetModelsDevProvidersAsync([new ModelsDevEntry("openrouter", "OpenRouter")]);
        await controller.SetModelsDevProvidersAsync([]);

        Assert.DoesNotContain(
            controller.CurrentDefinitions,
            definition => definition.Alias == "models.dev:openrouter");
        Assert.DoesNotContain("models.dev:openrouter", controlled.StartedAliases());
    }

    [Fact]
    public async Task Publishes_each_successful_provider_independently()
    {
        var controlled = new ControlledCatalogs();
        await using var controller = await CreateControllerAsync(controlled);
        var kiloApplied = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.StateChanged += (_, state) =>
        {
            if (state.Catalogs.TryGetValue(OpenAiAlias, out var kilo) && kilo.IsSuccess)
            {
                kiloApplied.TrySetResult(true);
            }
        };

        var refresh = controller.RefreshAsync(force: true);
        controlled.Complete(OpenAiAlias, Snapshot(OpenAiAlias, "gpt-4o"));
        Assert.True(await kiloApplied.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(controller.State.Catalogs.TryGetValue(AnthropicAlias, out var openCode) && openCode.IsSuccess);

        controlled.CompleteAll(alias => Snapshot(alias, "model-" + alias));
        Assert.True(await refresh);
    }

    [Fact]
    public async Task Retains_same_identity_stale_child_data_and_hides_failed_children_without_data()
    {
        var controlled = new ControlledCatalogs();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        await using var controller = await CreateControllerAsync(controlled, clock);

        var first = controller.RefreshAsync(force: true);
        controlled.CompleteAll(alias => Snapshot(alias, "model-" + alias));
        Assert.True(await first);

        controlled.ReArmAll();
        clock.Advance(TimeSpan.FromSeconds(31));
        var second = controller.RefreshAsync(force: true);
        controlled.Complete(OpenAiAlias, SameIdentityFailure(OpenAiAlias));
        controlled.Complete(AnthropicAlias, DifferentIdentityFailure(AnthropicAlias));
        controlled.CompleteAll(alias => alias is OpenAiAlias or AnthropicAlias
            ? null
            : DifferentIdentityFailure(alias));
        Assert.True(await second);

        var kilo = controller.State.Catalogs[OpenAiAlias];
        Assert.True(kilo.Stale);
        Assert.True(kilo.Models.Count > 0);
        var openCode = controller.State.Catalogs[AnthropicAlias];
        Assert.False(openCode.IsSuccess);
        Assert.Empty(openCode.Models);
        Assert.False(openCode.Stale);
    }

    [Fact]
    public async Task Coalesces_overlapping_force_refreshes()
    {
        var controlled = new ControlledCatalogs();
        await using var controller = new GroupedProviderCatalogController(controlled.CreateService());

        var first = controller.RefreshAsync(force: true);
        var second = controller.RefreshAsync(force: true);

        Assert.True(ReferenceEquals(first, second));
        controlled.CompleteAll(alias => Snapshot(alias, "model-" + alias));
        Assert.True(await first);
        Assert.True(await second);
    }

    [Fact]
    public async Task Preserves_selection_and_search_text_across_refreshes()
    {
        var controlled = new ControlledCatalogs();
        await using var controller = new GroupedProviderCatalogController(controlled.CreateService());

        controller.SetSelection("openai", "gpt-4o");
        controller.SetSearchText("frontier");
        var refresh = controller.RefreshAsync(force: true);
        controlled.CompleteAll(alias => Snapshot(alias, "model-" + alias));
        Assert.True(await refresh);

        Assert.Equal("openai", controller.State.SelectedBackend);
        Assert.Equal("gpt-4o", controller.State.SelectedModel);
        Assert.Equal("frontier", controller.State.SearchText);
    }

    [Fact]
    public async Task Invalidate_rejects_late_state_and_a_new_refresh_survives_it()
    {
        var controlled = new ControlledCatalogs();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        await using var controller = await CreateControllerAsync(controlled, clock);
        var successfulEvents = 0;
        controller.StateChanged += (_, state) =>
        {
            if (state.Catalogs.Values.Any(catalog => catalog.IsSuccess)) successfulEvents++;
        };

        var refresh = controller.RefreshAsync(force: true);
        controller.Invalidate();
        controlled.CompleteAll(alias => Snapshot(alias, "model-" + alias));
        Assert.False(await refresh);

        Assert.Equal(0, successfulEvents);
        Assert.All(controller.State.Catalogs.Values, catalog => Assert.False(catalog.IsSuccess));

        controlled.ReArmAll();
        clock.Advance(TimeSpan.FromSeconds(31));
        var second = controller.RefreshAsync(force: true);
        controlled.CompleteAll(alias => Snapshot(alias, "fresh-model"));
        Assert.True(await second);
        Assert.Equal("fresh-model", controller.State.Catalogs[OpenAiAlias].Models.Single());
        Assert.Equal(3, successfulEvents);
    }

    [Fact]
    public async Task RefreshIfDue_polls_due_children_only_while_active()
    {
        var controlled = new ControlledCatalogs();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        await using var controller = await CreateControllerAsync(controlled, clock);
        var children = controller.CurrentDefinitions.Count;

        Assert.False(await controller.RefreshIfDueAsync());
        Assert.Empty(controlled.StartedAliases());

        controller.SetPollingActive(true);
        var refresh = controller.RefreshIfDueAsync();
        await WaitForAsync(() => controlled.StartedAliases().Count == children, "all children to poll");
        controlled.CompleteAll(alias => Snapshot(alias, "model-" + alias));
        Assert.True(await refresh);

        Assert.False(await controller.RefreshIfDueAsync());

        controller.SetPollingActive(false);
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.False(await controller.RefreshIfDueAsync());
        Assert.True(controlled.StartedAliases().Count == children);
    }

    [Fact]
    public async Task Dispose_cancels_children_and_rejects_post_dispose_publication()
    {
        var controlled = new ControlledCatalogs();
        await using var controller = new GroupedProviderCatalogController(controlled.CreateService());
        var events = 0;
        controller.StateChanged += (_, _) => events++;

        var refresh = controller.RefreshAsync(force: true);
        await controller.DisposeAsync();
        var eventsAtDispose = events;
        controlled.CompleteAll(alias => Snapshot(alias, "model-" + alias));
        // Awaiting the refresh orders every publication in its own pipeline, so
        // the no-event assertion needs no sleep window to be deterministic.
        Assert.False(await refresh);
        Assert.Equal(eventsAtDispose, events);

        controller.SetSelection("openai", "model");
        Assert.Equal(eventsAtDispose, events);
    }

    private static async Task WaitForAsync(Func<bool> condition, string description)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        throw new TimeoutException($"Timed out waiting for {description}.");
    }

    private sealed class ControlledCatalogs
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, TaskCompletionSource<ProviderCatalogSnapshot?>> _sources =
            new(StringComparer.Ordinal);
        private readonly HashSet<string> _started = new(StringComparer.Ordinal);

        public ControlledCatalogs()
        {
            foreach (var definition in ProviderGroupDefinitions.All)
            {
                _sources[definition.Alias] = CreateSource();
            }
        }

        public ProviderCatalogService CreateService() => new((backend, refresh, cancellationToken) =>
        {
            lock (_gate) _started.Add(backend);
            TaskCompletionSource<ProviderCatalogSnapshot?> source;
            lock (_gate) source = _sources[backend];
            return source.Task.WaitAsync(cancellationToken);
        });

        public IReadOnlyCollection<string> StartedAliases()
        {
            lock (_gate) return _started.ToArray();
        }

        public void EnsureSource(string alias)
        {
            lock (_gate) _sources.TryAdd(alias, CreateSource());
        }

        public void Complete(string alias, ProviderCatalogSnapshot snapshot)
        {
            TaskCompletionSource<ProviderCatalogSnapshot?> source;
            lock (_gate) source = _sources[alias];
            source.TrySetResult(snapshot);
        }

        public void CompleteAll(Func<string, ProviderCatalogSnapshot?> factory)
        {
            string[] aliases;
            lock (_gate) aliases = [.. _sources.Keys];
            foreach (var alias in aliases)
            {
                var snapshot = factory(alias);
                if (snapshot is not null) Complete(alias, snapshot);
            }
        }

        public void ReArmAll()
        {
            lock (_gate)
            {
                foreach (var alias in _sources.Keys)
                {
                    _sources[alias] = CreateSource();
                }
            }
        }

        private TaskCompletionSource<ProviderCatalogSnapshot?> CreateSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class TestClock : IProviderCatalogClock
    {
        public TestClock(DateTimeOffset utcNow) => UtcNow = utcNow;

        public DateTimeOffset UtcNow { get; private set; }

        public void Advance(TimeSpan amount) => UtcNow += amount;
    }
}
