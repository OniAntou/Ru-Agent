using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Persistence;
using RuAgent.Protocol;
using RuBackend.Business;
using RuBackend.Business.Sessions;
using RuBackend.Harness;
using RuBackend.Harness.Policy;
using RuBackend.Harness.Workspace;
using RuBackend.Providers;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>
/// Fault injection at the drive boundary: whatever the fault is, the run must end
/// with a code that names its family, keep a bounded fingerprint of where it was
/// thrown, leave its durable events intact, and release the session for a new run.
/// The matrix is the contract; a new boundary belongs in this table.
/// </summary>
public sealed class RunFailureIsolationTests
{
    public enum Fault
    {
        Store,
        Database,
        Protocol,
        Transport,
        Unclassified,
    }

    [Theory]
    [InlineData(Fault.Store, "test_store_unavailable", "store")]
    [InlineData(Fault.Database, "agent_store_unavailable", "store")]
    [InlineData(Fault.Protocol, "invalid_run_token", "protocol")]
    [InlineData(Fault.Transport, "broker_gateway_unavailable", "transport")]
    [InlineData(Fault.Unclassified, "runtime_drive_failed", "invalid_operation")]
    public async Task A_boundary_fault_names_its_family_and_leaves_the_session_usable(
        Fault fault,
        string expectedCode,
        string expectedKind)
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var seeded = await SeedRunAsync(fixture);
        var roots = await RootsAsync(fixture, seeded);
        // The fault is thrown at the drive boundary (runtime construction), which is
        // where an unclassified failure used to collapse into runtime_drive_failed.
        // The durable store stays healthy so the failure transition itself can land.
        using var loop = Loop(fixture, fixture.EventStore, roots, new FaultingFactory(ExceptionFor(fault)));

        await loop.StepOnceAsync(default);

        var failed = await fixture.EventStore.GetRunAsync(seeded.Run.RunId, default);
        Assert.NotNull(failed);
        Assert.Equal(HarnessRunState.Failed, failed!.State);
        Assert.Equal(expectedCode, failed.TerminalErrorCode);
        Assert.NotNull(failed.TerminalFailure);
        Assert.Equal(expectedKind, failed.TerminalFailure!.Kind);
        Assert.False(string.IsNullOrWhiteSpace(failed.TerminalFailure.Site));

        // The failure is durable and complete: one run.failed event carrying the
        // code, and the run's cursor agrees with the events it recorded.
        var events = new List<HarnessEvent>();
        await foreach (var item in fixture.EventStore.ReadEventsAsync(seeded.Run.RunId, 0, default))
        {
            events.Add(item.Event);
        }

        Assert.Contains(events, item => item.Kind == "run.failed" && item.SafeCode == expectedCode);
        Assert.Equal(failed.LastSequence, events[^1].Sequence);

        // Released: the same session accepts a new run once the terminal state is committed.
        await AssertSessionAcceptsANewRunAsync(fixture, seeded);
    }

    /// <summary>
    /// The seed helper reuses one creation identity, so a second run for the same session
    /// needs its own: the point of this check is the partial unique index on active runs,
    /// which is exactly what a stuck session would trip.
    /// </summary>
    private static async Task AssertSessionAcceptsANewRunAsync(BackendHarnessTestFixture fixture, NewAgentRun seeded)
    {
        var now = DateTimeOffset.UtcNow;
        var next = new AgentRun(
            new RunId("run_" + Guid.NewGuid().ToString("N")),
            seeded.Run.SessionId,
            seeded.Run.WorkspaceId,
            new HarnessRunStatus(HarnessRunState.Running),
            0,
            1,
            1,
            0,
            0,
            seeded.Run.Preset,
            seeded.Run.PolicySnapshotVersion,
            seeded.Run.PolicySnapshotSha256,
            now,
            now);
        var request = new NewAgentRun(
            next,
            [new HarnessEventIntent(new EventId("evt_" + Guid.NewGuid().ToString("N")), HarnessEventKind.RunStateChanged)],
            [],
            [],
            new RunCreationIdentity(new string('a', 64), new string('b', 64), 1));

        var result = await fixture.EventStore.TryCreateRunAsync(request, CancellationToken.None);

        Assert.Equal(CreateRunOutcome.Created, result.Outcome);
    }

    private static Exception ExceptionFor(Fault fault) => fault switch
    {
        Fault.Store => new HarnessStoreException("test_store_unavailable"),
        // A raw driver failure escaping the store is a store failure, not a driver bug.
        Fault.Database => new SqliteException("database is locked", 5),
        Fault.Protocol => new AgentProtocolException("invalid_run_token"),
        Fault.Transport => new ToolTransportException("broker_gateway_unavailable"),
        _ => new InvalidOperationException("injected drive fault"),
    };

    private static async Task<NewAgentRun> SeedRunAsync(BackendHarnessTestFixture fixture) =>
        await fixture.SeedRunWithEncryptedWorkspaceAsync(
            new HarnessRunStatus(HarnessRunState.Running),
            "run_" + Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"));

    private static async Task<AgentRuntimeRootRegistry> RootsAsync(BackendHarnessTestFixture fixture, NewAgentRun seeded)
    {
        var workspace = Path.Combine(fixture.RootPath, "workspace-" + seeded.Run.RunId.Value);
        var policy = await new HarnessPolicySnapshotService(fixture.Options, fixture.KeyStore, new SystemTimeProvider())
            .LoadAsync(await new WindowsWorkspaceIdentityResolver().ResolveAsync(workspace), seeded.Run.Preset, seeded.Run.RunId, default);
        var roots = new AgentRuntimeRootRegistry();
        roots.Register(seeded.Run.RunId, workspace, policy);
        return roots;
    }

    private static AgentRuntimeLoopService Loop(
        BackendHarnessTestFixture fixture,
        IHarnessEventStore store,
        AgentRuntimeRootRegistry roots,
        IAgentRuntimeCoordinatorFactory factory)
    {
        var sessions = new SessionStore(new DataPaths(
            Path.Combine(fixture.RootPath, "settings.json"),
            Path.Combine(fixture.RootPath, "sessions"),
            Path.Combine(fixture.RootPath, "activity.log")));
        var dispatcher = new AgentConversationOutboxDispatcher(
            new HarnessOutboxStore(fixture.Options),
            fixture.PayloadStore,
            sessions,
            new HarnessModelContextBootstrapper(sessions, fixture.PayloadStore, new CryptographicIdGenerator(), TimeProvider.System),
            new AgentRunHistoryTransitionSink(factory, roots, store),
            TimeProvider.System);
        return new AgentRuntimeLoopService(
            dispatcher,
            store,
            factory,
            roots,
            NullLogger<AgentRuntimeLoopService>.Instance,
            new WorkspaceCoordinatorStore(fixture.Options),
            new WorkspaceJournalProtector(fixture.KeyStore),
            new UnavailableToolGatewayFactory());
    }

    private sealed class FaultingFactory(Exception fault) : IAgentRuntimeCoordinatorFactory
    {
        public Task<AgentRunCoordinator> CreateAsync(
            string root,
            PolicySnapshot? policySnapshot = null,
            RunId? runId = null,
            string? agentSessionId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<AgentRunCoordinator>(fault);

        public void DropRetainedApproval(RunId runId)
        {
        }
    }
}
