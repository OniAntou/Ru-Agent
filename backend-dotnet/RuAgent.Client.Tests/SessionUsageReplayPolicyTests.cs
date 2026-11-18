using RuAgent.Client;
using Xunit;

namespace RuAgent.Client.Tests;

public sealed class SessionUsageReplayPolicyTests
{
    [Fact]
    public void The_newest_report_wins_and_keeps_its_step()
    {
        string[] lines =
        [
            """{"event_type":"model.usage_recorded","sequence":4,"model_step_id":"step_a","payload_ref":"pay_1"}""",
            """{"event_type":"model.step_started","sequence":5,"model_step_id":"step_b"}""",
            """{"event_type":"model.usage_recorded","sequence":9,"model_step_id":"step_b","payload_ref":"pay_2"}""",
        ];

        var report = SessionUsageReplayPolicy.FindLatestUsageReport(lines);

        Assert.NotNull(report);
        Assert.Equal(9, report!.Value.Sequence);
        Assert.Equal("step_b", report.Value.ModelStepId);
        Assert.Equal("pay_2", report.Value.PayloadRef);
        Assert.Equal("pay_2", SessionUsageReplayPolicy.FindLatestUsagePayloadRef(lines));
    }

    [Fact]
    public void A_compaction_checkpoint_report_is_billed_but_never_the_context_reading()
    {
        // Recorded shape: a run whose last reports were checkpoint requests before it failed.
        string[] lines =
        [
            """{"event_type":"model.usage_recorded","sequence":35,"safe_summary":"reported","payload_ref":"pay_step"}""",
            """{"event_type":"model.usage_recorded","sequence":51,"safe_summary":"checkpoint_usage","payload_ref":"pay_cp1"}""",
            """{"event_type":"context.compacted","sequence":53}""",
            """{"event_type":"model.usage_recorded","sequence":65,"safe_summary":"checkpoint_usage","payload_ref":"pay_cp2"}""",
        ];

        Assert.Equal("pay_step", SessionUsageReplayPolicy.FindLatestUsageReport(lines)!.Value.PayloadRef);
        var reports = SessionUsageReplayPolicy.FindUsageReports(lines);
        Assert.Equal(3, reports.Count);
        Assert.Equal([true, false, false], reports.Select(report => report.IsContextReading));
        Assert.False(SessionUsageReplayPolicy.IsContextReading("checkpoint_usage"));
        Assert.False(SessionUsageReplayPolicy.IsContextReading("advisor_usage"));
        Assert.True(SessionUsageReplayPolicy.IsContextReading("reported"));
    }

    [Fact]
    public void An_advisor_report_is_billed_but_never_the_context_reading()
    {
        string[] lines =
        [
            """{"event_type":"model.usage_recorded","sequence":4,"model_step_id":"step_a","payload_ref":"pay_main"}""",
            """{"event_type":"model.usage_recorded","sequence":6,"safe_summary":"advisor_usage","payload_ref":"pay_advisor"}""",
        ];

        Assert.Equal("pay_main", SessionUsageReplayPolicy.FindLatestUsageReport(lines)!.Value.PayloadRef);
        var reports = SessionUsageReplayPolicy.FindUsageReports(lines);
        Assert.Equal(["pay_main", "pay_advisor"], reports.Select(report => report.PayloadRef));
        Assert.Equal([false, true], reports.Select(report => report.Advisor));
    }

    [Fact]
    public void Sse_data_lines_and_plain_json_are_both_read()
    {
        string[] lines =
        [
            "event: message",
            """data: {"event_type":"model.usage_recorded","sequence":3,"payload_ref":"pay_sse"}""",
            string.Empty,
        ];

        var report = SessionUsageReplayPolicy.FindLatestUsageReport(lines);

        Assert.NotNull(report);
        Assert.Equal(3, report!.Value.Sequence);
        Assert.Equal("pay_sse", report.Value.PayloadRef);
    }

    [Fact]
    public void A_report_without_a_payload_ref_never_shadows_an_earlier_usable_one()
    {
        string[] lines =
        [
            """{"event_type":"model.usage_recorded","sequence":2,"payload_ref":"pay_1"}""",
            """{"event_type":"model.usage_recorded","sequence":3}""",
            """{"event_type":"model.usage_recorded","sequence":4,"payload_ref":""}""",
        ];

        Assert.Equal("pay_1", SessionUsageReplayPolicy.FindLatestUsagePayloadRef(lines));
    }

    [Fact]
    public void Malformed_lines_and_other_event_types_are_skipped()
    {
        string[] lines =
        [
            string.Empty,
            "   ",
            "data: not-json",
            "data: [1,2,3]",
            ": comment",
            """{"event_type":"tool.proposed","sequence":1,"payload_ref":"pay_tool"}""",
            """{"event_type":"model.usage_recorded","sequence":2,"payload_ref":"pay_ok"}""",
        ];

        Assert.Equal("pay_ok", SessionUsageReplayPolicy.FindLatestUsagePayloadRef(lines));
    }

    [Fact]
    public void A_run_that_never_reported_usage_yields_nothing()
    {
        string[] lines =
        [
            """{"event_type":"model.step_started","sequence":1}""",
            """{"event_type":"model.step_completed","sequence":2}""",
        ];

        Assert.Null(SessionUsageReplayPolicy.FindLatestUsageReport(lines));
        Assert.Null(SessionUsageReplayPolicy.FindLatestUsagePayloadRef(lines));
        Assert.Empty(SessionUsageReplayPolicy.FindUsagePayloadRefs(lines));
    }

    [Fact]
    public void Repeated_payload_refs_are_reported_once_in_first_seen_order()
    {
        string[] lines =
        [
            """{"event_type":"model.usage_recorded","sequence":1,"payload_ref":"pay_1"}""",
            """{"event_type":"model.usage_recorded","sequence":2,"payload_ref":"pay_2"}""",
            """{"event_type":"model.usage_recorded","sequence":3,"payload_ref":"pay_1"}""",
        ];

        Assert.Equal(["pay_1", "pay_2"], SessionUsageReplayPolicy.FindUsagePayloadRefs(lines));
    }

    [Fact]
    public void The_latest_report_follows_line_order_even_when_a_payload_repeats()
    {
        string[] lines =
        [
            """{"event_type":"model.usage_recorded","sequence":1,"payload_ref":"pay_1"}""",
            """{"event_type":"model.usage_recorded","sequence":2,"payload_ref":"pay_2"}""",
            """{"event_type":"model.usage_recorded","sequence":3,"payload_ref":"pay_1"}""",
        ];

        Assert.Equal(3, SessionUsageReplayPolicy.FindLatestUsageReport(lines)!.Value.Sequence);
    }
}
