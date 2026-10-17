using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class HarnessConcurrencyTests
{
    [Fact]
    public async Task Same_idempotency_key_and_request_returns_the_original_run()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var store = fixture.CreateEventStore();
        var first = fixture.NewRun(1, 1, 1, 'a', 'b');
        var replay = fixture.NewRun(2, 1, 1, 'a', 'b');

        var created = await store.TryCreateRunAsync(first, CancellationToken.None);
        var repeated = await store.TryCreateRunAsync(replay, CancellationToken.None);

        Assert.Equal(CreateRunOutcome.Created, created.Outcome);
        Assert.Equal(CreateRunOutcome.AlreadyExists, repeated.Outcome);
        Assert.Equal(first.Run.RunId, repeated.Run.RunId);
        Assert.Null(repeated.ErrorCode);
    }

    [Fact]
    public async Task Reusing_an_idempotency_key_for_a_different_request_conflicts()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var store = fixture.CreateEventStore();
        var first = fixture.NewRun(1, 1, 1, 'a', 'b');
        var conflicting = fixture.NewRun(2, 1, 1, 'a', 'c');
        await store.TryCreateRunAsync(first, CancellationToken.None);

        var result = await store.TryCreateRunAsync(conflicting, CancellationToken.None);

        Assert.Equal(CreateRunOutcome.Conflict, result.Outcome);
        Assert.Equal("idempotency_conflict", result.ErrorCode);
        Assert.Equal(first.Run.RunId, result.Run.RunId);
    }

    [Fact]
    public async Task Different_sessions_in_one_workspace_are_admitted_atomically_without_configuration()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var firstStore = fixture.CreateEventStore();
        var secondStore = fixture.CreateEventStore();
        var first = fixture.NewRun(1, 1, 1, 'a', 'b');
        var second = fixture.NewRun(2, 2, 1, 'c', 'd');

        var results = await Task.WhenAll(
            firstStore.TryCreateRunAsync(first, CancellationToken.None),
            secondStore.TryCreateRunAsync(second, CancellationToken.None));

        Assert.All(results, result => Assert.Equal(CreateRunOutcome.Created, result.Outcome));
        Assert.Equal(2L, await CountRunsAsync(fixture));
    }

    [Fact]
    public async Task One_session_cannot_have_two_active_runs()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        await fixture.RegisterWorkspaceAsync(2);
        var store = fixture.CreateEventStore();
        await store.TryCreateRunAsync(fixture.NewRun(1, 1, 1, 'a', 'b'), CancellationToken.None);

        var result = await store.TryCreateRunAsync(
            fixture.NewRun(2, 1, 2, 'c', 'd'),
            CancellationToken.None);

        Assert.Equal(CreateRunOutcome.Conflict, result.Outcome);
        Assert.Equal("session_busy", result.ErrorCode);
        Assert.Equal(1L, await CountRunsAsync(fixture));
    }

    [Fact]
    public async Task Terminal_run_releases_the_session_and_workspace_indexes()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var store = fixture.CreateEventStore();
        var first = fixture.NewRun(1, 1, 1, 'a', 'b');
        await store.TryCreateRunAsync(first, CancellationToken.None);
        var cancelled = HarnessEventStoreTests.CopyRun(
            first.Run,
            new HarnessRunStatus(HarnessRunState.Cancelled),
            version: 1,
            lastSequence: 2,
            terminalAt: first.Run.UpdatedAt.AddSeconds(1));
        await store.CommitAsync(
            new HarnessCommit(
                first.Run.RunId,
                0,
                cancelled,
                [new HarnessEventIntent(HarnessStoreTestFixture.Event(30), HarnessEventKind.RunStateChanged)],
                [],
                []),
            CancellationToken.None);

        var next = await store.TryCreateRunAsync(
            fixture.NewRun(2, 1, 1, 'c', 'd'),
            CancellationToken.None);

        Assert.Equal(CreateRunOutcome.Created, next.Outcome);
        Assert.Equal(2L, await CountRunsAsync(fixture));
    }

    private static async Task<long> CountRunsAsync(HarnessStoreTestFixture fixture)
    {
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM agent_runs";
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }
}
