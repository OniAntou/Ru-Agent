using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class AgentRunDriveBackoffTests
{
    private const string RunId = "run_11111111111111111111111111111111";

    [Fact]
    public void Repeated_failures_double_the_delay_up_to_the_cap()
    {
        var now = DateTimeOffset.Parse("2026-11-18T00:00:00Z");
        var backoff = new AgentRunDriveBackoff(TimeSpan.FromMilliseconds(250), () => now);

        Assert.True(backoff.IsDue(RunId));
        // A first failure retries on the next pass; spacing starts with a repeat.
        Assert.Equal(TimeSpan.Zero, backoff.RecordFailure(RunId));
        Assert.True(backoff.IsDue(RunId));
        Assert.Equal(TimeSpan.FromMilliseconds(250), backoff.RecordFailure(RunId));
        Assert.False(backoff.IsDue(RunId));
        Assert.Equal(TimeSpan.FromMilliseconds(500), backoff.RecordFailure(RunId));
        Assert.Equal(TimeSpan.FromMilliseconds(1000), backoff.RecordFailure(RunId));

        for (var index = 0; index < 40; index++) backoff.RecordFailure(RunId);
        Assert.Equal(AgentRunDriveBackoff.MaximumDelay, backoff.RecordFailure(RunId));

        now += AgentRunDriveBackoff.MaximumDelay;
        Assert.True(backoff.IsDue(RunId));
    }

    [Fact]
    public void A_clean_pass_clears_the_backoff_but_a_failing_pass_keeps_it()
    {
        var now = DateTimeOffset.Parse("2026-11-18T00:00:00Z");
        var backoff = new AgentRunDriveBackoff(TimeSpan.FromMilliseconds(250), () => now);
        backoff.RecordFailure(RunId);
        backoff.RecordFailure(RunId);
        now += TimeSpan.FromSeconds(1);

        var before = backoff.Failures(RunId);
        backoff.RecordFailure(RunId);
        backoff.RecordPass(RunId, before);
        Assert.Equal(3, backoff.Failures(RunId));
        Assert.False(backoff.IsDue(RunId));

        now += TimeSpan.FromSeconds(1);
        backoff.RecordPass(RunId, backoff.Failures(RunId));
        Assert.Equal(0, backoff.Failures(RunId));
        Assert.True(backoff.IsDue(RunId));
    }

    [Fact]
    public void Forget_drops_a_released_run()
    {
        var backoff = new AgentRunDriveBackoff(TimeSpan.FromMilliseconds(250));
        backoff.RecordFailure(RunId);
        backoff.Forget(RunId);

        Assert.True(backoff.IsDue(RunId));
        Assert.Equal(0, backoff.Failures(RunId));
    }
}
