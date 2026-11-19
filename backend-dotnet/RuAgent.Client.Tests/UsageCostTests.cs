using RuAgent.Client.Usage;
using Xunit;

namespace RuAgent.Client.Tests;

/// <summary>The one pricing rule set the desktop usage panel and the terminal's /status usage share.</summary>
public sealed class UsageCostTests
{
    private static readonly UsageRates Paid = new(Input: 1, Output: 2, CacheRead: .5, CacheWrite: 3);

    [Theory]
    [InlineData(true, 1000)]
    [InlineData(false, 600)]
    public void Inclusive_and_exclusive_accounting_price_the_same_request_the_same(bool inclusive, long input)
    {
        var cost = UsageCost.Price(new(input, 100, 400, 0, inclusive), Paid);

        Assert.Equal(.001m, cost);
    }

    [Fact]
    public void A_provider_that_reports_no_cache_counts_is_priced_on_input_and_output()
    {
        Assert.Equal(.0012m, UsageCost.Price(new(1000, 100, null, null, null), Paid));
    }

    [Fact]
    public void Missing_input_or_output_is_unknown_not_free()
    {
        Assert.Null(UsageCost.Price(new(1000, null, 0, 0, true), Paid));
        Assert.Null(UsageCost.Price(new(null, 100, 0, 0, true), Paid));
    }

    [Fact]
    public void Cache_traffic_without_an_accounting_flag_is_not_split_by_guess()
    {
        Assert.Null(UsageCost.Price(new(1000, 100, 400, 0, null), Paid));
    }

    [Fact]
    public void A_measured_part_without_a_published_rate_makes_the_cost_unknown()
    {
        Assert.Null(UsageCost.Price(new(100, 10, 20, 0, true), Paid with { CacheRead = null }));
        Assert.Null(UsageCost.Price(new(100, 10, 0, 0, true), Paid with { Input = double.NaN }));
        // An unmeasured part needs no rate.
        Assert.NotNull(UsageCost.Price(new(100, 10, 0, 0, true), Paid with { CacheRead = null }));
    }

    [Fact]
    public void A_free_model_is_free_however_its_cache_is_billed()
    {
        Assert.Equal(0m, UsageCost.Price(new(1000, 100, 400, 50, null), new(0, 0, null, null)));
    }

    [Fact]
    public void An_aggregate_with_a_missing_cache_count_is_unknown()
    {
        Assert.Null(UsageCost.Price(new(1000, 100, null, 0, true, IsAggregate: true), Paid));
        Assert.NotNull(UsageCost.Price(new(1000, 100, 0, 0, true, IsAggregate: true), Paid));
    }
}
