using Microsoft.Extensions.Logging.Abstractions;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Persistence;
using RuBackend.Business;
using RuBackend.Business.Sessions;
using RuBackend.Harness;
using RuBackend.Harness.Policy;
using RuBackend.Harness.Workspace;
using RuBackend.Providers;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class AgentRuntimeConstructionFailureTests
{
    [Theory]
    [InlineData(HarnessRunState.Running)]
    [InlineData(HarnessRunState.WaitingApproval)]
    public async Task Factory_failure_commits_named_failure_before_releasing_the_run(HarnessRunState state)
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var seeded = await SeedAsync(fixture, new(state));
        var roots = await RootsAsync(fixture, seeded);
        using var loop = Loop(fixture, fixture.EventStore, roots);

        await loop.StepOnceAsync(default);

        var failed = await fixture.EventStore.GetRunAsync(seeded.Run.RunId, default);
        Assert.Equal(HarnessRunState.Failed, failed!.State);
        Assert.Equal("provider_catalog_unauthorized", failed.TerminalErrorCode);
        Assert.False(roots.TryGetRoot(seeded.Run.RunId, out _));
        var events = new List<HarnessEvent>();
        await foreach (var item in fixture.EventStore.ReadEventsAsync(seeded.Run.RunId, 0, default)) events.Add(item.Event);
        Assert.Single(events, item => item.Kind == "run.failed" && item.SafeCode == failed.TerminalErrorCode);
    }

    [Fact]
    public async Task Store_failure_retains_registration_until_failure_can_be_committed()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var seeded = await SeedAsync(fixture, new(HarnessRunState.Running));
        var roots = await RootsAsync(fixture, seeded);
        var store = new RacingStore(fixture.EventStore) { FailWrites = true };
        using var loop = Loop(fixture, store, roots);

        await loop.StepOnceAsync(default);
        Assert.True(roots.TryGetRoot(seeded.Run.RunId, out _));
        Assert.Equal(HarnessRunState.Running, (await fixture.EventStore.GetRunAsync(seeded.Run.RunId, default))!.State);

        store.FailWrites = false;
        await loop.StepOnceAsync(default);
        Assert.Equal(HarnessRunState.Failed, (await fixture.EventStore.GetRunAsync(seeded.Run.RunId, default))!.State);
        Assert.False(roots.TryGetRoot(seeded.Run.RunId, out _));
    }

    [Theory]
    [InlineData(false, HarnessRunState.Failed)]
    [InlineData(true, HarnessRunState.Cancelled)]
    public async Task Failure_commit_rebases_on_progress_and_preserves_concurrent_cancellation(bool cancel, HarnessRunState expected)
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var seeded = await SeedAsync(fixture, new(HarnessRunState.Running));
        var roots = await RootsAsync(fixture, seeded);
        var store = new RacingStore(fixture.EventStore) { RaceOnce = true, CancelInstead = cancel };
        using var loop = Loop(fixture, store, roots);

        await loop.StepOnceAsync(default);

        var final = await fixture.EventStore.GetRunAsync(seeded.Run.RunId, default);
        Assert.Equal(expected, final!.State);
        Assert.Equal(cancel ? 1 : 2, store.Writes);
        Assert.False(roots.TryGetRoot(seeded.Run.RunId, out _));
    }

    [Fact]
    public async Task Repeated_commit_conflicts_are_bounded_and_retain_registration_for_retry()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var seeded = await SeedAsync(fixture, new(HarnessRunState.Running));
        var roots = await RootsAsync(fixture, seeded);
        var store = new RacingStore(fixture.EventStore) { AlwaysConflict = true };
        using var loop = Loop(fixture, store, roots);

        await loop.StepOnceAsync(default);

        Assert.Equal(8, store.Writes);
        Assert.True(roots.TryGetRoot(seeded.Run.RunId, out _));
        Assert.Equal(HarnessRunState.Running, (await fixture.EventStore.GetRunAsync(seeded.Run.RunId, default))!.State);
    }

    [Fact]
    public async Task Initial_history_acknowledgement_failure_ends_the_run_without_the_provider()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var seeded = await SeedAsync(fixture, new(HarnessRunState.WaitingHistory, HistoryWaitKind.UserBeforeFirstStep));
        var roots = await RootsAsync(fixture, seeded);
        var sink = new AgentRunHistoryTransitionSink(new FailingFactory(), roots, fixture.EventStore);

        await sink.AcknowledgeAsync(seeded.Run.RunId, HistoryWaitKind.UserBeforeFirstStep, default);

        var failed = await fixture.EventStore.GetRunAsync(seeded.Run.RunId, default);
        Assert.Equal(HarnessRunState.Failed, failed!.State);
        Assert.Equal("provider_catalog_unauthorized", failed.TerminalErrorCode);
    }

    [Fact]
    public async Task History_delivery_failure_is_persisted_without_constructing_the_provider()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var seeded = await SeedAsync(fixture, new(HarnessRunState.WaitingHistory, HistoryWaitKind.UserBeforeFirstStep));
        var roots = await RootsAsync(fixture, seeded);
        var sink = new AgentRunHistoryTransitionSink(new FailingFactory(), roots, fixture.EventStore);

        await sink.FailAsync(seeded.Run.RunId, "session_not_found", default);

        var failed = await fixture.EventStore.GetRunAsync(seeded.Run.RunId, default);
        Assert.Equal(HarnessRunState.Failed, failed!.State);
        Assert.Equal("session_not_found", failed.TerminalErrorCode);
    }

    private static async Task<NewAgentRun> SeedAsync(BackendHarnessTestFixture fixture, HarnessRunStatus status)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var payloadId = new PayloadId("payload_" + Guid.NewGuid().ToString("N"));
        HarnessPayloadIntent[] payloads = status.State == HarnessRunState.WaitingHistory
            ? [new(payloadId, HarnessPayloadKind.UserPrompt,
                HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                HarnessPayloadEncoding.Utf8Text, 1, new byte[] { 65 })]
            : [];
        ConversationOutboxIntent[] outbox = status.State == HarnessRunState.WaitingHistory
            ? [new(new CryptographicIdGenerator().NewEventId(), new SessionId(sessionId), payloadId,
                ConversationOutboxRole.User, HistoryWaitKind.UserBeforeFirstStep)]
            : [];
        var seeded = await fixture.SeedRunWithEncryptedWorkspaceAsync(status, "run_" + Guid.NewGuid().ToString("N"), sessionId,
            payloads: payloads, outbox: outbox);
        if (status.State == HarnessRunState.WaitingApproval)
        {
            // This test exercises construction failure, not expiry of the fixture's historical timestamp.
            var run = seeded.Run;
            var fresh = AgentV1Shared.AdvanceRun(run, run.Status, run.BudgetWindow,
                run.ModelStepsInWindow, run.ToolCallsInWindow, 1, DateTimeOffset.UtcNow);
            var committed = await fixture.EventStore.CommitAsync(new HarnessCommit(run.RunId, run.Version, fresh,
                [new HarnessEventIntent(new CryptographicIdGenerator().NewEventId(), HarnessEventKind.RunStateChanged,
                    safeCode: "waiting_approval")], [], []), default);
            Assert.Equal(HarnessCommitOutcome.Committed, committed.Outcome);
        }
        return seeded;
    }

    private static async Task<AgentRuntimeRootRegistry> RootsAsync(BackendHarnessTestFixture fixture, NewAgentRun seeded)
    {
        var workspace = Directory.GetDirectories(fixture.RootPath, "workspace-*").Single();
        var policy = await new HarnessPolicySnapshotService(fixture.Options, fixture.KeyStore, new SystemTimeProvider())
            .LoadAsync(await new WindowsWorkspaceIdentityResolver().ResolveAsync(workspace), seeded.Run.Preset, seeded.Run.RunId, default);
        var roots = new AgentRuntimeRootRegistry();
        roots.Register(seeded.Run.RunId, workspace, policy);
        return roots;
    }

    private static AgentRuntimeLoopService Loop(BackendHarnessTestFixture fixture, IHarnessEventStore store, AgentRuntimeRootRegistry roots)
    {
        var sessions = new SessionStore(new DataPaths(Path.Combine(fixture.RootPath, "settings.json"),
            Path.Combine(fixture.RootPath, "sessions"), Path.Combine(fixture.RootPath, "activity.log")));
        var factory = new FailingFactory();
        var dispatcher = new AgentConversationOutboxDispatcher(new HarnessOutboxStore(fixture.Options), fixture.PayloadStore, sessions,
            new HarnessModelContextBootstrapper(sessions, fixture.PayloadStore, new CryptographicIdGenerator(), TimeProvider.System),
            new AgentRunHistoryTransitionSink(factory, roots, store), TimeProvider.System);
        return new AgentRuntimeLoopService(dispatcher, store, factory, roots, NullLogger<AgentRuntimeLoopService>.Instance,
            new WorkspaceCoordinatorStore(fixture.Options), new WorkspaceJournalProtector(fixture.KeyStore), new UnavailableToolGatewayFactory());
    }

    private sealed class FailingFactory : IAgentRuntimeCoordinatorFactory
    {
        public Task<AgentRunCoordinator> CreateAsync(string root, PolicySnapshot? policySnapshot = null, RunId? runId = null,
            string? agentSessionId = null, CancellationToken cancellationToken = default) =>
            Task.FromException<AgentRunCoordinator>(new ProviderCatalogException("catalog_unauthorized"));
        public void DropRetainedApproval(RunId runId) { }
    }

    private sealed class RacingStore(IHarnessEventStore inner) : IHarnessEventStore
    {
        public bool FailWrites { get; set; }
        public bool RaceOnce { get; init; }
        public bool AlwaysConflict { get; init; }
        public bool CancelInstead { get; init; }
        public int Writes { get; private set; }
        public Task<CreateRunResult> TryCreateRunAsync(NewAgentRun request, CancellationToken ct) => inner.TryCreateRunAsync(request, ct);
        public Task<AgentRun?> GetRunAsync(RunId id, CancellationToken ct) => inner.GetRunAsync(id, ct);
        public IAsyncEnumerable<HarnessEventEnvelope> ReadEventsAsync(RunId id, long after, CancellationToken ct) => inner.ReadEventsAsync(id, after, ct);
        public async Task<HarnessCommitResult> CommitAsync(HarnessCommit commit, CancellationToken ct)
        {
            Writes++;
            if (FailWrites) throw new HarnessStoreException("test_store_unavailable");
            if (AlwaysConflict)
                return HarnessCommitResult.VersionConflict((await inner.GetRunAsync(commit.RunId, ct))!);
            if (RaceOnce && Writes == 1)
            {
                var run = (await inner.GetRunAsync(commit.RunId, ct))!;
                var next = AgentV1Shared.AdvanceRun(run, CancelInstead ? new(HarnessRunState.Cancelled) : run.Status,
                    run.BudgetWindow, run.ModelStepsInWindow, run.ToolCallsInWindow, 1, DateTimeOffset.UtcNow);
                var progress = await inner.CommitAsync(new HarnessCommit(run.RunId, run.Version, next,
                    [new HarnessEventIntent(new CryptographicIdGenerator().NewEventId(), HarnessEventKind.RunStateChanged,
                        safeCode: CancelInstead ? "cancelled" : "worker_progress")], [], []), ct);
                Assert.Equal(HarnessCommitOutcome.Committed, progress.Outcome);
            }
            return await inner.CommitAsync(commit, ct);
        }
    }
}
