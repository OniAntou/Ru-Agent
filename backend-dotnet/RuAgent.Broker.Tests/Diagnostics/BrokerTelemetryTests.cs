using System.Diagnostics;
using System.Text.Json.Nodes;
using RuAgent.Broker.Diagnostics;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests.Diagnostics;

public sealed class BrokerTelemetryTests
{
    [Fact]
    public void StartToolActivity_returns_null_without_listener()
    {
        // No ActivityListener is registered anywhere in this test process,
        // so the source must stay silent: telemetry never perturbs execution.
        Assert.Null(BrokerTelemetry.StartToolActivity());
    }

    [Theory]
    [InlineData("read", true)]
    [InlineData("web_search", true)]
    [InlineData("tool-1.0", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("with\"quote", false)]
    [InlineData("semi;colon", false)]
    [InlineData("slash/path", false)]
    public void IsSafeDiagnosticToken_allowlists_short_tokens(string? value, bool expected)
    {
        Assert.Equal(expected, BrokerTelemetry.IsSafeDiagnosticToken(value));
    }

    [Fact]
    public void IsSafeDiagnosticToken_rejects_long_values()
    {
        Assert.False(BrokerTelemetry.IsSafeDiagnosticToken(new string('a', 65)));
        Assert.True(BrokerTelemetry.IsSafeDiagnosticToken(new string('a', 64)));
    }

    [Fact]
    public void ReadToolId_returns_short_tool_ids_only()
    {
        Assert.Equal("read", BrokerTelemetry.ReadToolId(new JsonObject { ["tool_id"] = "read" }));
        Assert.Null(BrokerTelemetry.ReadToolId(null));
        Assert.Null(BrokerTelemetry.ReadToolId(new JsonObject()));
        Assert.Null(BrokerTelemetry.ReadToolId(new JsonObject { ["tool_id"] = "../../etc/passwd" }));
    }

    [Fact]
    public void SetToolOutcome_records_bounded_outcome()
    {
        var activity = new Activity("tool.execute");
        activity.Start();
        var result = new ToolResult(
            "req", "sess", ToolResultStatuses.Failed, new JsonObject(),
            "tool_failed", "safe", 12, ToolVerificationStates.NotRequired);

        BrokerTelemetry.SetToolOutcome(activity, "read", result);

        Assert.Equal("read", activity.GetTagItem("tool.id"));
        Assert.Equal("failed", activity.GetTagItem("tool.status"));
        Assert.Equal("tool_failed", activity.GetTagItem("error.code"));
        Assert.Equal("not_required", activity.GetTagItem("verification.state"));
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        activity.Stop();
    }

    [Fact]
    public void SetToolOutcome_drops_unsafe_values_fail_closed()
    {
        var activity = new Activity("tool.execute");
        activity.Start();
        var result = new ToolResult(
            "req", "sess", "weird status!", new JsonObject(),
            "error with spaces", "safe", 12, "unknown-state");

        BrokerTelemetry.SetToolOutcome(activity, "../evil", result);

        Assert.Null(activity.GetTagItem("tool.id"));
        Assert.Null(activity.GetTagItem("tool.status"));
        Assert.Null(activity.GetTagItem("error.code"));
        Assert.Null(activity.GetTagItem("verification.state"));
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);
        activity.Stop();
    }

    [Fact]
    public void CreateProvider_builds_without_exporter_by_default()
    {
        // No OTEL_EXPORTER_OTLP_ENDPOINT in test env: provider builds with no
        // exporter, so nothing leaves the loopback host.
        using var provider = BrokerTelemetry.CreateProvider();
        Assert.NotNull(provider);
    }
}
