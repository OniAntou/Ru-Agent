using Microsoft.Extensions.Logging.Abstractions;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Persistence;
using RuBackend.Business;
using RuBackend.Business.Sessions;
using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed partial class HarnessSessionDeletionCoordinatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_cancels_owned_children_even_when_parent_is_already_terminal(bool terminalParent)
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var root = await fixture.SeedRunAsync(new(HarnessRunState.Running));
        var agents = new RuAgent.Harness.Persistence.Subagents.SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        var child = new AgentRun(new("run_" + new string('b', 32)), new(new string('b', 32)), root.Run.WorkspaceId,
            new(HarnessRunState.Created), 0, 1, 1, 0, 0, root.Run.Preset, root.Run.PolicySnapshotVersion,
            root.Run.PolicySnapshotSha256, BackendHarnessTestFixture.Timestamp, BackendHarnessTestFixture.Timestamp);
        await agents.CreateAsync(new("agent_one", root.Run.RunId, "spawn_one", RuAgent.Protocol.Subagents.SubagentRole.Review,
            new(child, [new(new("evt_" + new string('b', 32)), HarnessEventKind.RunStateChanged)], [], [],
                new(new string('a', 64), new string('b', 64), 1))), default);
        if (terminalParent)
            await fixture.EventStore.CommitAsync(RuAgent.Harness.Core.Subagents.SubagentAdmission.CreateTerminalCommit(root.Run,
                new("evt_" + new string('c', 32)), false, "fixture_closed", DateTimeOffset.UtcNow), default);
        var cleanup = new FamilyCleanup();
        var cancellation = new RuBackend.Harness.Subagents.SubagentCancellationCoordinator(fixture.EventStore,
            agents, cleanup, new SystemTimeProvider());
        var coordinator = new HarnessSessionDeletionCoordinator(new HarnessSessionArtifactStore(fixture.Options),
            new RecordingSessionStore([], true), TimeProvider.System, runCleanup: cleanup,
            subagentCancellation: cancellation);
        Assert.Equal(HarnessSessionDeletionOutcome.Deleted, await coordinator.DeleteAsync(root.Run.SessionId.Value, default));
        Assert.Contains(child.RunId, cleanup.Runs);
        Assert.Null(await fixture.EventStore.GetRunAsync(child.RunId, default));
    }

    private sealed class FamilyCleanup : IAgentRuntimeRunCleanup
    {
        public List<RunId> Runs { get; } = [];
        public Task CancelAndRemoveRunAsync(RunId runId, CancellationToken ct)
        {
            Runs.Add(runId);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Harness_artifacts_are_closed_and_removed_before_the_session_file()
    {
        var trace = new List<string>();
        var artifacts = new RecordingArtifactStore(trace);
        var sessions = new RecordingSessionStore(trace, deleteResult: true);
        var coordinator = new HarnessSessionDeletionCoordinator(
            artifacts,
            sessions,
            TimeProvider.System);

        var outcome = await coordinator.DeleteAsync(
            "11111111111111111111111111111111",
            CancellationToken.None);

        Assert.Equal(HarnessSessionDeletionOutcome.Deleted, outcome);
        Assert.Equal(["tombstone", "harness", "session"], trace);
    }

    [Fact]
    public async Task Broker_grants_are_revoked_and_the_session_closed_before_the_session_file()
    {
        var trace = new List<string>();
        var artifacts = new RecordingArtifactStore(trace);
        var sessions = new RecordingSessionStore(trace, deleteResult: true);
        var gateways = new StubGatewayFactory(trace, revokeResult: true);
        var coordinator = new HarnessSessionDeletionCoordinator(
            artifacts,
            sessions,
            TimeProvider.System,
            gateways);

        var outcome = await coordinator.DeleteAsync(
            "11111111111111111111111111111111",
            CancellationToken.None);

        Assert.Equal(HarnessSessionDeletionOutcome.Deleted, outcome);
        // The revoke fails the delete closed when the authority rejects it;
        // the close is what releases the authority's session-scoped run state
        // (the wire session died with its connection, while the in-process
        // session lives for the host).
        Assert.Equal(
            [
                "revoke:11111111111111111111111111111111",
                "close:11111111111111111111111111111111",
                "tombstone",
                "harness",
                "session",
            ],
            trace);
    }

    [Fact]
    public async Task Tombstone_failure_fails_the_delete_closed_before_the_session_file()
    {
        var trace = new List<string>();
        var artifacts = new RecordingArtifactStore(trace)
        {
            MarkError = new HarnessStoreException("harness_store_unavailable"),
        };
        var sessions = new RecordingSessionStore(trace, deleteResult: true);
        var coordinator = new HarnessSessionDeletionCoordinator(
            artifacts,
            sessions,
            TimeProvider.System);

        await Assert.ThrowsAsync<HarnessStoreException>(() => coordinator.DeleteAsync(
            "11111111111111111111111111111111",
            CancellationToken.None));
        Assert.DoesNotContain("harness", trace);
        Assert.DoesNotContain("session", trace);
    }

    [Fact]
    public async Task Broker_revoke_rejection_fails_the_delete_closed()
    {
        var trace = new List<string>();
        var artifacts = new RecordingArtifactStore(trace);
        var sessions = new RecordingSessionStore(trace, deleteResult: true);
        var gateways = new StubGatewayFactory(trace, revokeResult: false);
        var coordinator = new HarnessSessionDeletionCoordinator(
            artifacts,
            sessions,
            TimeProvider.System,
            gateways);

        await Assert.ThrowsAsync<HarnessStoreException>(() => coordinator.DeleteAsync(
            "11111111111111111111111111111111",
            CancellationToken.None));
        Assert.DoesNotContain("session", trace);
        Assert.DoesNotContain("close:11111111111111111111111111111111", trace);
    }

    [Fact]
    public async Task Harness_cleanup_failure_still_deletes_the_session_file_but_reports_pending()
    {
        var trace = new List<string>();
        var artifacts = new RecordingArtifactStore(trace)
        {
            Error = new HarnessStoreException("harness_store_unavailable"),
        };
        var sessions = new RecordingSessionStore(trace, deleteResult: true);
        var coordinator = new HarnessSessionDeletionCoordinator(
            artifacts,
            sessions,
            TimeProvider.System);

        var outcome = await coordinator.DeleteAsync(
            "11111111111111111111111111111111",
            CancellationToken.None);

        Assert.Equal(HarnessSessionDeletionOutcome.ArtifactsPending, outcome);
        Assert.Equal(["tombstone", "harness", "session"], trace);
    }

    [Fact]
    public async Task Deleted_session_refuses_a_late_run_creation_with_a_bounded_code()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var seeded = await fixture.SeedRunAsync();
            var artifacts = new HarnessSessionArtifactStore(fixture.Options);
            var sessions = new RecordingSessionStore([], deleteResult: true);
            var coordinator = new HarnessSessionDeletionCoordinator(
                artifacts,
                sessions,
                TimeProvider.System);

            var outcome = await coordinator.DeleteAsync(
                seeded.Run.SessionId.Value,
                CancellationToken.None);

            Assert.Equal(HarnessSessionDeletionOutcome.Deleted, outcome);
            var late = await fixture.EventStore.TryCreateRunAsync(seeded, CancellationToken.None);
            Assert.Equal(CreateRunOutcome.Conflict, late.Outcome);
            Assert.Equal("session_deleted", late.ErrorCode);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Deleting_a_session_cancels_in_flight_runs_and_leaves_no_active_work()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var seeded = await fixture.SeedRunAsync(new HarnessRunStatus(HarnessRunState.WaitingUser));
            var registry = new RecordingRootRegistry();
            registry.Register(seeded.Run.RunId, fixture.RootPath);
            var loop = NewRuntimeLoop(fixture, registry);
            await loop.StepOnceAsync(CancellationToken.None);
            Assert.Equal(1, loop.ActiveWorkCount);

            var artifacts = new RecordingArtifactStore([])
            {
                NonTerminalRunIds = [seeded.Run.RunId.Value],
            };
            var sessions = new RecordingSessionStore([], deleteResult: true);
            var coordinator = new HarnessSessionDeletionCoordinator(
                artifacts,
                sessions,
                TimeProvider.System,
                brokerGateways: null,
                runCleanup: loop);

            var outcome = await coordinator.DeleteAsync(
                seeded.Run.SessionId.Value,
                CancellationToken.None);

            Assert.Equal(HarnessSessionDeletionOutcome.Deleted, outcome);
            Assert.Contains(seeded.Run.RunId.Value, registry.CancelledRunIds);
            Assert.Contains(seeded.Run.RunId.Value, registry.RemovedRunIds);

            await loop.StepOnceAsync(CancellationToken.None);
            Assert.Equal(0, loop.ActiveWorkCount);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Deleted_run_drive_pass_removes_the_registration()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var registry = new RecordingRootRegistry();
            var runId = new RunId("run_" + Guid.NewGuid().ToString("N"));
            registry.Register(runId, fixture.RootPath);
            var loop = NewRuntimeLoop(fixture, registry);

            await loop.StepOnceAsync(CancellationToken.None);

            Assert.Contains(runId.Value, registry.RemovedRunIds);
            Assert.False(registry.TryGetRoot(runId, out _));

            await loop.StepOnceAsync(CancellationToken.None);
            Assert.Equal(0, loop.ActiveWorkCount);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static AgentRuntimeLoopService NewRuntimeLoop(
        BackendHarnessTestFixture fixture,
        IAgentRuntimeRootRegistry roots)
    {
        var sessions = new SessionStore(
            new DataPaths(
                Path.Combine(fixture.RootPath, "settings.json"),
                Path.Combine(fixture.RootPath, "sessions"),
                Path.Combine(fixture.RootPath, "activity.log")),
            TimeProvider.System);
        var dispatcher = new AgentConversationOutboxDispatcher(
            new HarnessOutboxStore(fixture.Options),
            fixture.PayloadStore,
            sessions,
            new HarnessModelContextBootstrapper(
                sessions,
                fixture.PayloadStore,
                new CryptographicIdGenerator(),
                TimeProvider.System),
            new AgentRunHistoryTransitionSink(new UnusedCoordinatorFactory(), roots),
            TimeProvider.System);
        return new AgentRuntimeLoopService(
            dispatcher,
            fixture.EventStore,
            new UnusedCoordinatorFactory(),
            roots,
            NullLogger<AgentRuntimeLoopService>.Instance);
    }

    private sealed class RecordingRootRegistry : IAgentRuntimeRootRegistry
    {
        private readonly Dictionary<string, string> _roots = new(StringComparer.Ordinal);

        public List<string> CancelledRunIds { get; } = [];

        public List<string> RemovedRunIds { get; } = [];

        public void Register(RunId runId, string workspaceRoot) =>
            _roots[runId.Value] = workspaceRoot;

        public void Register(RunId runId, string workspaceRoot, PolicySnapshot policySnapshot) =>
            _roots[runId.Value] = workspaceRoot;

        public bool TryGetRoot(RunId runId, out string? workspaceRoot) =>
            _roots.TryGetValue(runId.Value, out workspaceRoot);

        public bool TryGetPolicySnapshot(RunId runId, out PolicySnapshot? policySnapshot)
        {
            policySnapshot = null;
            return false;
        }

        public void RegisterCoordinator(RunId runId, AgentRunCoordinator coordinator)
        {
        }

        public bool TryGetCoordinator(RunId runId, out AgentRunCoordinator? coordinator)
        {
            coordinator = null;
            return false;
        }

        public Task CancelRunningToolsAsync(RunId runId, CancellationToken cancellationToken)
        {
            CancelledRunIds.Add(runId.Value);
            return Task.CompletedTask;
        }

        public void Remove(RunId runId)
        {
            RemovedRunIds.Add(runId.Value);
            _roots.Remove(runId.Value);
        }

        public IReadOnlyList<(string RunId, string WorkspaceRoot)> Snapshot() =>
            _roots.Select(pair => (pair.Key, pair.Value)).ToArray();
    }

    private sealed class UnusedCoordinatorFactory : IAgentRuntimeCoordinatorFactory
    {
        public Task<AgentRunCoordinator> CreateAsync(
            string workspaceRoot,
            PolicySnapshot? policySnapshot = null,
            RunId? runId = null,
            string? agentSessionId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<AgentRunCoordinator>(new NotSupportedException());

        public void DropRetainedApproval(RunId runId)
        {
        }
    }

    private sealed class StubGatewayFactory(List<string> trace, bool revokeResult)
        : RuBackend.Harness.Broker.IToolGatewayFactory
    {
        public bool IsConfigured => true;

        public Task<IToolGateway> CreateAsync(
            string workspaceRoot,
            PolicySnapshot policySnapshot,
            RunId runId,
            string? agentSessionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<System.Text.Json.Nodes.JsonObject> ListSessionTasksAsync(
            string runId,
            string? killExecutionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<(bool Authorized, string? AuthorizationToken)> RespondApprovalAsync(
            string runId,
            string hostApprovalId,
            string command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> RevokeSessionGrantsAsync(string sessionId, CancellationToken cancellationToken)
        {
            trace.Add("revoke:" + sessionId);
            return Task.FromResult(revokeResult);
        }

        public Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken)
        {
            trace.Add("close:" + sessionId);
            return Task.CompletedTask;
        }

        public Task CloseRunAsync(string runId) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingArtifactStore(List<string> trace)
        : IHarnessSessionArtifactStore
    {
        public Exception? Error { get; init; }

        public Exception? MarkError { get; init; }
        public HarnessSessionArtifactDeleteResult DeleteResult { get; init; } = new(1, 1);

        public IReadOnlyList<string> NonTerminalRunIds { get; init; } = [];

        public Task MarkSessionDeletedAsync(
            string sessionId,
            DateTimeOffset deletedAt,
            CancellationToken cancellationToken)
        {
            trace.Add("tombstone");
            return MarkError is null
                ? Task.CompletedTask
                : Task.FromException(MarkError);
        }

        public Task<IReadOnlyList<string>> ListNonTerminalRunIdsAsync(
            string sessionId,
            CancellationToken cancellationToken)
        {
            trace.Add("list");
            return Task.FromResult(NonTerminalRunIds);
        }

        public Task<HarnessSessionArtifactDeleteResult> CloseAndDeleteAsync(
            string sessionId,
            DateTimeOffset deletedAt,
            CancellationToken cancellationToken)
        {
            trace.Add("harness");
            return Error is null
                ? Task.FromResult(DeleteResult)
                : Task.FromException<HarnessSessionArtifactDeleteResult>(Error);
        }
    }

    internal sealed class RecordingSessionStore(List<string> trace, bool deleteResult)
        : ISessionStore
    {
        public bool PrimaryMissing { get; init; }

        public async Task<SessionDeleteOutcome> DeleteWithOutcomeAsync(string sessionId, CancellationToken cancellationToken) =>
            await DeleteAsync(sessionId, cancellationToken) ? SessionDeleteOutcome.Deleted
                : PrimaryMissing ? SessionDeleteOutcome.NotFound : SessionDeleteOutcome.Failed;

        public Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SessionSummary>>([]);

        public Task<SessionDocument?> GetAsync(
            string sessionId,
            CancellationToken cancellationToken) =>
            Task.FromResult<SessionDocument?>(null);

        public Task<string?> CreateAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task<bool> DeleteAsync(
            string sessionId,
            CancellationToken cancellationToken)
        {
            trace.Add("session");
            return Task.FromResult(deleteResult);
        }

        public Task<bool> AppendAsync(
            string sessionId,
            IReadOnlyList<SessionMessage> messages,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<SessionAppendOutcome> AppendIfAbsentAsync(
            string sessionId,
            SessionMessage message,
            CancellationToken cancellationToken) =>
            Task.FromResult(SessionAppendOutcome.SessionNotFound);

        public Task<IReadOnlyList<ConversationTurn>> GetContextTurnsAsync(
            string sessionId,
            int maxTurns,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ConversationTurn>>([]);
    }
}
