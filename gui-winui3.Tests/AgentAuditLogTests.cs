using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentAuditLogTests
{
    private static AgentEventModel Evt(long seq) => new(
        EventId: "e" + seq,
        RunId: "run_x",
        SessionId: "s",
        EventType: "run.state_changed",
        OccurredAt: "2026",
        Status: "running",
        SafeSummary: "step " + seq,
        PayloadRef: null,
        ApprovalId: null,
        Sequence: seq,
        IsTransient: false,
        TransientSequence: null,
        TransientData: null);

    [Fact]
    public void Record_assigns_monotonic_sequence_and_stays_bounded()
    {
        var log = new AgentAuditLog(capacity: 3);
        log.Record(Evt(1));
        log.Record(Evt(2));
        log.Record(Evt(3));
        log.Record(Evt(4));

        Assert.Equal(4, log.LastSequence);
        Assert.Equal(3, log.Entries.Count);
        Assert.Equal("step 4", log.Entries[^1].SafeSummary);
        Assert.Equal("step 2", log.Entries[0].SafeSummary);
    }

    [Fact]
    public void Clear_resets_sequence_and_entries()
    {
        var log = new AgentAuditLog();
        log.Record(Evt(1));
        log.Clear();

        Assert.Equal(0, log.LastSequence);
        Assert.Empty(log.Entries);

        log.Record(Evt(9));
        Assert.Equal(1, log.LastSequence);
    }

    [Fact]
    public void Record_ignores_null()
    {
        var log = new AgentAuditLog();
        log.Record(null!);

        Assert.Equal(0, log.LastSequence);
        Assert.Empty(log.Entries);
    }
}
