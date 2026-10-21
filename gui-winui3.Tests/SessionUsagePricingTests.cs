using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// What a session's tokens cost, and what the panel is allowed to claim.
/// </summary>
/// <remarks>
/// A price is the one figure here that turns into money, so most of these cases are
/// about the cases where the honest answer is "I cannot price that". The three that
/// matter most: a run nobody can price keeps no figure rather than being priced at
/// zero, a total never covers more runs than it priced, and a model published as free
/// really does come out free.
/// </remarks>
public sealed class SessionUsagePricingTests
{
    [Theory]
    [InlineData(null, 20, 30)]
    [InlineData(10, null, 30)]
    [InlineData(10, 20, null)]
    public void Aggregated_run_with_an_unreported_billing_bucket_has_no_partial_price(int? output, int? read, int? write)
    {
        var usage = RunUsageAccumulator.Sum([new AgentModelUsage(100, output, null, read, write, "vendor/paid", false)]);
        var prices = SessionUsagePricing.PriceRuns([new SessionUsageRow("run", true, "completed", null, null, usage)],
            new Dictionary<string, ProviderCatalogCost> { ["vendor/paid"] = Rate(cacheRead: 0.3, cacheWrite: 0.6) });
        Assert.Null(prices["run"].Dollars);
    }

    [Fact]
    public void Legacy_cached_usage_is_readable_but_has_no_guessed_price()
    {
        var usage = AgentModelUsage.Parse("""
            {"input_tokens":1000,"output_tokens":1,"cache_read_input_tokens":998,"model":"vendor/paid"}
            """);
        Assert.NotNull(usage);
        Assert.Null(usage.CacheHitPercent);
        var prices = SessionUsagePricing.PriceRuns([new SessionUsageRow("run", true, "completed", null, null, usage)],
            new Dictionary<string, ProviderCatalogCost> { ["vendor/paid"] = Rate(cacheRead: 0.3) });
        Assert.Null(prices["run"].Dollars);
    }

    [Fact]
    public void Inclusive_prompt_counts_do_not_bill_cached_input_at_the_uncached_rate_twice()
    {
        var usage = AgentModelUsage.Parse("""
            {"input_tokens":1000000,"output_tokens":0,"cache_read_input_tokens":900000,
             "input_tokens_include_cache":true,"model":"vendor/paid"}
            """);
        var row = new SessionUsageRow("run", true, "completed", null, null, usage);
        var prices = SessionUsagePricing.PriceRuns([row], new Dictionary<string, ProviderCatalogCost>
        {
            ["vendor/paid"] = Rate(input: 3, output: 15, cacheRead: 0.3),
        });
        Assert.Equal(0.57m, prices["run"].Dollars);
    }

    private static ProviderCatalogCost Rate(
        double input = 3, double output = 15, double? cacheRead = null, double? cacheWrite = null) =>
        new() { Input = input, Output = output, CacheRead = cacheRead, CacheWrite = cacheWrite };

    private static ProviderCatalogState Catalog(params (string Id, ProviderCatalogCost Cost)[] models) =>
        new()
        {
            Status = "ok",
            Catalog = models
                .Select(pair => new ProviderCatalogModel { Id = pair.Id, Cost = pair.Cost })
                .ToList(),
        };

    private static SessionUsageRow Row(
        string id = "run_1",
        bool terminal = true,
        int? input = null,
        int? output = null,
        int? cacheRead = null,
        int? cacheWrite = null,
        string? model = "vendor/paid") =>
        new(
            id,
            terminal,
            terminal ? "completed" : "running",
            null,
            null,
            model is null ? null : new AgentModelUsage(input, output, null, cacheRead, cacheWrite, model, InputTokensIncludeCache: false));

    [Fact]
    public void A_run_costs_its_tokens_times_its_rates_per_million()
    {
        // 1M input at $3/M is $3, 1M output at $15/M is $15.
        var prices = SessionUsagePricing.PriceRuns(
            new[] { Row(input: 1_000_000, output: 1_000_000) },
            SessionUsagePricing.RatesFrom(Catalog(("vendor/paid", Rate()))));

        Assert.Equal(18m, Assert.Single(prices.Values).Dollars);
    }

    [Fact]
    public void A_fractional_run_costs_a_fraction()
    {
        var prices = SessionUsagePricing.PriceRuns(
            new[] { Row(input: 4_000, output: 83) },
            SessionUsagePricing.RatesFrom(Catalog(("vendor/paid", Rate()))));

        // 4000/1M * 3 + 83/1M * 15
        Assert.Equal(0.012m + 0.001245m, prices.Values.Single().Dollars);
    }

    [Fact]
    public void A_free_model_really_is_free()
    {
        // models.dev publishes zero for a free model, and that has to come out as a
        // priced zero rather than as "unknown": a real total of $0.00 is information.
        var prices = SessionUsagePricing.PriceRuns(
            new[] { Row(input: 1_000_000, output: 1_000_000, model: "vendor/free") },
            SessionUsagePricing.RatesFrom(Catalog(("vendor/free", Rate(0, 0)))));

        var price = prices.Values.Single();
        Assert.True(price.IsPriced);
        Assert.Equal(0m, price.Dollars);
    }

    [Fact]
    public void A_free_model_is_free_through_its_cache_too_even_with_no_cache_rate_published()
    {
        // A model priced at zero for both input and output is a statement that it is
        // free, not an absence of rates. Free models on models.dev publish no
        // cache_read at all, and reading that silence as "the cache read cost an
        // unknown amount" reported a genuinely free session as unpriceable. Found by
        // opening a real session: a free model reported as having no rate.
        var prices = SessionUsagePricing.PriceRuns(
            new[] { Row(input: 4_012, output: 17, cacheRead: 3_584, model: "vendor/free") },
            SessionUsagePricing.RatesFrom(Catalog(("vendor/free", Rate(0, 0)))));

        var price = prices.Values.Single();
        Assert.True(price.IsPriced);
        Assert.Equal(0m, price.Dollars);
        Assert.Equal(0m, SessionUsagePricing.Total(prices.Values));
    }

    [Fact]
    public void A_paid_model_with_no_cache_rate_still_refuses_to_price()
    {
        // The free case is special because zero is a price. On a paid model the same
        // silence means the amount is genuinely unknown, and refusing is the honest
        // answer: a total quietly missing a measured part is not the cost of anything.
        var prices = SessionUsagePricing.PriceRuns(
            new[] { Row(input: 1_000, output: 100, cacheRead: 3_584, model: "vendor/paid") },
            SessionUsagePricing.RatesFrom(Catalog(("vendor/paid", Rate()))));

        Assert.False(prices.Values.Single().IsPriced);
    }

    [Fact]
    public void A_model_the_catalog_does_not_list_carries_no_figure_rather_than_a_free_one()
    {
        // This is the case that matters: a session that ran on a backend the current
        // catalog does not describe must not read as a session that cost nothing.
        var prices = SessionUsagePricing.PriceRuns(
            new[] { Row(input: 1_000_000, model: "other-backend/old-model") },
            SessionUsagePricing.RatesFrom(Catalog(("vendor/paid", Rate()))));

        var price = prices.Values.Single();
        Assert.False(price.IsPriced);
        Assert.Null(price.Dollars);
        Assert.True(price.ModelKnown);
        Assert.False(price.RateKnown);
        Assert.Equal("no rate", price.Blocker);
    }

    [Fact]
    public void A_run_that_named_no_model_says_so_distinctly_from_a_run_with_no_rate()
    {
        var prices = SessionUsagePricing.PriceRuns(
            new[] { Row(input: 100, model: null) },
            SessionUsagePricing.RatesFrom(Catalog(("vendor/paid", Rate()))));

        var price = prices.Values.Single();
        Assert.False(price.IsPriced);
        Assert.False(price.ModelKnown);
        Assert.Equal("no model", price.Blocker);
    }

    [Fact]
    public void A_run_with_no_report_costs_nothing_and_is_not_a_failure()
    {
        var prices = SessionUsagePricing.PriceRuns(
            new[] { Row(input: null, model: "vendor/paid") },
            SessionUsagePricing.RatesFrom(Catalog(("vendor/paid", Rate()))));

        var price = prices.Values.Single();
        Assert.True(price.IsPriced);
        Assert.Equal(0m, price.Dollars);
    }

    [Fact]
    public void Cache_traffic_is_priced_at_its_own_rates()
    {
        var prices = SessionUsagePricing.PriceRuns(
            new[] { Row(input: 0, output: 0, cacheRead: 1_000_000, cacheWrite: 1_000_000) },
            SessionUsagePricing.RatesFrom(Catalog((
                "vendor/paid",
                Rate(cacheRead: 0.3, cacheWrite: 3.75)))));

        Assert.Equal(0.3m + 3.75m, prices.Values.Single().Dollars);
    }

    [Fact]
    public void Measured_cache_traffic_with_no_published_rate_costs_nothing_and_cannot_be_priced()
    {
        // The cache rates are the ones providers most often leave out, and treating the
        // gap as a free cache read would understate the session by a real amount. A run
        // that cannot be fully priced keeps no figure at all.
        var prices = SessionUsagePricing.PriceRuns(
            new[] { Row(input: 1_000, output: 100, cacheRead: 500_000) },
            SessionUsagePricing.RatesFrom(Catalog(("vendor/paid", Rate()))));

        var price = prices.Values.Single();
        Assert.False(price.IsPriced);
        Assert.Null(price.Dollars);
    }

    [Fact]
    public void Unmeasured_cache_traffic_needs_no_rate_at_all()
    {
        // Nobody measured cache, so there is nothing to price and no reason to refuse.
        var prices = SessionUsagePricing.PriceRuns(
            new[] { Row(input: 1_000, output: 100) },
            SessionUsagePricing.RatesFrom(Catalog(("vendor/paid", Rate()))));

        Assert.True(prices.Values.Single().IsPriced);
    }

    [Fact]
    public void A_total_covers_only_the_runs_it_priced()
    {
        var rows = new[]
        {
            Row("run_1", input: 1_000_000, model: "vendor/paid"),
            Row("run_2", input: 1_000_000, model: "gone/model"),
        };
        var prices = SessionUsagePricing.PriceRuns(
            rows,
            SessionUsagePricing.RatesFrom(Catalog(("vendor/paid", Rate()))));

        // One run priced, one refused. The total is the priced one, and the panel is
        // expected to say the other exists rather than present this as the session's
        // whole cost.
        Assert.Equal(3m, SessionUsagePricing.Total(prices.Values));
        Assert.Equal(1, prices.Values.Count(price => !price.IsPriced));
    }

    [Fact]
    public void No_priced_run_means_no_total_rather_than_a_total_of_zero()
    {
        var prices = SessionUsagePricing.PriceRuns(
            new[] { Row(input: 1_000, model: "gone/model") },
            SessionUsagePricing.RatesFrom(Catalog(("vendor/paid", Rate()))));

        Assert.Null(SessionUsagePricing.Total(prices.Values));
    }

    [Fact]
    public void An_empty_session_has_no_total()
    {
        Assert.Null(SessionUsagePricing.Total(
            SessionUsagePricing.PriceRuns(
                Array.Empty<SessionUsageRow>(),
                SessionUsagePricing.RatesFrom(Catalog(("vendor/paid", Rate())))).Values));
    }

    [Fact]
    public void A_catalog_that_failed_or_went_stale_prices_nothing()
    {
        // Pricing a session from a catalog that may no longer describe the provider
        // would attribute yesterday's rates to today's runs. A stale read is worse than
        // no read, because it looks like one.
        foreach (var catalog in new[]
        {
            new ProviderCatalogState { Status = "error", Catalog = new[] { new ProviderCatalogModel { Id = "vendor/paid", Cost = Rate() } } },
            new ProviderCatalogState { Status = "ok", Stale = true, Catalog = new[] { new ProviderCatalogModel { Id = "vendor/paid", Cost = Rate() } } },
            new ProviderCatalogState { Status = "ok" },
        })
        {
            Assert.Empty(SessionUsagePricing.RatesFrom(catalog));
        }

        Assert.Empty(SessionUsagePricing.RatesFrom(null));
    }

    [Fact]
    public void Rates_are_matched_whatever_the_case_of_the_model_id()
    {
        var prices = SessionUsagePricing.PriceRuns(
            new[] { Row(input: 1_000_000, model: "Vendor/Paid") },
            SessionUsagePricing.RatesFrom(Catalog(("vendor/paid", Rate()))));

        Assert.True(prices.Values.Single().IsPriced);
    }

    [Fact]
    public void A_model_with_no_published_rate_is_absent_from_the_table_not_priced_at_zero()
    {
        var rates = SessionUsagePricing.RatesFrom(Catalog(
            ("vendor/paid", Rate()),
            ("vendor/mystery", null!)));

        Assert.True(rates.ContainsKey("vendor/paid"));
        Assert.False(rates.ContainsKey("vendor/mystery"));
    }

    [Theory]
    [InlineData("0", "$0.0000")]
    [InlineData("0.0004", "$0.0004")]
    [InlineData("0.009", "$0.0090")]
    [InlineData("0.01", "$0.01")]
    [InlineData("0.5", "$0.50")]
    [InlineData("0.999", "$1.00")]
    [InlineData("1", "$1")]
    [InlineData("12.4", "$12")]
    [InlineData("1234", "$1234")]
    public void Money_is_written_at_the_precision_the_size_justifies(string dollars, string expected)
    {
        // Under a cent, more than two decimals says more than the number knows.
        Assert.Equal(expected, SessionUsagePricing.FormatDollars(
            decimal.Parse(dollars, NumberStyles.Float, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void An_absent_figure_is_a_dash_and_never_zero_dollars()
    {
        // The one rendering that must not exist: "$0.00" for a session nobody could
        // price. It is indistinguishable from a genuinely free session.
        Assert.Equal("—", SessionUsagePricing.FormatDollars(null));
    }
}
