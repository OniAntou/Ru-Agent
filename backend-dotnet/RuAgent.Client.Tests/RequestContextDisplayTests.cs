using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Client.Tests;

/// <summary>
/// The displayed context reading: estimates converted with the route's own report ratio, the
/// reply tail, the compaction point and the baseline before a first request.
/// </summary>
public sealed class RequestContextDisplayTests
{
    private static string Step(int index) => "step_" + index.ToString("x32");

    /// <summary>
    /// Recorded from a real run (hy4-flash-free, chat completions): each step's heuristic
    /// request breakdown and the prompt tokens the provider reported for that same request.
    /// The raw heuristic overstated every step by about 9-13%.
    /// </summary>
    private static readonly (int Heuristic, long Reported)[] RecordedSteps =
    [
        (48_816, 44_519), (59_636, 54_271), (68_330, 60_715), (69_488, 61_694),
        (71_599, 63_046), (74_939, 66_043), (76_409, 67_161), (77_947, 69_881),
        (80_847, 72_273), (84_251, 75_331),
    ];

    [Fact]
    public void Estimates_converted_with_the_last_report_stay_within_a_few_percent_of_the_next_report()
    {
        var reading = new RequestContextReading();
        reading.UseRoute(ContextBaselineMemory.RouteKey("chat_completions", "recorded-" + Guid.NewGuid().ToString("N")));
        long sequence = 0;
        for (var index = 0; index < RecordedSteps.Length; index++)
        {
            var (heuristic, reported) = RecordedSteps[index];
            var breakdown = new ContextTokenBreakdown(9_000, 6_000, heuristic - 15_000);
            Assert.True(reading.BeginStep(Step(index), ++sequence));
            Assert.True(reading.ObserveEstimate(new AgentUsageEstimateModel(heuristic, 1_000_000, Step(index), 1) { ContextTokens = breakdown }));
            if (index > 0)
            {
                var error = Math.Abs((double)reading.Tokens!.Value - reported) / reported;
                Assert.True(error < 0.03, $"step {index}: displayed {reading.Tokens} for a reported {reported}");
                // The raw heuristic, which the meter used to show, was off by far more.
                Assert.True(Math.Abs((double)heuristic - reported) / reported > 0.08);
            }

            Assert.True(reading.ObserveUsage(++sequence, Step(index), reported, breakdown));
            Assert.Equal(reported, reading.Tokens);
        }
    }

    [Fact]
    public void The_displayed_breakdown_sums_to_the_reading_and_the_reply_tail()
    {
        var reading = new RequestContextReading();
        reading.UseRoute(ContextBaselineMemory.RouteKey("chat_completions", "sum-" + Guid.NewGuid().ToString("N")));
        reading.BeginStep(Step(1), 1);
        Assert.True(reading.ObserveUsage(2, Step(1), 45_000, new ContextTokenBreakdown(10_000, 5_000, 35_000)));

        reading.ObserveReplyTail(4_000);

        Assert.Equal(900, reading.TailTokens); // 1,000 heuristic tokens at the route's 0.9 ratio
        Assert.Equal(45_900, reading.ContextTokens);
        var shown = reading.DisplayBreakdown!;
        Assert.Equal(9_000, shown.SystemPrompt);
        Assert.Equal(4_500, shown.ToolDefinitions);
        Assert.Equal(45_900, shown.Total);
    }

    [Fact]
    public void A_reply_tail_counts_only_against_a_reported_reading_and_ends_with_the_next_step()
    {
        var reading = new RequestContextReading();
        reading.ObserveReplyTail(4_000);
        Assert.Equal(0, reading.TailTokens); // no reading yet

        reading.BeginStep(Step(1), 1);
        reading.ObserveEstimate(new AgentUsageEstimateModel(10_000, 100_000, Step(1), 1));
        reading.ObserveReplyTail(4_000);
        Assert.Equal(0, reading.TailTokens); // an estimate already covers everything sent

        reading.ObserveUsage(2, Step(1), 9_000, null);
        reading.ObserveReplyTail(4_000);
        Assert.Equal(1_000, reading.TailTokens);
        Assert.Equal(10_000, reading.ContextTokens);

        reading.BeginStep(Step(2), 3);
        reading.ObserveEstimate(new AgentUsageEstimateModel(11_000, 100_000, Step(2), 1));
        Assert.Equal(0, reading.TailTokens);
        Assert.Equal(11_000, reading.ContextTokens);
    }

    [Fact]
    public void The_compaction_trigger_is_shown_in_the_units_of_the_reading()
    {
        var reading = new RequestContextReading();
        reading.UseRoute(ContextBaselineMemory.RouteKey("chat_completions", "trigger-" + Guid.NewGuid().ToString("N")));
        reading.BeginStep(Step(1), 1);
        reading.ObserveUsage(2, Step(1), 40_000, new ContextTokenBreakdown(5_000, 5_000, 40_000)); // ratio 0.8
        reading.BeginStep(Step(2), 3);
        reading.ObserveEstimate(new AgentUsageEstimateModel(60_000, 200_000, Step(2), 1)
        {
            ContextTokens = new ContextTokenBreakdown(5_000, 5_000, 50_000),
            CompactionTriggerTokens = 150_000,
        });

        Assert.Equal(48_000, reading.Tokens);
        Assert.Equal(200_000, reading.MaxPromptTokens);
        Assert.Equal(120_000, reading.CompactionTriggerTokens);
    }

    [Fact]
    public void A_new_route_forgets_the_ratio_and_an_untrusted_ratio_is_ignored()
    {
        var reading = new RequestContextReading();
        reading.UseRoute(ContextBaselineMemory.RouteKey("chat_completions", "route-a-" + Guid.NewGuid().ToString("N")));
        reading.BeginStep(Step(1), 1);
        reading.ObserveUsage(2, Step(1), 9_000, new ContextTokenBreakdown(1_000, 1_000, 8_000));
        Assert.Equal(0.9, reading.Ratio!.Value, 6);

        reading.UseRoute(ContextBaselineMemory.RouteKey("chat_completions", "route-b-" + Guid.NewGuid().ToString("N")));
        Assert.Null(reading.Ratio);

        reading.BeginStep(Step(2), 3);
        reading.ObserveUsage(4, Step(2), 50_000, new ContextTokenBreakdown(1_000, 1_000, 8_000)); // 5x: not trusted
        Assert.Null(reading.Ratio);
    }

    [Fact]
    public void A_route_seen_before_gives_a_new_session_its_fixed_prompt_baseline()
    {
        var model = "baseline-" + Guid.NewGuid().ToString("N");
        Assert.Null(ContextBaselineMemory.BaselineFor(ContextBaselineMemory.ModelKey(model)));

        var reading = new RequestContextReading();
        reading.UseRoute(ContextBaselineMemory.RouteKey("chat_completions", model));
        reading.BeginStep(Step(1), 1);
        reading.ObserveUsage(2, Step(1), 18_000, new ContextTokenBreakdown(10_000, 10_000, 0)); // ratio 0.9

        // A session that has not started knows the model it will use, not yet the protocol.
        Assert.Equal(18_000, ContextBaselineMemory.BaselineFor(ContextBaselineMemory.ModelKey(model)));
        Assert.Equal(18_000, ContextBaselineMemory.BaselineFor(ContextBaselineMemory.RouteKey("chat_completions", model)));
    }
}
