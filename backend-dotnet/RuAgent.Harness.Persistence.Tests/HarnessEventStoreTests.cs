using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class HarnessEventStoreTests
{
    [Fact]
    public async Task Create_round_trips_projection_and_safe_events()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var store = fixture.CreateEventStore();
        var request = fixture.NewRun(1, 1, 1);

        var created = await store.TryCreateRunAsync(request, CancellationToken.None);
        var loaded = await store.GetRunAsync(request.Run.RunId, CancellationToken.None);
        var events = await ReadAllAsync(store, request.Run.RunId);

        Assert.Equal(CreateRunOutcome.Created, created.Outcome);
        Assert.Equal(request.Run, created.Run);
        Assert.Equal(request.Run, loaded);
        var envelope = Assert.Single(events);
        Assert.Equal(1, envelope.ProtocolVersion);
        Assert.Equal(1, envelope.Event.Sequence);
        Assert.Equal("run.state_changed", envelope.Event.Kind);
        Assert.Equal(request.Events[0].EventId, envelope.Event.EventId);
    }

    [Fact]
    public async Task Commit_appends_after_the_last_durable_sequence()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var store = fixture.CreateEventStore();
        var request = fixture.NewRun(1, 1, 1);
        await store.TryCreateRunAsync(request, CancellationToken.None);
        var next = CopyRun(
            request.Run,
            new HarnessRunStatus(HarnessRunState.Running),
            version: 1,
            lastSequence: 2);
        var commit = new HarnessCommit(
            request.Run.RunId,
            expectedVersion: 0,
            next,
            [new HarnessEventIntent(HarnessStoreTestFixture.Event(20), HarnessEventKind.RunStateChanged)],
            [],
            []);

        var result = await store.CommitAsync(commit, CancellationToken.None);
        var events = await ReadAllAsync(store, request.Run.RunId);

        Assert.Equal(HarnessCommitOutcome.Committed, result.Outcome);
        Assert.Equal(next, result.Run);
        Assert.Equal([1L, 2L], events.Select(item => item.Event.Sequence));
    }

    [Fact]
    public async Task Commit_rejects_outbox_referencing_a_payload_that_was_never_stored()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var store = fixture.CreateEventStore();
        var request = fixture.NewRun(
            1,
            1,
            1,
            status: new HarnessRunStatus(HarnessRunState.WaitingWorkspaceTrust));
        await store.TryCreateRunAsync(request, CancellationToken.None);

        var missingPayloadId = new PayloadId("payload_22222222222222222222222222222222");
        var next = CopyRun(
            request.Run,
            new HarnessRunStatus(
                HarnessRunState.WaitingHistory,
                HistoryWaitKind.UserBeforeFirstStep),
            version: 1,
            lastSequence: 2);
        var commit = new HarnessCommit(
            request.Run.RunId,
            expectedVersion: 0,
            next,
            [new HarnessEventIntent(HarnessStoreTestFixture.Event(20), HarnessEventKind.RunStateChanged)],
            [],
            [
                new ConversationOutboxIntent(
                    HarnessStoreTestFixture.Event(21),
                    request.Run.SessionId,
                    missingPayloadId,
                    ConversationOutboxRole.User,
                    HistoryWaitKind.UserBeforeFirstStep),
            ]);

        var error = Assert.Throws<HarnessStoreException>(() =>
            store.CommitAsync(commit, CancellationToken.None).GetAwaiter().GetResult());
        Assert.Equal("outbox_payload_missing", error.Code);
    }

    private static async Task<List<HarnessEventEnvelope>> ReadAllAsync(
        SqliteHarnessEventStore store,
        RunId runId)
    {
        var events = new List<HarnessEventEnvelope>();
        await foreach (var envelope in store.ReadEventsAsync(runId, 0, CancellationToken.None))
        {
            events.Add(envelope);
        }

        return events;
    }

    internal static AgentRun CopyRun(
        AgentRun current,
        HarnessRunStatus status,
        long version,
        long lastSequence,
        DateTimeOffset? terminalAt = null,
        string? terminalErrorCode = null) =>
        new(
            current.RunId,
            current.SessionId,
            current.WorkspaceId,
            status,
            version,
            lastSequence,
            current.BudgetWindow,
            current.ModelStepsInWindow,
            current.ToolCallsInWindow,
            current.Preset,
            current.PolicySnapshotVersion,
            current.PolicySnapshotSha256,
            current.CreatedAt,
            current.UpdatedAt.AddSeconds(version),
            terminalAt,
            terminalErrorCode);
}
