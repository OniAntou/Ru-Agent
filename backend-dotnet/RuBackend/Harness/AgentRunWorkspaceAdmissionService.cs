using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Workspace;
using RuBackend.Harness.Policy;
using RuBackend.Harness.Workspace;

using RuAgent.Protocol.Harness;
namespace RuBackend.Harness;

public sealed record RunWorkspaceIdentityAdmission(
    WorkspaceIdentity Identity,
    string CanonicalRoot,
    WorkspaceTrustRequest PendingTrustRequest);

public sealed record RunWorkspaceAdmission(
    WorkspaceIdentity Identity,
    string CanonicalRoot,
    WorkspaceTrustRequest PendingTrustRequest,
    bool IsTrusted,
    RunId RunId,
    DateTimeOffset CreatedAt,
    PolicySnapshot PolicySnapshot);

/// <summary>
/// Owns the workspace identity/trust/policy admission sequence for a new run.
/// It prevents run creation from stitching together a caller path, a stale trust
/// registration, or a policy snapshot that no longer matches the canonical root.
/// </summary>
public sealed class AgentRunWorkspaceAdmissionService
{
    private readonly WindowsWorkspaceIdentityResolver _resolver;
    private readonly WorkspaceTrustService _trustService;
    private readonly SqliteWorkspaceStore _workspaceStore;
    private readonly WorkspaceCoordinatorStore _coordinatorStore;
    private readonly HarnessPolicySnapshotService _policySnapshots;
    private readonly IIdGenerator _ids;
    private readonly ITimeProvider _time;
    private readonly ILogger<AgentRunWorkspaceAdmissionService>? _logger;

    public AgentRunWorkspaceAdmissionService(
        WindowsWorkspaceIdentityResolver resolver,
        WorkspaceTrustService trustService,
        SqliteWorkspaceStore workspaceStore,
        WorkspaceCoordinatorStore coordinatorStore,
        HarnessPolicySnapshotService policySnapshots,
        IIdGenerator ids,
        ITimeProvider time,
        ILogger<AgentRunWorkspaceAdmissionService>? logger = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _trustService = trustService ?? throw new ArgumentNullException(nameof(trustService));
        _workspaceStore = workspaceStore ?? throw new ArgumentNullException(nameof(workspaceStore));
        _coordinatorStore = coordinatorStore ?? throw new ArgumentNullException(nameof(coordinatorStore));
        _policySnapshots = policySnapshots ?? throw new ArgumentNullException(nameof(policySnapshots));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger;
    }

    public async Task<(RunWorkspaceIdentityAdmission? Admission, AgentServiceResult? Error)> ResolveAndRegisterAsync(
        string workspacePath,
        CancellationToken cancellationToken)
    {
        WorkspaceIdentity identity;
        try
        {
            identity = await _resolver.ResolveAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            _logger?.LogWarning(error, "workspace path could not be resolved as a workspace root");
            return (null, AgentV1Shared.BoundedError(400, "invalid_workspace", "workspace_path invalid."));
        }

        WorkspaceTrustRequest trustRequest;
        string canonicalRoot;
        try
        {
            trustRequest = await _trustService.EnsureTrustRequestAsync(workspacePath, cancellationToken)
                .ConfigureAwait(false);
            canonicalRoot = WindowsWorkspaceIdentityResolver.GetCanonicalRoot(workspacePath);
            var storedWorkspace = await _workspaceStore.GetAsync(identity.WorkspaceId, cancellationToken)
                .ConfigureAwait(false);
            if (storedWorkspace is not null)
            {
                identity = storedWorkspace.Identity;
                trustRequest = storedWorkspace.TrustRequest;
            }

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            _logger?.LogWarning(error, "workspace trust registration failed");
            return (null, AgentV1Shared.BoundedError(400, "invalid_workspace", "workspace registration failed."));
        }

        try
        {
            var finalIdentity = await _resolver.ResolveAsync(canonicalRoot, cancellationToken).ConfigureAwait(false);
            if (finalIdentity != identity)
            {
                return (null, AgentV1Shared.BoundedError(409, "workspace_changed", "Workspace identity changed during registration."));
            }

            return (new RunWorkspaceIdentityAdmission(identity, canonicalRoot, trustRequest), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            _logger?.LogWarning(error, "workspace identity changed during registration");
            return (null, AgentV1Shared.BoundedError(409, "workspace_changed", "Workspace identity changed during registration."));
        }
    }

    public async Task<(RunWorkspaceAdmission? Admission, AgentServiceResult? Error)> AdmitRunAsync(
        RunWorkspaceIdentityAdmission registration,
        AgentPermissionPreset preset,
        CancellationToken cancellationToken)
    {
        var isTrusted = await _workspaceStore.IsTrustedAsync(registration.Identity, preset, cancellationToken).ConfigureAwait(false);
        try
        {
            await _coordinatorStore.GetOrCreateAsync(registration.Identity.WorkspaceId, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessStoreException error)
        {
            return (null, AgentV1Shared.BoundedError(409,
                error.Code ?? "workspace_coordinator_unavailable", "Workspace coordination is unavailable."));
        }

        try
        {
            var createdAt = _time.UtcNow;
            var runId = _ids.NewRunId();
            var snapshot = await _policySnapshots.LoadAsync(registration.Identity, preset, runId, cancellationToken)
                .ConfigureAwait(false);
            return (new RunWorkspaceAdmission(registration.Identity, registration.CanonicalRoot,
                registration.PendingTrustRequest, isTrusted, runId, createdAt, snapshot), null);
        }
        catch (HarnessStoreException error)
        {
            return (null, AgentV1Shared.BoundedError(409,
                error.Code ?? "policy_snapshot_invalid", "Policy snapshot could not be loaded."));
        }
    }
}
