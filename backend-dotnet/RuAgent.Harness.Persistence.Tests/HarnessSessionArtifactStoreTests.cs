using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class HarnessSessionArtifactStoreTests
{
    [Fact]
    public async Task Close_and_delete_removes_active_run_payload_events_outbox_and_setting()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var bytes = Encoding.UTF8.GetBytes("session deletion prompt");
        var payload = new HarnessPayloadIntent(
            HarnessStoreTestFixture.Payload(170),
            HarnessPayloadKind.UserPrompt,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text,
            1024,
            bytes);
        var request = fixture.NewRun(
            1,
            1,
            1,
            payloads: [payload],
            outbox:
            [
                new ConversationOutboxIntent(
                    HarnessStoreTestFixture.Event(171),
                    HarnessStoreTestFixture.Session(1),
                    payload.PayloadId,
                    ConversationOutboxRole.User,
                    HistoryWaitKind.UserBeforeFirstStep),
            ],
            status: new HarnessRunStatus(
                HarnessRunState.WaitingHistory,
                HistoryWaitKind.UserBeforeFirstStep));
        await fixture.CreateEventStore().TryCreateRunAsync(request, CancellationToken.None);
        var store = new HarnessSessionArtifactStore(fixture.Options);

        var result = await store.CloseAndDeleteAsync(
            request.Run.SessionId.Value,
            HarnessStoreTestFixture.Timestamp.AddMinutes(1),
            CancellationToken.None);

        Assert.Equal(1, result.ClosedActiveRuns);
        Assert.Equal(1, result.DeletedRuns);
        Assert.Equal(0L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM agent_runs"));
        Assert.Equal(0L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM agent_events"));
        Assert.Equal(0L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM agent_payloads"));
        Assert.Equal(0L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM agent_conversation_outbox"));
        Assert.Equal(0L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM agent_session_settings"));
    }

    [Fact]
    public async Task Run_creation_after_the_session_tombstone_is_refused_with_a_bounded_code()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var request = fixture.NewRun(1, 1, 1);
        var store = new HarnessSessionArtifactStore(fixture.Options);

        await store.MarkSessionDeletedAsync(
            request.Run.SessionId.Value,
            HarnessStoreTestFixture.Timestamp.AddMinutes(1),
            CancellationToken.None);

        var result = await fixture.CreateEventStore()
            .TryCreateRunAsync(request, CancellationToken.None);

        Assert.Equal(CreateRunOutcome.Conflict, result.Outcome);
        Assert.Equal("session_deleted", result.ErrorCode);
        Assert.Equal(0L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM agent_runs"));
        Assert.Equal(0L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM agent_session_settings"));
    }

    [Fact]
    public async Task Non_terminal_run_listing_is_session_scoped_and_skips_terminal_runs()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        await fixture.RegisterWorkspaceAsync(2);
        var store = fixture.CreateEventStore();

        var live = fixture.NewRun(1, 1, 1, status: new HarnessRunStatus(HarnessRunState.WaitingUser));
        await store.TryCreateRunAsync(live, CancellationToken.None);
        var finished = fixture.NewRun(2, 2, 2);
        await store.TryCreateRunAsync(finished, CancellationToken.None);

        var current = await store.GetRunAsync(finished.Run.RunId, CancellationToken.None);
        var terminal = new AgentRun(
            current!.RunId,
            current.SessionId,
            current.WorkspaceId,
            new HarnessRunStatus(HarnessRunState.Completed),
            current.Version + 1,
            current.LastSequence + 1,
            current.BudgetWindow,
            current.ModelStepsInWindow,
            current.ToolCallsInWindow,
            current.Preset,
            current.PolicySnapshotVersion,
            current.PolicySnapshotSha256,
            current.CreatedAt,
            HarnessStoreTestFixture.Timestamp.AddMinutes(5),
            terminalAt: HarnessStoreTestFixture.Timestamp.AddMinutes(5));
        var commit = new HarnessCommit(
            finished.Run.RunId,
            current.Version,
            terminal,
            [new HarnessEventIntent(HarnessStoreTestFixture.Event(200), HarnessEventKind.RunStateChanged)],
            [],
            []);
        Assert.Equal(
            HarnessCommitOutcome.Committed,
            (await store.CommitAsync(commit, CancellationToken.None)).Outcome);

        var artifacts = new HarnessSessionArtifactStore(fixture.Options);
        Assert.Equal(
            new[] { live.Run.RunId.Value },
            await artifacts.ListNonTerminalRunIdsAsync(live.Run.SessionId.Value, CancellationToken.None));
        Assert.Empty(
            await artifacts.ListNonTerminalRunIdsAsync(finished.Run.SessionId.Value, CancellationToken.None));
    }

    private static async Task<long> ScalarAsync(
        HarnessStoreTestFixture fixture,
        string commandText)
    {
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }
}
