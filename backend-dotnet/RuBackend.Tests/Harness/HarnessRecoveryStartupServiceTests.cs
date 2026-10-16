using Microsoft.Extensions.Logging.Abstractions;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Workspace;
using RuBackend.Business;
using RuBackend.Business.Sessions;
using RuBackend.Harness;
using RuBackend.Harness.Policy;
using RuBackend.Harness.Reconciliation;
using RuBackend.Harness.Workspace;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class HarnessRecoveryStartupServiceTests
{
    [Fact]
    public async Task Transient_scan_fault_retries_and_still_frees_the_workspace()
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var request = await fixture.SeedRunAsync(new HarnessRunStatus(HarnessRunState.WaitingApproval));
            var runId = request.Run.RunId;
            var store = new FlakyStore(fixture.EventStore);
            var service = new HarnessRecoveryStartupService(
                store,
                fixture.Options,
                NullLogger<HarnessRecoveryStartupService>.Instance);

            await service.StartAsync(CancellationToken.None);
            try
            {
                var recovered = await WaitForTerminalAsync(
                    fixture.EventStore,
                    runId,
                    TimeSpan.FromSeconds(15));

                Assert.True(recovered, "the parked run must still be recovered after a transient scan fault");
                var after = await fixture.EventStore.GetRunAsync(runId, CancellationToken.None);
                Assert.Equal(HarnessRunState.Failed, after!.State);
                Assert.Equal("approval_expired", after.TerminalErrorCode);
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    /// <summary>
    /// A parked run that outlived the host must be resumable: after the sweep its root
    /// and policy snapshot are registered, and while it stays parked the drive loop must
    /// leave it alone. Only once Continue moves it to Running may the loop drive it —
    /// that is what the registered root is for.
    /// </summary>
    [Theory]
    [InlineData(HarnessRunState.WaitingUser)]
    [InlineData(HarnessRunState.ReconciliationRequired)]
    public async Task Parked_runs_are_registered_for_resume_but_not_driven_until_continued(
        HarnessRunState parkedState)
    {
        var fixture = await BackendHarnessTestFixture.CreateAsync();
        try
        {
            var runId = "run_" + Guid.NewGuid().ToString("N");
            var sessionId = Guid.NewGuid().ToString("N");
            var request = await fixture.SeedRunWithEncryptedWorkspaceAsync(
                new HarnessRunStatus(parkedState),
                runId,
                sessionId);
            var roots = new AgentRuntimeRootRegistry();
            var service = new HarnessRecoveryStartupService(
                fixture.EventStore,
                fixture.Options,
                NullLogger<HarnessRecoveryStartupService>.Instance,
                new WorkspaceTrustService(
                    new WindowsWorkspaceIdentityResolver(),
                    new SqliteWorkspaceStore(fixture.Options),
                    new WorkspaceTrustRequestStore(fixture.Options),
                    fixture.KeyStore),
                new SqliteWorkspaceStore(fixture.Options),
                new HarnessPolicySnapshotService(fixture.Options, fixture.KeyStore, new SystemTimeProvider()),
                roots);

            await service.StartAsync(CancellationToken.None);
            try
            {
                var registered = await WaitForRootAsync(roots, request.Run.RunId, TimeSpan.FromSeconds(15));
                Assert.True(registered, "the parked run's root must be registered so a later Continue can be driven");
                Assert.True(roots.TryGetPolicySnapshot(request.Run.RunId, out var policySnapshot));
                Assert.NotNull(policySnapshot);

                var factory = new RecordingCoordinatorFactory();
                var loop = NewRuntimeLoop(fixture, roots, factory);
                await loop.StartAsync(CancellationToken.None);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    Assert.Equal(0, factory.Calls);
                    Assert.True(roots.TryGetRoot(request.Run.RunId, out _), "a parked run must stay registered");
                    var parked = await fixture.EventStore.GetRunAsync(request.Run.RunId, CancellationToken.None);
                    Assert.Equal(parkedState, parked!.State);

                    // Continue's own transition (Continue for WaitingUser, the typed
                    // reconciliation decision for ReconciliationRequired) is the only change
                    // needed: with the root registered the loop picks the run up from here.
                    await CommitResumeAsync(fixture, parked);
                    var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
                    while (factory.Calls == 0 && DateTimeOffset.UtcNow < deadline)
                    {
                        await Task.Delay(100);
                    }

                    Assert.True(factory.Calls > 0, "the resumed run must be drivable through its registered root");
                }
                finally
                {
                    await loop.StopAsync(CancellationToken.None);
                }
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static async Task<bool> WaitForRootAsync(
        AgentRuntimeRootRegistry roots,
        RunId runId,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (roots.TryGetRoot(runId, out _))
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    private static async Task CommitResumeAsync(BackendHarnessTestFixture fixture, AgentRun run)
    {
        var transition = run.State == HarnessRunState.ReconciliationRequired
            ? AgentRunStateMachine.TryTransition(run.Status, HarnessRunTransition.ApplyReconciliation)
            : AgentRunStateMachine.TryTransition(run.Status, HarnessRunTransition.Continue);
        Assert.True(transition.Accepted);
        var eventKind = run.State == HarnessRunState.ReconciliationRequired
            ? HarnessEventKind.ReconciliationResolved
            : HarnessEventKind.ContinueRequested;
        var next = new AgentRun(
            run.RunId,
            run.SessionId,
            run.WorkspaceId,
            transition.Next,
            run.Version + 1,
            run.LastSequence + 1,
            run.BudgetWindow + 1,
            0,
            0,
            run.Preset,
            run.PolicySnapshotVersion,
            run.PolicySnapshotSha256,
            run.CreatedAt,
            DateTimeOffset.UtcNow,
            terminalAt: null,
            terminalErrorCode: null,
            run.PermissionProfileVersion);
        var result = await fixture.EventStore.CommitAsync(
            new HarnessCommit(
                run.RunId,
                run.Version,
                next,
                [new HarnessEventIntent(new EventId($"evt_{Guid.NewGuid():N}"), eventKind)],
                Array.Empty<HarnessPayloadIntent>(),
                Array.Empty<ConversationOutboxIntent>()),
            CancellationToken.None);
        Assert.Equal(HarnessCommitOutcome.Committed, result.Outcome);
    }

    private static AgentRuntimeLoopService NewRuntimeLoop(
        BackendHarnessTestFixture fixture,
        AgentRuntimeRootRegistry roots,
        IAgentRuntimeCoordinatorFactory coordinatorFactory)
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
            new AgentRunHistoryTransitionSink(coordinatorFactory, roots),
            TimeProvider.System);
        return new AgentRuntimeLoopService(
            dispatcher,
            fixture.EventStore,
            coordinatorFactory,
            roots,
            NullLogger<AgentRuntimeLoopService>.Instance);
    }

    /// <summary>
    /// Counts every attempt to build a coordinator. A parked run must never reach one;
    /// a resumed run must.
    /// </summary>
    private sealed class RecordingCoordinatorFactory : IAgentRuntimeCoordinatorFactory
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public AgentRunCoordinator Create(
            string workspaceRoot,
            RuAgent.Harness.Core.Policy.PolicySnapshot? policySnapshot = null,
            RunId? runId = null,
            string? agentSessionId = null,
            RuAgent.Harness.Core.Context.ContextBudgetPolicy? contextBudgetPolicy = null)
        {
            Interlocked.Increment(ref _calls);
            throw new NotSupportedException("the test only observes drive attempts");
        }

        public Task<AgentRunCoordinator> CreateAsync(
            string workspaceRoot,
            RuAgent.Harness.Core.Policy.PolicySnapshot? policySnapshot = null,
            RunId? runId = null,
            string? agentSessionId = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            throw new NotSupportedException("the test only observes drive attempts");
        }

        public void DropRetainedApproval(RunId runId)
        {
        }
    }

    private static async Task<bool> WaitForTerminalAsync(
        IHarnessEventStore store,
        RunId runId,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var run = await store.GetRunAsync(runId, CancellationToken.None);
            if (run is null || run.State.IsTerminal())
            {
                return true;
            }

            await Task.Delay(200);
        }

        return false;
    }

    /// <summary>
    /// One run read fails as it would when the Broker holds the database while the
    /// runtime starts: the sweep must report the parked run so the startup service
    /// retries instead of leaving the session blocked until the next restart.
    /// </summary>
    private sealed class FlakyStore(IHarnessEventStore inner) : IHarnessEventStore
    {
        private int _failures;

        public Task<CreateRunResult> TryCreateRunAsync(
            NewAgentRun request,
            CancellationToken cancellationToken) =>
            inner.TryCreateRunAsync(request, cancellationToken);

        public Task<AgentRun?> GetRunAsync(RunId runId, CancellationToken cancellationToken)
        {
            if (++_failures == 1)
            {
                return Task.FromException<AgentRun?>(
                    new InvalidOperationException("database is locked"));
            }

            return inner.GetRunAsync(runId, cancellationToken);
        }

        public Task<HarnessCommitResult> CommitAsync(
            HarnessCommit commit,
            CancellationToken cancellationToken) =>
            inner.CommitAsync(commit, cancellationToken);

        public IAsyncEnumerable<HarnessEventEnvelope> ReadEventsAsync(
            RunId runId,
            long afterSequence,
            CancellationToken cancellationToken) =>
            inner.ReadEventsAsync(runId, afterSequence, cancellationToken);
    }
}
