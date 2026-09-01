using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class GroupedProviderCatalogControllerTests
{
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
        // No completion has been awaited yet; all seven fetches are already in flight.
        controlled.CompleteAll(alias => Snapshot(alias, "model-" + alias));
        Assert.True(await refresh);
    }

    [Fact]
    public async Task Publishes_each_successful_provider_independently()
    {
        var controlled = new ControlledCatalogs();
        await using var controller = new GroupedProviderCatalogController(controlled.CreateService());
        var kiloApplied = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.StateChanged += (_, state) =>
        {
            if (state.Catalogs.TryGetValue("kilo", out var kilo) && kilo.IsSuccess)
            {
                kiloApplied.TrySetResult(true);
            }
        };

        var refresh = controller.RefreshAsync(force: true);
        controlled.Complete("kilo", Snapshot("kilo", "kilo-auto/frontier"));
        Assert.True(await kiloApplied.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(controller.State.Catalogs.TryGetValue("opencode", out var openCode) && openCode.IsSuccess);

        controlled.CompleteAll(alias => Snapshot(alias, "model-" + alias));
        Assert.True(await refresh);
    }

    [Fact]
    public async Task Retains_same_identity_stale_child_data_and_hides_failed_children_without_data()
    {
        var controlled = new ControlledCatalogs();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        await using var controller = new GroupedProviderCatalogController(controlled.CreateService(), clock);

        var first = controller.RefreshAsync(force: true);
        controlled.CompleteAll(alias => Snapshot(alias, "model-" + alias));
        Assert.True(await first);

        controlled.ReArmAll();
        clock.Advance(TimeSpan.FromSeconds(31));
        var second = controller.RefreshAsync(force: true);
        controlled.Complete("kilo", SameIdentityFailure("kilo"));
        controlled.Complete("opencode", DifferentIdentityFailure("opencode"));
        controlled.CompleteAll(alias => alias is "kilo" or "opencode"
            ? null
            : DifferentIdentityFailure(alias));
        Assert.True(await second);

        var kilo = controller.State.Catalogs["kilo"];
        Assert.True(kilo.Stale);
        Assert.True(kilo.Models.Count > 0);
        var openCode = controller.State.Catalogs["opencode"];
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

        controller.SetSelection("kilo", "kilo-auto/frontier");
        controller.SetSearchText("frontier");
        var refresh = controller.RefreshAsync(force: true);
        controlled.CompleteAll(alias => Snapshot(alias, "model-" + alias));
        Assert.True(await refresh);

        Assert.Equal("kilo", controller.State.SelectedBackend);
        Assert.Equal("kilo-auto/frontier", controller.State.SelectedModel);
        Assert.Equal("frontier", controller.State.SearchText);
    }

    [Fact]
    public async Task Invalidate_rejects_late_state_from_the_previous_generation()
    {
        var controlled = new ControlledCatalogs();
        await using var controller = new GroupedProviderCatalogController(controlled.CreateService());
        var events = 0;
        var successfulEvents = 0;
        controller.StateChanged += (_, state) =>
        {
            events++;
            if (state.Catalogs.Values.Any(catalog => catalog.IsSuccess)) successfulEvents++;
        };

        var refresh = controller.RefreshAsync(force: true);
        controller.Invalidate();
        controlled.CompleteAll(alias => Snapshot(alias, "model-" + alias));
        Assert.False(await refresh);

        Assert.Equal(0, successfulEvents);
        Assert.All(controller.State.Catalogs.Values, catalog => Assert.False(catalog.IsSuccess));
    }

    [Fact]
    public async Task RefreshIfDue_polls_due_children_only_while_active()
    {
        var controlled = new ControlledCatalogs();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        await using var controller = new GroupedProviderCatalogController(controlled.CreateService(), clock);

        Assert.False(await controller.RefreshIfDueAsync());
        Assert.Empty(controlled.StartedAliases());

        controller.SetPollingActive(true);
        var refresh = controller.RefreshIfDueAsync();
        await WaitForAsync(() => controlled.StartedAliases().Count == 7, "all children to poll");
        controlled.CompleteAll(alias => Snapshot(alias, "model-" + alias));
        Assert.True(await refresh);

        Assert.False(await controller.RefreshIfDueAsync());

        controller.SetPollingActive(false);
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.False(await controller.RefreshIfDueAsync());
        Assert.True(controlled.StartedAliases().Count == 7);
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
        await Task.Delay(50);
        Assert.Equal(eventsAtDispose, events);
        Assert.False(await refresh);

        controller.SetSelection("kilo", "model");
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
