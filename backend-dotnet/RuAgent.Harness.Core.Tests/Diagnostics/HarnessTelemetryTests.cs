using System.Diagnostics;
using RuAgent.Harness.Core.Diagnostics;
using Xunit;

namespace RuAgent.Harness.Core.Tests.Diagnostics;

public sealed class HarnessTelemetryTests
{
    [Fact]
    public void StartModelStepActivity_returns_null_without_listener()
    {
        // No ActivityListener is registered anywhere in this test process,
        // so the source must stay silent: telemetry never perturbs the run.
        Assert.Null(HarnessTelemetry.StartModelStepActivity());
    }

    [Theory]
    [InlineData("succeeded", ActivityStatusCode.Ok)]
    [InlineData("cancelled", ActivityStatusCode.Error)]
    [InlineData("model_failed", ActivityStatusCode.Error)]
    [InlineData("prompt_contract_invalid", ActivityStatusCode.Error)]
    public void SetStepOutcome_records_bounded_codes(string outcome, ActivityStatusCode status)
    {
        var activity = new Activity("agent.model_step");
        activity.Start();

        HarnessTelemetry.SetStepOutcome(activity, outcome);

        Assert.Equal(outcome, activity.GetTagItem("step.outcome"));
        Assert.Equal(status, activity.Status);
        activity.Stop();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hacked\";drop")]
    [InlineData("run_id:run_abc")]
    [InlineData("prompt text with spaces")]
    public void SetStepOutcome_drops_unknown_codes_fail_closed(string? outcome)
    {
        var activity = new Activity("agent.model_step");
        activity.Start();

        HarnessTelemetry.SetStepOutcome(activity, outcome);

        Assert.Null(activity.GetTagItem("step.outcome"));
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);
        activity.Stop();
    }

    [Fact]
    public void SetStepOutcome_ignores_null_activity()
    {
        var exception = Record.Exception(() => HarnessTelemetry.SetStepOutcome(null, "succeeded"));
        Assert.Null(exception);
    }
}
