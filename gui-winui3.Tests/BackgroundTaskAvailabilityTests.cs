using RuApp.Services;
using RuApp.Models;
using Xunit;

namespace RuApp.Tests;

public sealed class BackgroundTaskAvailabilityTests
{
    [Fact]
    public void Missing_gateway_does_not_poll_forever_after_a_run_finishes()
    {
        var observation = new BackgroundTaskObservation();
        observation.Fail(retryable: false);
        Assert.False(observation.ShouldPoll(mainRunRunning: false));
        Assert.True(observation.ShouldPoll(mainRunRunning: true));
        Assert.Equal("Task status unavailable for this run.", observation.Status);
    }

    [Fact]
    public void Missing_gateway_preserves_known_running_task_identity_and_polling()
    {
        var observation = new BackgroundTaskObservation();
        observation.Apply(new([new AgentTaskRow("owned-task", "running", null, "test process")], "1 running"));
        observation.Fail(retryable: false);
        Assert.Equal("owned-task", Assert.Single(observation.Rows).ExecutionId);
        Assert.True(observation.ShouldPoll(mainRunRunning: false));
        Assert.Contains("last known", observation.Status);
        Assert.DoesNotContain("Retrying", observation.Status);
    }
}
