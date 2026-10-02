using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RuBackend.Business;
using RuBackend.Business.Settings;
using RuBackend.Providers;
using Xunit;

namespace RuBackend.Tests.Providers;

/// <summary>
/// What the catalog publishes about price, and what it refuses to guess.
/// </summary>
/// <remarks>
/// A price is the one figure in this system that turns into money, so nearly every
/// case here is about refusing to produce a number rather than about producing one.
/// The three that matter most: a model nobody priced is not free, a model priced at
/// zero is free, and the tiered rates models.dev also publishes are not read at all,
/// because nothing in a usage report says which tier a run fell in.
/// </remarks>
public sealed class CatalogCostTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ru-cost-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task A_published_rate_reaches_the_snapshot_wire()
    {
        var snapshot = await ReadAsync(
            modelsDev: """
                "vendor/paid": {
                  "cost": { "input": 3, "output": 15, "cache_read": 0.3, "cache_write": 3.75 }
                }
                """,
            providerModels: """{"id":"vendor/paid"}""");

        var model = Assert.Single(snapshot.Catalog);
        Assert.Equal("vendor/paid", model.Id);
        Assert.NotNull(model.Cost);
        Assert.Equal(3, model.Cost!.Input);
        Assert.Equal(15, model.Cost.Output);
        Assert.Equal(0.3, model.Cost.CacheRead);
        Assert.Equal(3.75, model.Cost.CacheWrite);

        // The wire is what the GUI reads, so a rate that stopped here would price
        // nothing.
        var cost = Wire(snapshot);
        Assert.Equal(3, cost["input"]!.GetValue<double>());
        Assert.Equal(15, cost["output"]!.GetValue<double>());
        Assert.Equal(0.3, cost["cache_read"]!.GetValue<double>());
        Assert.Equal(3.75, cost["cache_write"]!.GetValue<double>());
    }

    [Fact]
    public async Task A_free_model_keeps_its_zero_rates_rather_than_reading_as_unpriced()
    {
        // models.dev publishes {"input":0,"output":0} for a model that is genuinely
        // free. Collapsing that into "no price" would show a free session as one whose
        // cost nobody knows.
        var snapshot = await ReadAsync(
            modelsDev: """ "vendor/free": { "cost": { "input": 0, "output": 0 } } """,
            providerModels: """{"id":"vendor/free"}""");

        var model = Assert.Single(snapshot.Catalog);
        Assert.NotNull(model.Cost);
        Assert.Equal(0, model.Cost!.Input);
        Assert.Equal(0, model.Cost.Output);
        Assert.Null(model.Cost.CacheRead);
        Assert.Null(model.Cost.CacheWrite);
    }

    [Fact]
    public async Task An_unpriced_model_publishes_no_rates_at_all()
    {
        var snapshot = await ReadAsync(
            modelsDev: """ "vendor/mystery": { "limit": { "context": 200000 } } """,
            providerModels: """{"id":"vendor/mystery"}""");

        var model = Assert.Single(snapshot.Catalog);
        Assert.Null(model.Cost);
        Assert.Equal(200000, model.ContextLength);
    }

    [Fact]
    public async Task A_published_context_limit_lands_on_the_model_and_the_wire()
    {
        var snapshot = await ReadAsync(
            modelsDev: """ "vendor/ctx": { "limit": { "context": 131072 } } """,
            providerModels: """{"id":"vendor/ctx"}""");

        var model = Assert.Single(snapshot.Catalog);
        Assert.Equal(131072, model.ContextLength);
        var root = JsonNode.Parse(snapshot.ToJson().ToJsonString())!.AsObject();
        Assert.Equal(131072, root["catalog"]!.AsArray()[0]!["context_length"]!.GetValue<long>());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("\"big\"")]
    [InlineData("100000001")]
    public async Task A_nonsense_context_limit_stays_unpublished(string context)
    {
        var snapshot = await ReadAsync(
            modelsDev: $$""" "vendor/bad": { "limit": { "context": {{context}} } } """,
            providerModels: """{"id":"vendor/bad"}""");

        Assert.Null(Assert.Single(snapshot.Catalog).ContextLength);
    }

    [Fact]
    public async Task An_absent_cache_rate_is_left_out_of_the_wire_entirely()
    {
        // Publishing cache_read: 0 would claim the provider charges nothing for a
        // cache read, which is a different and much stronger statement than saying
        // nothing about it.
        var snapshot = await ReadAsync(
            modelsDev: """ "vendor/paid": { "cost": { "input": 1, "output": 2 } } """,
            providerModels: """{"id":"vendor/paid"}""");

        var cost = Wire(snapshot);
        Assert.False(cost.AsObject().ContainsKey("cache_read"));
        Assert.False(cost.AsObject().ContainsKey("cache_write"));
    }

    [Theory]
    [InlineData("""{"input": 1}""")]
    [InlineData("""{"output": 1}""")]
    [InlineData("""{"input": -1, "output": 1}""")]
    [InlineData("""{"input": "free", "output": 1}""")]
    public async Task A_half_quote_publishes_no_price_at_all(string cost)
    {
        // Input and output are both required. A quote missing either cannot become a
        // total, and inventing the missing half would be a number nobody can audit.
        var snapshot = await ReadAsync(
            modelsDev: $$""" "vendor/odd": { "cost": {{cost}} } """,
            providerModels: """{"id":"vendor/odd"}""");

        Assert.Null(Assert.Single(snapshot.Catalog).Cost);
    }

    [Fact]
    public async Task One_unusable_cache_rate_does_not_void_the_whole_quote()
    {
        // A negative cache rate is bad data, not a discount. It costs that one rate and
        // nothing else: input and output were quoted cleanly, and dropping the whole
        // price over a bad third field would lose a figure that is usable.
        var snapshot = await ReadAsync(
            modelsDev: """ "vendor/odd": { "cost": { "input": 1, "output": 2, "cache_read": -3 } } """,
            providerModels: """{"id":"vendor/odd"}""");

        var cost = Assert.Single(snapshot.Catalog).Cost;
        Assert.NotNull(cost);
        Assert.Equal(1, cost!.Input);
        Assert.Equal(2, cost.Output);
        Assert.Null(cost.CacheRead);
    }

    [Fact]
    public async Task The_tiered_rates_are_ignored_rather_than_silently_undercounting()
    {
        // models.dev also publishes `tiers` and `context_over_200k`, which raise the
        // price past a context threshold. Nothing in a usage report says which side of
        // that threshold a run fell on, so the base rate is what ships and the tiers
        // stay unread rather than being quietly averaged in.
        var snapshot = await ReadAsync(
            modelsDev: """
                "vendor/tiered": {
                  "cost": {
                    "input": 3,
                    "output": 15,
                    "tiers": [ { "tier": "context > 200k", "input": 6, "output": 22.5 } ],
                    "context_over_200k": { "input": 6, "output": 22.5 }
                  }
                }
                """,
            providerModels: """{"id":"vendor/tiered"}""");

        var cost = Assert.Single(snapshot.Catalog).Cost;
        Assert.NotNull(cost);
        Assert.Equal(3, cost!.Input);
        Assert.Equal(15, cost.Output);
    }

    [Fact]
    public async Task A_price_reaches_a_model_the_host_declares_unsupported()
    {
        // The price is attached whatever the model's support state. A model the host
        // cannot call is never billed, so attaching prices only to the supported ones
        // would make "the host cannot call this" indistinguishable from "nobody has
        // heard of this model" — and the catalog's own parser has no supported flag to
        // consult, so pinning it here would test the fixture rather than the rule.
        var snapshot = await ReadAsync(
            modelsDev: """ "vendor/odd": { "cost": { "input": 3, "output": 15 } } """,
            providerModels: """{"id":"vendor/odd","protocol":"embeddings"}""");

        Assert.Equal(3, Assert.Single(snapshot.Catalog).Cost!.Input);
    }

    [Fact]
    public async Task A_catalog_that_cannot_be_read_publishes_no_rates_at_all()
    {
        // No fallback invents a price. A missing rate is the honest answer when the
        // source is unavailable, so the read fails closed and the snapshot is empty.
        var store = new SettingsStore(DataPaths.Resolve(_root, _root, _root, _root));
        await store.SaveAsync((await store.ReadAsync(default)) with
        { Backend = "anthropic", Model = "claude-x" }, default);

        var service = new ProviderCatalogService(
            new HttpClient(new ExplodingHandler()), new AlwaysKey(), TimeProvider.System);
        var snapshot = await service.GetAsync(await store.ReadAsync(default), false, default);

        Assert.Equal("error", snapshot.Status);
        Assert.Empty(snapshot.Catalog);
    }

    /// <summary>
    /// The cost object exactly as the GUI will read it, off the snapshot's own wire.
    /// </summary>
    /// <remarks>
    /// Returned as a live <see cref="JsonNode"/> rather than a <see cref="JsonElement"/>
    /// because an element belongs to the document that produced it, and a caller that
    /// holds one after the parse scope closes reads a disposed object instead of the
    /// number it asked for.
    /// </remarks>
    private static JsonNode Wire(ProviderCatalogSnapshot snapshot)
    {
        var root = JsonNode.Parse(snapshot.ToJson().ToJsonString())!.AsObject();
        return root["catalog"]!.AsArray()[0]!["cost"]
            ?? throw new InvalidOperationException("the snapshot published no cost");
    }

    /// <summary>
    /// Reads the whole path: the provider's own model list supplies the entries, and
    /// the models.dev catalog is layered on top of it. Both are answered locally, so a
    /// case states the real source of every field it asserts on.
    /// </summary>
    private async Task<ProviderCatalogSnapshot> ReadAsync(
        string modelsDev,
        string providerModels,
        string backend = "openrouter")
    {
        Directory.CreateDirectory(_root);
        var store = new SettingsStore(DataPaths.Resolve(_root, _root, _root, _root));
        await store.SaveAsync((await store.ReadAsync(default)) with
        {
            Backend = $"models.dev:{backend}",
            Model = "claude-x",
        }, default);

        var providerId = backend.StartsWith("models.dev:", StringComparison.Ordinal)
            ? backend["models.dev:".Length..]
            : backend;
        SeedModelsDevCache(providerId, modelsDev);
        var service = new ProviderCatalogService(
            new HttpClient(new CatalogHandler(providerModels)),
            new AlwaysKey(),
            TimeProvider.System,
            Path.Combine(_root, "models-dev.json"));
        var snapshot = await service.GetAsync(await store.ReadAsync(default), false, default);

        Assert.Equal("ok", snapshot.Status);
        return snapshot;
    }

    private sealed class ExplodingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("no catalog is reachable");
    }

    private sealed class CatalogHandler(string providerModels) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            // Only the provider's own model list is fetched here: the models.dev half is
            // served from the cache file, so a case cannot pass because the fetcher
            // happened to return something.
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"data":[{{providerModels}}]}"""),
            });
    }

    /// <summary>
    /// Writes a models.dev cache file that looks like the real one. The service
    /// discards a payload listing fewer than 50 providers, believing it truncated or
    /// proxied, so the filler is not decoration.
    /// </summary>
    private void SeedModelsDevCache(string providerId, string modelsDevEntry)
    {
        var filler = new System.Text.StringBuilder();
        for (int i = 0; i < 60; i++)
        {
            if (i > 0) _ = filler.Append(',');
            _ = filler.Append($"\"f{i:000}\":{{\"api\":\"https://f{i:000}.example/v1\"}}");
        }

        var payload = "{\"fetched_at\":\"2099-01-01T00:00:00+00:00\",\"payload\":{"
            + $"\"{providerId}\":{{\"api\":\"https://{providerId}.example/v1\",\"models\":{{{modelsDevEntry}}}}}"
            + "," + filler + "}}";
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "models-dev.json"), payload);
    }

    private sealed class AlwaysKey : ICredentialStore
    {
        // A models.dev backend requires a credential before its catalog is read at all,
        // so a store answering "no key" fails closed and never reaches the price. The
        // value goes nowhere: both handlers are local.
        public Task<string?> GetAsync(string service, string account, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("test-key");

        public Task SetAsync(string service, string account, string secret, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string service, string account, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
