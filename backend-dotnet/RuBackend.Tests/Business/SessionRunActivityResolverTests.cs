using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence;
using RuBackend.Business.Sessions;
using RuBackend.Tests.Harness;
using Xunit;

namespace RuBackend.Tests.Business;

public sealed class SessionRunActivityResolverTests
{
    [Fact]
    public async Task Activity_is_scoped_to_requested_sessions_and_names_the_wait_of_nonterminal_states()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var seeded = await fixture.SeedRunAsync(new(HarnessRunState.Running));
        string session = seeded.Run.SessionId.Value;
        foreach (HarnessRunState state in Enum.GetValues<HarnessRunState>())
        {
            await using var connection = await fixture.Options.OpenConnectionAsync(default);
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE agent_runs SET state = $state, terminal_at_utc = $terminal, history_wait_kind = $history";
            update.Parameters.AddWithValue("$state", (int)state);
            update.Parameters.AddWithValue("$terminal", state.IsTerminal() ? DateTimeOffset.UtcNow.ToString("O") : DBNull.Value);
            update.Parameters.AddWithValue("$history", state == HarnessRunState.WaitingHistory ? (int)HistoryWaitKind.UserBeforeFirstStep : DBNull.Value);
            await update.ExecuteNonQueryAsync();
            var activity = await SessionRunActivityResolver.ResolveAsync([session, "unrelated"], fixture.Options, default);
            SessionRunActivity? expected = state switch
            {
                HarnessRunState.Created or HarnessRunState.WaitingHistory or HarnessRunState.Running
                    or HarnessRunState.WaitingSubagents => SessionRunActivity.Working,
                HarnessRunState.WaitingApproval => SessionRunActivity.WaitingApproval,
                HarnessRunState.WaitingUser => SessionRunActivity.WaitingUser,
                HarnessRunState.WaitingWorkspaceTrust => SessionRunActivity.WaitingWorkspaceTrust,
                _ => null,
            };
            Assert.Equal(expected, activity.TryGetValue(session, out var actual) ? actual : null);
            Assert.False(activity.ContainsKey("unrelated"));
        }
        Assert.Empty(await SessionRunActivityResolver.ResolveAsync(["not-requested"], fixture.Options, default));
        Assert.Empty(await SessionRunActivityResolver.ResolveAsync([], fixture.Options, default));
    }

    [Fact]
    public async Task A_user_wait_parked_on_an_input_request_reads_as_a_question()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var seeded = await fixture.SeedRunAsync(new(HarnessRunState.Running));
        string session = seeded.Run.SessionId.Value;
        await using (var connection = await fixture.Options.OpenConnectionAsync(default))
        {
            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE agent_runs SET state = $state;
                INSERT INTO agent_events(event_id, run_id, sequence, kind, occurred_at_utc)
                VALUES ('evt_question', $run, 1000, 'input.requested', $now);
                """;
            update.Parameters.AddWithValue("$state", (int)HarnessRunState.WaitingUser);
            update.Parameters.AddWithValue("$run", seeded.Run.RunId.Value);
            update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await update.ExecuteNonQueryAsync();
        }

        var activity = await SessionRunActivityResolver.ResolveAsync([session], fixture.Options, default);

        Assert.Equal(SessionRunActivity.WaitingAnswer, activity[session]);
    }

    [Fact]
    public void Only_a_wait_on_the_user_has_a_wire_value()
    {
        Assert.Null(SessionRunActivityResolver.WaitWireValue(SessionRunActivity.Working));
        Assert.Equal("approval", SessionRunActivityResolver.WaitWireValue(SessionRunActivity.WaitingApproval));
        Assert.Equal("question", SessionRunActivityResolver.WaitWireValue(SessionRunActivity.WaitingAnswer));
        Assert.Equal("user", SessionRunActivityResolver.WaitWireValue(SessionRunActivity.WaitingUser));
        Assert.Equal("workspace_trust", SessionRunActivityResolver.WaitWireValue(SessionRunActivity.WaitingWorkspaceTrust));
    }

    [Fact]
    public void The_query_has_the_same_bound_as_the_session_listing()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionRunActivityResolver.BuildSql(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionRunActivityResolver.BuildSql(SessionWorkspaceResolver.MaximumRequestedSessions + 1));
    }

    [Fact]
    public async Task An_unreadable_harness_database_yields_no_activity_instead_of_failing()
    {
        // The session list must still render when the harness database is missing
        // or locked: the activity marks are decorative, the sessions are not.
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var missing = new HarnessSqliteOptions(
            Path.Combine(fixture.Options.DatabasePath + ".missing", "harness.db"));

        Assert.Empty(await SessionRunActivityResolver.ResolveAsync(["sess_any"], missing, default));
    }
}
