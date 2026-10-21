using RuAgent.Harness.Core;
using Xunit;

namespace RuAgent.Runtime.Model.Tests;

public sealed class RequestTokenAnchorTests
{
    private const string Original = "{\"model\":\"fixture\",\"tools\":[],\"messages\":[{\"role\":\"user\",\"content\":\"old\"}]}";
    private const string Extended = "{\"model\":\"fixture\",\"tools\":[],\"messages\":[{\"role\":\"user\",\"content\":\"old\"},{\"role\":\"assistant\",\"content\":\"new\"}]}";

    [Theory]
    [InlineData(true, 10000)]
    [InlineData(false, 13000)]
    public void Uses_provider_input_plus_only_new_prefix_bytes_without_double_counting_cache(bool inclusive, int expected)
    {
        var meter = new RequestTokenAnchor();
        meter.Observe("run", Original, "route", "fixture", new ModelUsage(10000, 50, null, 2000, 1000, "fixture", inclusive));
        var result = meter.Estimate("run", Extended, "route", 100);
        Assert.InRange(result, expected + 1, expected + 100);
        Assert.Equal(expected, meter.Estimate("run", Original, "route", 100));
        Assert.Equal(20000, meter.Estimate("run", Extended, "route", 20000));
    }

    [Theory]
    [InlineData("route2", "fixture", "old")]
    [InlineData("route", "other", "old")]
    [InlineData("route", "fixture", "rewritten")]
    public void Changed_route_model_or_history_invalidates_the_anchor(string route, string model, string text)
    {
        var meter = new RequestTokenAnchor();
        meter.Observe("run", Original, "route", "fixture", new ModelUsage(10000, 50, null, null, null, "fixture", true));
        var body = Original.Replace("fixture", model).Replace("old", text);
        Assert.Equal(100, meter.Estimate("run", body, route, 100));
        Assert.Equal(100, meter.Estimate("run", Extended, "route", 100));
    }

    [Fact]
    public void Missing_unknown_or_mismatched_usage_cannot_seed_an_anchor_or_cross_runs()
    {
        var meter = new RequestTokenAnchor();
        foreach (var usage in new ModelUsage?[] { null, new(1000, 1, null, null, null, "fixture"), new(1000, 1, null, null, null, "other", true) })
        {
            meter.Observe("run", Original, "route", "fixture", usage);
            Assert.Equal(100, meter.Estimate("run", Extended, "route", 100));
        }
        meter.Observe("run", Original, "route", "fixture", new ModelUsage(10000, 50, null, null, null, "fixture", true));
        Assert.Equal(100, meter.Estimate("other-run", Extended, "route", 100));
    }
}
