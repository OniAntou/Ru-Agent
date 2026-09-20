using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class SessionUsageReplayPolicyTests
{
    [Fact]
    public void Latest_report_wins_inside_one_run()
    {
        string? payloadRef = SessionUsageReplayPolicy.FindLatestUsagePayloadRef(new[]
        {
            "data: {\"event_type\":\"model.step_started\",\"sequence\":1}",
            "data: {\"event_type\":\"model.usage_recorded\",\"payload_ref\":\"usage-1\",\"sequence\":2}",
            "data: {\"event_type\":\"tool.execution_completed\",\"sequence\":3}",
            "data: {\"event_type\":\"model.usage_recorded\",\"payload_ref\":\"usage-2\",\"sequence\":4}",
        });

        Assert.Equal("usage-2", payloadRef);
    }

    [Fact]
    public void No_report_means_no_payload_ref()
    {
        Assert.Null(SessionUsageReplayPolicy.FindLatestUsagePayloadRef(new[]
        {
            "data: {\"event_type\":\"model.step_started\",\"sequence\":1}",
            "data: {\"event_type\":\"run.state_changed\",\"sequence\":2}",
        }));
    }

    [Fact]
    public void Usage_report_without_a_payload_ref_is_skipped()
    {
        // A later empty report must not shadow an earlier usable one.
        string? payloadRef = SessionUsageReplayPolicy.FindLatestUsagePayloadRef(new[]
        {
            "data: {\"event_type\":\"model.usage_recorded\",\"payload_ref\":\"usage-1\",\"sequence\":2}",
            "data: {\"event_type\":\"model.usage_recorded\",\"sequence\":3}",
            "data: {\"event_type\":\"model.usage_recorded\",\"payload_ref\":\"\",\"sequence\":4}",
        });

        Assert.Equal("usage-1", payloadRef);
    }

    [Fact]
    public void Malformed_lines_do_not_break_the_scan()
    {
        string? payloadRef = SessionUsageReplayPolicy.FindLatestUsagePayloadRef(new[]
        {
            string.Empty,
            "   ",
            "data: not-json",
            "data: [1,2,3]",
            ": comment",
            "data: {\"event_type\":\"model.usage_recorded\",\"payload_ref\":\"usage-9\",\"sequence\":5}",
        });

        Assert.Equal("usage-9", payloadRef);
    }

    [Fact]
    public void Plain_json_lines_are_accepted_beside_sse_frames()
    {
        string? payloadRef = SessionUsageReplayPolicy.FindLatestUsagePayloadRef(new[]
        {
            "{\"event_type\":\"model.usage_recorded\",\"payload_ref\":\"usage-plain\"}",
        });

        Assert.Equal("usage-plain", payloadRef);
    }
}
