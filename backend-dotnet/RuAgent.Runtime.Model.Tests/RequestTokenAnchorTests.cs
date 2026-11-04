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
    public void A_lost_anchor_falls_back_to_the_calibrated_heuristic_for_the_same_route_and_model()
    {
        // Regression: bytes/4 undercounted DeepSeek by ~25%. After the anchor was lost
        // (compaction, rewritten prefix, idle) the raw heuristic moved the 80% trigger
        // to about the full window.
        var body = Body(new string('x', 40_000));
        var heuristic = (System.Text.Encoding.UTF8.GetByteCount(body) + 3L) / 4;
        var meter = new RequestTokenAnchor();
        meter.Observe("run", body, "route", "deepseek", new ModelUsage((int)(heuristic * 13 / 10), 10, null, null, null, "deepseek", true));

        var rewritten = Body(new string('y', 40_000));
        var calibrated = meter.Estimate("other-run", rewritten, "route", heuristic, "deepseek");
        Assert.InRange(calibrated, heuristic * 129 / 100, heuristic * 131 / 100);
        // Another model or route keeps the raw heuristic; the ratio never lowers it.
        Assert.Equal(heuristic, meter.Estimate("other-run", rewritten, "route", heuristic, "other-model"));
        Assert.Equal(heuristic, meter.Estimate("other-run", rewritten, "route2", heuristic, "deepseek"));

        var generous = new RequestTokenAnchor();
        generous.Observe("run", body, "route", "deepseek", new ModelUsage((int)(heuristic / 2), 10, null, null, null, "deepseek", true));
        Assert.Equal(heuristic, generous.Estimate("other-run", rewritten, "route", heuristic, "deepseek"));
    }

    [Fact]
    public void Small_requests_do_not_calibrate()
    {
        var meter = new RequestTokenAnchor();
        meter.Observe("run", Original, "route", "fixture", new ModelUsage(10000, 50, null, null, null, "fixture", true));
        Assert.Equal(100, meter.Estimate("other-run", Extended, "route", 100, "fixture"));
    }

    private static string Body(string content) =>
        "{\"model\":\"deepseek\",\"tools\":[],\"messages\":[{\"role\":\"user\",\"content\":\"" + content + "\"}]}";

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
