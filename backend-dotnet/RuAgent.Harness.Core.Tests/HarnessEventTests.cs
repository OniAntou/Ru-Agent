using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed class HarnessEventTests
{
    [Fact]
    public void Durable_event_exposes_only_safe_envelope_fields()
    {
        var occurredAt = new DateTimeOffset(2026, 8, 24, 8, 0, 0, TimeSpan.Zero);
        var payloadId = new PayloadId("payload_11111111111111111111111111111111");
        var harnessEvent = new HarnessEvent(
            new EventId("evt_11111111111111111111111111111111"),
            new RunId("run_11111111111111111111111111111111"),
            1,
            "run.created",
            occurredAt,
            payloadId);

        Assert.Equal(1, harnessEvent.Sequence);
        Assert.Equal("run.created", harnessEvent.Kind);
        Assert.Equal(payloadId, harnessEvent.PayloadId);
        Assert.Equal(
            new[]
            {
                "ApprovalId",
                "EventId",
                "ExecutionId",
                "Kind",
                "ModelStepId",
                "OccurredAt",
                "PayloadId",
                "RunId",
                "SafeCode",
                "SafeCount",
                "Sequence",
                "ToolCallId",
            },
            typeof(HarnessEvent)
                .GetProperties()
                .Select(property => property.Name)
                .OrderBy(name => name));
        Assert.All(
            typeof(HarnessEvent).GetProperties(),
            property => Assert.False(property.CanWrite, property.Name));
    }

    [Theory]
    [InlineData(0, "run.created")]
    [InlineData(-1, "run.created")]
    [InlineData(1, "")]
    [InlineData(1, "Run.Created")]
    [InlineData(1, "contains private text")]
    public void Invalid_sequence_or_kind_is_rejected(long sequence, string kind)
    {
        Assert.ThrowsAny<ArgumentException>(() => new HarnessEvent(
            new EventId("evt_11111111111111111111111111111111"),
            new RunId("run_11111111111111111111111111111111"),
            sequence,
            kind,
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Default_identifiers_are_rejected_at_the_event_boundary()
    {
        Assert.Throws<InvalidOperationException>(() => new HarnessEvent(
            default,
            new RunId("run_11111111111111111111111111111111"),
            1,
            "run.created",
            DateTimeOffset.UtcNow));
    }
}
