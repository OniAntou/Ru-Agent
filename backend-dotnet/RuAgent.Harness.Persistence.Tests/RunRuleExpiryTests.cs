using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Persistence.Policy;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class RunRuleExpiryTests
{
    [Fact]
    public async Task Run_rule_deleted_in_same_transaction_as_terminal_transition()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var ws = await fixture.RegisterWorkspaceAsync(1);
        var store = fixture.CreateEventStore();
        var keyStore = new FakeHarnessKeyStore();
        var policyStore = new SqlitePolicyRuleStore(fixture.Options, keyStore);

        var request = fixture.NewRun(1, 1, 1);
        await store.TryCreateRunAsync(request, CancellationToken.None);
        var runId = request.Run.RunId;
        var rule = new PolicyRule(
            new PolicyRuleId("rule_" + new string('a', 32)),
            PolicyRuleScope.Run,
            PolicyDecision.Allow,
            new PolicyMatcher(CanonicalPathPattern: "src/run.ts"),
            1,
            null);
        await policyStore.CreateAsync(rule, null, runId, HarnessStoreTestFixture.Timestamp, CancellationToken.None);
        Assert.Equal(1, await CountAsync(fixture, "SELECT COUNT(*) FROM agent_run_rules WHERE run_id = $id", runId.Value));

        // terminal transition via commit
        var current = await store.GetRunAsync(runId, CancellationToken.None);
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
            runId,
            current.Version,
            terminal,
            [new HarnessEventIntent(HarnessStoreTestFixture.Event(99), HarnessEventKind.RunStateChanged)],
            [],
            []);
        var result = await store.CommitAsync(commit, CancellationToken.None);
        Assert.Equal(HarnessCommitOutcome.Committed, result.Outcome);

        // same transaction deleted run rule
        Assert.Equal(0, await CountAsync(fixture, "SELECT COUNT(*) FROM agent_run_rules WHERE run_id = $id", runId.Value));
        // global/project would survive if existed
    }

    [Fact]
    public async Task Project_and_global_rules_survive_session_and_run_deletion()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var ws = await fixture.RegisterWorkspaceAsync(2);
        var eventStore = fixture.CreateEventStore();
        var keyStore = new FakeHarnessKeyStore();
        var policyStore = new SqlitePolicyRuleStore(fixture.Options, keyStore);

        var global = new PolicyRule(new PolicyRuleId("rule_" + new string('b', 32)), PolicyRuleScope.Global, PolicyDecision.Allow, new PolicyMatcher(ToolId: "files.read"), 1, null);
        var project = new PolicyRule(new PolicyRuleId("rule_" + new string('c', 32)), PolicyRuleScope.Project, PolicyDecision.Allow, new PolicyMatcher(ToolId: "files.write"), 2, null);
        await policyStore.CreateAsync(global, null, null, HarnessStoreTestFixture.Timestamp, CancellationToken.None);
        await policyStore.CreateAsync(project, ws.WorkspaceId, null, HarnessStoreTestFixture.Timestamp, CancellationToken.None);

        var runReq = fixture.NewRun(2, 10, 2);
        await eventStore.TryCreateRunAsync(runReq, CancellationToken.None);
        var runRule = new PolicyRule(new PolicyRuleId("rule_" + new string('d', 32)), PolicyRuleScope.Run, PolicyDecision.Allow, new PolicyMatcher(ToolId: "files.list"), 3, null);
        await policyStore.CreateAsync(runRule, null, runReq.Run.RunId, HarnessStoreTestFixture.Timestamp, CancellationToken.None);

        // delete session 10 which owns run 2
        var sessionStore = new HarnessSessionArtifactStore(fixture.Options);
        await sessionStore.CloseAndDeleteAsync(runReq.Run.SessionId.Value, HarnessStoreTestFixture.Timestamp.AddMinutes(10), CancellationToken.None);

        Assert.Equal(1, await CountAsync(fixture, "SELECT COUNT(*) FROM agent_global_rules"));
        Assert.Equal(1, await CountAsync(fixture, "SELECT COUNT(*) FROM agent_project_rules WHERE workspace_id = $id", ws.WorkspaceId.Value));
        Assert.Equal(0, await CountAsync(fixture, "SELECT COUNT(*) FROM agent_run_rules"));
        Assert.Equal(0, await CountAsync(fixture, "SELECT COUNT(*) FROM agent_runs WHERE session_id = $id", runReq.Run.SessionId.Value));
    }

    [Fact]
    public async Task Session_only_data_and_encrypted_payloads_removed_when_session_deletion_completes()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var ws = await fixture.RegisterWorkspaceAsync(3);
        var eventStore = fixture.CreateEventStore();
        var payloadBytes = Encoding.UTF8.GetBytes("secret payload");
        var payload = new HarnessPayloadIntent(
            HarnessStoreTestFixture.Payload(100),
            HarnessPayloadKind.UserPrompt,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text,
            1024,
            payloadBytes);
        var runBase = fixture.NewRun(3, 20, 3);
        var waitingRun = new AgentRun(
            runBase.Run.RunId,
            runBase.Run.SessionId,
            runBase.Run.WorkspaceId,
            new HarnessRunStatus(HarnessRunState.WaitingHistory, HistoryWaitKind.UserBeforeFirstStep),
            0,
            1,
            1,
            0,
            0,
            runBase.Run.Preset,
            runBase.Run.PolicySnapshotVersion,
            runBase.Run.PolicySnapshotSha256,
            HarnessStoreTestFixture.Timestamp,
            HarnessStoreTestFixture.Timestamp);
        var req = new NewAgentRun(
            waitingRun,
            [new HarnessEventIntent(runBase.Events[0].EventId, HarnessEventKind.RunStateChanged, payloadId: payload.PayloadId)],
            [payload],
            [new ConversationOutboxIntent(HarnessStoreTestFixture.Event(101), waitingRun.SessionId, payload.PayloadId, ConversationOutboxRole.User, HistoryWaitKind.UserBeforeFirstStep)],
            runBase.CreationIdentity);
        await eventStore.TryCreateRunAsync(req, CancellationToken.None);

        // also add a run rule
        var keyStore = new FakeHarnessKeyStore();
        var policyStore = new SqlitePolicyRuleStore(fixture.Options, keyStore);
        var runRule = new PolicyRule(new PolicyRuleId("rule_" + new string('e', 32)), PolicyRuleScope.Run, PolicyDecision.Allow, new PolicyMatcher(ToolId: "files.read"), 1, null);
        await policyStore.CreateAsync(runRule, null, waitingRun.RunId, HarnessStoreTestFixture.Timestamp, CancellationToken.None);

        Assert.Equal(1, await CountAsync(fixture, "SELECT COUNT(*) FROM agent_payloads WHERE run_id = $id", waitingRun.RunId.Value));
        Assert.Equal(1, await CountAsync(fixture, "SELECT COUNT(*) FROM agent_run_rules WHERE run_id = $id", waitingRun.RunId.Value));
        Assert.Equal(1, await CountAsync(fixture, "SELECT COUNT(*) FROM agent_session_settings WHERE session_id = $id", waitingRun.SessionId.Value));

        var sessionStore = new HarnessSessionArtifactStore(fixture.Options);
        await sessionStore.CloseAndDeleteAsync(waitingRun.SessionId.Value, HarnessStoreTestFixture.Timestamp.AddMinutes(10), CancellationToken.None);

        Assert.Equal(0, await CountAsync(fixture, "SELECT COUNT(*) FROM agent_payloads WHERE run_id = $id", waitingRun.RunId.Value));
        Assert.Equal(0, await CountAsync(fixture, "SELECT COUNT(*) FROM agent_run_rules WHERE run_id = $id", waitingRun.RunId.Value));
        Assert.Equal(0, await CountAsync(fixture, "SELECT COUNT(*) FROM agent_session_settings WHERE session_id = $id", waitingRun.SessionId.Value));
        Assert.Equal(0, await CountAsync(fixture, "SELECT COUNT(*) FROM agent_events WHERE run_id = $id", waitingRun.RunId.Value));
    }

    [Fact]
    public async Task Nonterminal_commit_does_not_delete_run_rules()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(4);
        var eventStore = fixture.CreateEventStore();
        var keyStore = new FakeHarnessKeyStore();
        var policyStore = new SqlitePolicyRuleStore(fixture.Options, keyStore);
        var req = fixture.NewRun(4, 30, 4);
        await eventStore.TryCreateRunAsync(req, CancellationToken.None);
        var rule = new PolicyRule(new PolicyRuleId("rule_" + new string('f', 32)), PolicyRuleScope.Run, PolicyDecision.Allow, new PolicyMatcher(ToolId: "files.read"), 1, null);
        await policyStore.CreateAsync(rule, null, req.Run.RunId, HarnessStoreTestFixture.Timestamp, CancellationToken.None);

        var current = await eventStore.GetRunAsync(req.Run.RunId, CancellationToken.None);
        var next = new AgentRun(
            current!.RunId,
            current.SessionId,
            current.WorkspaceId,
            new HarnessRunStatus(HarnessRunState.Running),
            current.Version + 1,
            current.LastSequence + 1,
            current.BudgetWindow,
            current.ModelStepsInWindow,
            current.ToolCallsInWindow,
            current.Preset,
            current.PolicySnapshotVersion,
            current.PolicySnapshotSha256,
            current.CreatedAt,
            HarnessStoreTestFixture.Timestamp.AddMinutes(1));
        var commit = new HarnessCommit(
            req.Run.RunId,
            current.Version,
            next,
            [new HarnessEventIntent(HarnessStoreTestFixture.Event(200), HarnessEventKind.RunStateChanged)],
            [],
            []);
        await eventStore.CommitAsync(commit, CancellationToken.None);

        Assert.Equal(1, await CountAsync(fixture, "SELECT COUNT(*) FROM agent_run_rules WHERE run_id = $id", req.Run.RunId.Value));
    }

    private static async Task<long> CountAsync(HarnessStoreTestFixture fixture, string sql, string? param = null)
    {
        await using var conn = fixture.CreateRawConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (param is not null)
        {
            cmd.Parameters.AddWithValue("$id", param);
        }
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }
}
