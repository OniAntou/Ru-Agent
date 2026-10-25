extern alias Broker;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BrokerExecutionRuntime = Broker::RuAgent.Broker.Runtime.BrokerExecutionRuntime;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Workspace;
using Xunit;

using RuAgent.Protocol.Harness;
namespace RuBackend.Tests.Harness;

/// <summary>
/// Factory lifecycle coverage for the in-process seam, mirroring the wire
/// factory's reuse/rebind tests: a same-scope Create reuses the gateway and its
/// approval state, a changed snapshot or session rotates the lease (with the
/// session id travelling with it), and a stale generation is rejected before
/// execute.
/// </summary>
public sealed class InProcessToolGatewayFactoryTests : IDisposable
{
    private readonly InProcessExecutionFixture _fixture = new();
    private readonly string _externalPath;

    public InProcessToolGatewayFactoryTests()
    {
        _externalPath = Path.Combine(
            Path.GetTempPath(),
            "ru-inprocess-lease-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(_externalPath, "lease-state");
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_externalPath);
        }
        catch (IOException)
        {
        }

        _fixture.Dispose();
    }

    [Fact]
    public async Task Create_reuses_the_gateway_and_rotates_the_lease_when_the_snapshot_changes()
    {
        var runId = new RunId("run_" + new string('5', 32));
        var snapshot = Snapshot(1);
        var first = await Create(runId, snapshot, "session-inprocess-reuse");

        var proposal = ExternalReadProposal(runId);
        var ask = await first.EvaluateAsync(proposal, CancellationToken.None);
        Assert.Equal(PolicyDecision.Ask, ask.Decision);
        var (authorized, _) = await _fixture.Factory.RespondApprovalAsync(
            runId.Value,
            ask.ApprovalId!.Value.Value,
            "allow_once",
            CancellationToken.None);
        Assert.True(authorized);

        // Same scope: the same gateway comes back and its cached approval
        // state survives, so the resumed proposal allows without a new ask.
        var replacement = await Create(runId, snapshot, "session-inprocess-reuse");
        Assert.Same(first, replacement);
        var resumed = await replacement.EvaluateAsync(proposal, CancellationToken.None);
        Assert.Equal(PolicyDecision.Allow, resumed.Decision);

        // Changed snapshot: same gateway, rotated lease and generation, and
        // the old cached approval/token are gone (the new policy never saw
        // the intent).
        var generationBeforeRebind = first.Lease.Generation;
        var changed = await Create(runId, Snapshot(2), "session-inprocess-reuse");
        Assert.Same(first, changed);
        Assert.True(changed.Lease.Generation > generationBeforeRebind);
        Assert.Equal(2, changed.Lease.PolicyVersion);
        var reevaluated = await changed.EvaluateAsync(proposal, CancellationToken.None);
        Assert.Equal(PolicyDecision.Ask, reevaluated.Decision);

        await _fixture.Factory.CloseRunAsync(runId.Value);
    }

    [Fact]
    public async Task Create_rotates_the_lease_when_the_session_changes()
    {
        // Regression for the rebind session-id bug: a run can legitimately
        // rebind onto a different session (a call site that omits
        // agentSessionId falls back to "session_"+runId). Every runtime call
        // after the rebind must address the new session, or the authority
        // answers run_not_open.
        var runId = new RunId("run_" + new string('6', 32));
        var snapshot = Snapshot(1);
        File.WriteAllText(Path.Combine(_fixture.WorkspaceRoot, "notes.txt"), "in-process");
        var first = await Create(runId, snapshot, "session-inprocess-a");
        Assert.Equal("session-inprocess-a", first.Lease.SessionId);

        var proposal = WorkspaceReadProposal(runId);
        var initial = await first.EvaluateAsync(proposal, CancellationToken.None);
        Assert.Equal(PolicyDecision.Allow, initial.Decision);

        var rebound = await Create(runId, snapshot, agentSessionId: null);
        Assert.Same(first, rebound);
        Assert.Equal("session_" + runId.Value, rebound.Lease.SessionId);

        var afterRebind = await rebound.EvaluateAsync(proposal, CancellationToken.None);
        Assert.Equal(PolicyDecision.Allow, afterRebind.Decision);

        await _fixture.Factory.CloseRunAsync(runId.Value);
    }

    [Fact]
    public async Task Rebind_between_evaluate_and_prepare_rejects_the_stale_generation()
    {
        var runId = new RunId("run_" + new string('9', 32));
        var snapshot = Snapshot(1);
        var writePath = Path.Combine(_fixture.WorkspaceRoot, "toctou.txt");
        File.WriteAllText(writePath, "before");
        var gateway = await Create(runId, snapshot, "session-inprocess-generation");

        var proposal = new ToolProposal(
            runId,
            new ModelStepId("step_" + new string('0', 32)),
            new ToolCallId("call_" + new string('0', 32)),
            "provider_call_1",
            modelOrder: 1,
            "write_file",
            "1.0.0",
            JsonSerializer.SerializeToElement(new JsonObject
            {
                ["path"] = writePath,
                ["content"] = "after",
                ["overwrite"] = true,
            }));
        var allow = await gateway.EvaluateAsync(proposal, CancellationToken.None);
        Assert.Equal(PolicyDecision.Allow, allow.Decision);
        Assert.Equal(allow.GatewayGeneration, gateway.Lease.Generation);

        var rebound = await Create(runId, Snapshot(2), "session-inprocess-generation");
        Assert.Same(gateway, rebound);
        Assert.True(rebound.Lease.Generation > allow.GatewayGeneration);

        var error = await Assert.ThrowsAsync<ToolTransportException>(() => rebound.PrepareAsync(
            new ApprovedToolExecution(
                proposal,
                new ExecutionId("exec_" + new string('9', 32)),
                allow.CanonicalIntentToken!,
                allow.GatewayGeneration),
            CancellationToken.None));
        Assert.Equal("broker_lease_generation_changed", error.Message);
        Assert.Equal("before", await File.ReadAllTextAsync(writePath));

        await _fixture.Factory.CloseRunAsync(runId.Value);
    }

    [Fact]
    public void IsConfigured_is_true_for_the_injected_runtime()
    {
        Assert.True(_fixture.Factory.IsConfigured);
    }

    [Fact]
    public async Task CloseSessionAsync_closes_the_runtime_session_and_drops_its_leases()
    {
        var runId = new RunId("run_" + new string('c', 32));
        const string sessionId = "session-inprocess-close";
        await Create(runId, Snapshot(1), sessionId);
        Assert.True(_fixture.Session.Owns(sessionId));
        Assert.True(_fixture.Session.TryGetRunLease(runId.Value, out _));

        await _fixture.Factory.CloseSessionAsync(sessionId, CancellationToken.None);

        // The authority session is closed: its run leases are gone and it no
        // longer owns the session (the runtime close also invokes the
        // execution service's session teardown, which reaps its child tasks).
        Assert.False(_fixture.Session.Owns(sessionId));
        Assert.False(_fixture.Session.TryGetRunLease(runId.Value, out _));
        var tasks = await _fixture.Factory.ListSessionTasksAsync(runId.Value, null, CancellationToken.None);
        Assert.Equal("broker_gateway_unavailable", tasks["error_code"]!.GetValue<string>());
    }

    [Fact]
    public async Task CloseSessionAsync_without_authority_state_does_not_compose_the_runtime()
    {
        // A delete for a session that never ran a tool must stay lazy: the
        // factory skips the runtime call instead of composing the authority.
        var composed = false;
        var factory = new global::RuBackend.Harness.Broker.InProcessToolGatewayFactory(
            new Lazy<BrokerExecutionRuntime>(() =>
            {
                composed = true;
                return _fixture.Runtime;
            }));
        try
        {
            await factory.CloseSessionAsync("session-inprocess-never-opened", CancellationToken.None);
            Assert.False(composed);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [Fact]
    public async Task CloseRun_releases_the_authority_run_lease()
    {
        var runId = new RunId("run_" + new string('d', 32));
        const string sessionId = "session-inprocess-close-run";
        await Create(runId, Snapshot(1), sessionId);
        Assert.True(_fixture.Session.TryGetRunLease(runId.Value, out _));

        await _fixture.Factory.CloseRunAsync(runId.Value);

        // The run teardown released the authority lease; the session itself
        // stays open for any other run of the chat.
        Assert.False(_fixture.Session.TryGetRunLease(runId.Value, out _));
        Assert.True(_fixture.Session.Owns(sessionId));
    }

    [Fact]
    public async Task Create_throws_when_snapshot_hash_is_missing()
    {
        var runId = new RunId("run_" + new string('7', 32));
        var snapshot = new PolicySnapshot(
            1,
            AgentPermissionPreset.Standard,
            new WorkspaceIdentity(
                new WorkspaceId("ws_" + new string('1', 32)),
                "VOL-INPROCESS",
                "fid-inprocess",
                new string('a', 64)),
            [],
            DateTimeOffset.UtcNow,
            string.Empty);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _fixture.Factory.CreateAsync(_fixture.WorkspaceRoot, snapshot, runId, "session-inprocess-hash", CancellationToken.None));
    }

    [Fact]
    public async Task Rebind_drops_a_prepared_execution_from_the_old_generation()
    {
        var runId = new RunId("run_" + new string('8', 32));
        var writePath = Path.Combine(_fixture.WorkspaceRoot, "racing.txt");
        File.WriteAllText(writePath, "before");
        var gateway = await Create(runId, Snapshot(1), "session-inprocess-racing");
        var proposal = new ToolProposal(
            runId,
            new ModelStepId("step_" + new string('0', 32)),
            new ToolCallId("call_" + new string('0', 32)),
            "provider_call_1",
            modelOrder: 1,
            "write_file",
            "1.0.0",
            JsonSerializer.SerializeToElement(new JsonObject
            {
                ["path"] = writePath,
                ["content"] = "after",
                ["overwrite"] = true,
            }));
        var allow = await gateway.EvaluateAsync(proposal, CancellationToken.None);
        var prepared = await gateway.PrepareAsync(
            new ApprovedToolExecution(
                proposal,
                new ExecutionId("exec_" + new string('8', 32)),
                allow.CanonicalIntentToken!,
                allow.GatewayGeneration),
            CancellationToken.None);

        // A rebind rotates the generation and drops the prepared execution, so
        // executing the stale preparation must fail closed without touching
        // the file.
        var rebound = await Create(runId, Snapshot(2), "session-inprocess-racing");
        Assert.Same(gateway, rebound);

        var receipt = await rebound.ExecuteAsync(prepared, _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal(ToolExecutionOutcome.Failed, receipt.Outcome);
        Assert.Equal("decision_stale", receipt.Code);
        Assert.Equal("before", await File.ReadAllTextAsync(writePath));

        await _fixture.Factory.CloseRunAsync(runId.Value);
    }

    [Fact]
    public async Task CreateAsync_propagates_cancellation_and_leaves_no_lease()
    {
        // The run never drives: a cancelled token must refuse the create
        // closed through the runtime's own open path, and must not leave
        // Host-side lease or gateway state behind.
        var runId = new RunId("run_" + new string('e', 32));
        var snapshot = Snapshot(1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _fixture.Factory.CreateAsync(
                _fixture.WorkspaceRoot,
                snapshot,
                runId,
                "session-inprocess-cancel",
                cancellation.Token));
        Assert.False(_fixture.Session.TryGetRunLease(runId.Value, out _));
        Assert.False(_fixture.Session.Owns("session-inprocess-cancel"));

        await _fixture.Factory.CloseRunAsync(runId.Value);
    }

    private async Task<global::RuBackend.Harness.Broker.InProcessToolGateway> Create(
        RunId runId,
        PolicySnapshot snapshot,
        string? agentSessionId) =>
        Assert.IsType<global::RuBackend.Harness.Broker.InProcessToolGateway>(
            await _fixture.Factory.CreateAsync(_fixture.WorkspaceRoot, snapshot, runId, agentSessionId, CancellationToken.None));

    private ToolProposal ExternalReadProposal(RunId runId) =>
        new(
            runId,
            new ModelStepId("step_" + new string('0', 32)),
            new ToolCallId("call_" + new string('0', 32)),
            "provider_call_1",
            modelOrder: 1,
            "read",
            "1.0.0",
            JsonSerializer.SerializeToElement(new JsonObject { ["path"] = _externalPath }));

    private static ToolProposal WorkspaceReadProposal(RunId runId) =>
        new(
            runId,
            new ModelStepId("step_" + new string('0', 32)),
            new ToolCallId("call_" + new string('0', 32)),
            "provider_call_1",
            modelOrder: 1,
            "read",
            "1.0.0",
            JsonSerializer.SerializeToElement(new JsonObject { ["path"] = "notes.txt" }));

    private static PolicySnapshot Snapshot(long version) =>
        PolicySnapshotCanonicalizer.Create(
            version,
            AgentPermissionPreset.Standard,
            new WorkspaceIdentity(
                new WorkspaceId("ws_" + new string('1', 32)),
                "VOL-INPROCESS",
                "fid-inprocess",
                new string('a', 64)),
            [],
            DateTimeOffset.Parse(
                "2026-08-24T12:00:00.0000000+00:00",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind));
}
