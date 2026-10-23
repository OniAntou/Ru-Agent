using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Reconciliation;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Policy;
using RuAgent.Harness.Persistence.Workspace;
using RuAgent.Harness.Persistence.Subagents;
using RuBackend.Business;
using RuBackend.Business.Sessions;
using RuBackend.Business.Settings;
using RuBackend.Harness;
using RuBackend.Harness.Git;
using RuBackend.Harness.Broker;
using RuBackend.Harness.Policy;
using RuBackend.Harness.Reconciliation;
using RuBackend.Harness.Workspace;

namespace RuBackend.Tests.Harness;

internal sealed class AgentV1ServiceTestBuilder(BackendHarnessTestFixture fixture)
{
    private IAgentRuntimeRootRegistry? _runtimeRoots;
    private IHarnessEventStore? _lifecycleStore;
    private RuBackend.Harness.Subagents.SubagentCancellationCoordinator? _subagentCancellation;

    public AgentV1ServiceTestBuilder WithRuntimeRoots(IAgentRuntimeRootRegistry runtimeRoots)
    {
        _runtimeRoots = runtimeRoots;
        return this;
    }

    public AgentV1ServiceTestBuilder WithLifecycleStore(IHarnessEventStore store)
    {
        _lifecycleStore = store;
        return this;
    }

    public AgentV1ServiceTestBuilder WithSubagentCancellation(
        RuBackend.Harness.Subagents.SubagentCancellationCoordinator coordinator)
    {
        _subagentCancellation = coordinator;
        return this;
    }

    public AgentV1ServiceTestBundle Build()
    {
        var workspaces = new SqliteWorkspaceStore(fixture.Options);
        var trustRequests = new WorkspaceTrustRequestStore(fixture.Options);
        var resolver = new WindowsWorkspaceIdentityResolver();
        var trust = new WorkspaceTrustService(resolver, workspaces, trustRequests, fixture.KeyStore);
        var roots = _runtimeRoots ?? new AgentRuntimeRootRegistry();
        var gitState = new RuBackend.Harness.Git.SqliteRunGitStateStore(
            Path.Combine(fixture.RootPath, "agent_git_state.db"));
        var snapshots = new WorkspaceStateSnapshotService(
            gitState,
            runId => roots.Snapshot().Where(pair => pair.RunId == runId.Value)
                .Select(pair => pair.WorkspaceRoot).FirstOrDefault());
        var sessions = new SessionStore(new DataPaths(
            Path.Combine(fixture.RootPath, "settings.json"),
            Path.Combine(fixture.RootPath, "sessions"),
            Path.Combine(fixture.RootPath, "activity.log")));

        var ids = new CryptographicIdGenerator();
        var time = new SystemTimeProvider();
        var permissionStore = new AgentSessionPermissionStore(fixture.Options);
        var workspaceCoordinator = new WorkspaceCoordinatorStore(fixture.Options);
        var journalProtector = new WorkspaceJournalProtector(fixture.KeyStore);
        var policySnapshotService = new HarnessPolicySnapshotService(fixture.Options, fixture.KeyStore, time);
        var inspectionGate = new ReconciliationInspectionGate();
        var gateway = new UnavailableToolGatewayFactory();
        var guard = new AgentV1SubagentGuard(fixture.Options, fixture.PayloadStore);
        var workspaceTrust = new AgentWorkspaceTrustService(fixture.EventStore,
            workspaces, trustRequests, trust, ids, time, guard, fixture.Options);
        var settings = new SettingsStore(new DataPaths(
            Path.Combine(fixture.RootPath, "settings.json"),
            Path.Combine(fixture.RootPath, "sessions"),
            Path.Combine(fixture.RootPath, "activity.log")));
        var commands = new AgentCommandsService(fixture.EventStore, ids, time, roots, snapshots,
            inspectionGate, gateway, guard);
        var workspaceAdmission = new AgentRunWorkspaceAdmissionService(
            resolver, trust, workspaces, workspaceCoordinator, policySnapshotService, ids, time);
        var creation = new AgentRunCreationService(fixture.EventStore, sessions, permissionStore,
            workspaceAdmission, roots, ids, time, guard, settings);
        var queries = new AgentRunQueryService(fixture.EventStore, sessions, workspaceTrust, guard,
            new SqliteAgentRunProviderSelectionStore(fixture.Options), fixture.PayloadStore, fixture.Options);
        var subagentStore = new SqliteSubagentStore(fixture.Options, fixture.PayloadStore);
        var subagentCancellation = _subagentCancellation ?? new RuBackend.Harness.Subagents.SubagentCancellationCoordinator(
            fixture.EventStore, subagentStore, new NoChildrenCleanup(), time);
        var lifecycle = new AgentRunLifecycleService(_lifecycleStore ?? fixture.EventStore, roots, inspectionGate,
            ids, time, guard, commands, subagentCancellation);

        return new AgentV1ServiceTestBundle(
            creation,
            queries,
            lifecycle,
            new AgentApprovalsService(fixture.EventStore, fixture.Options, workspaces, ids, time,
                fixture.PayloadStore, fixture.KeyStore, roots, gateway, guard),
            commands,
            new AgentInputsService(fixture.EventStore, ids, time, fixture.PayloadStore, guard),
            new AgentPoliciesService(fixture.EventStore, fixture.Options, workspaces, ids, time,
                sessions, permissionStore, fixture.KeyStore),
            new AgentWorkspaceService(fixture.Options, workspaces, trust, time, workspaceCoordinator, journalProtector),
            workspaceTrust,
            new AgentActivitiesService(fixture.EventStore, fixture.PayloadStore, new RuBackend.Conversation.MetadataPolicy()),
            gitState);
    }

    private sealed class NoChildrenCleanup : IAgentRuntimeRunCleanup
    {
        public Task CancelAndRemoveRunAsync(RunId runId, CancellationToken ct) =>
            throw new InvalidOperationException("unexpected_subagent_cleanup");
    }

}

internal sealed record AgentV1ServiceTestBundle(
    AgentRunCreationService Creation,
    AgentRunQueryService Queries,
    AgentRunLifecycleService Lifecycle,
    AgentApprovalsService Approvals,
    AgentCommandsService Commands,
    AgentInputsService Inputs,
    AgentPoliciesService Policies,
    AgentWorkspaceService Workspace,
    AgentWorkspaceTrustService WorkspaceTrust,
    AgentActivitiesService Activities,
    SqliteRunGitStateStore GitStateStore) : IDisposable
{
    public void Dispose() => GitStateStore.Dispose();
}
