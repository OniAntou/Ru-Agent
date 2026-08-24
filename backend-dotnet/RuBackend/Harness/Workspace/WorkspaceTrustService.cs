using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Harness.Persistence.Workspace;

namespace RuBackend.Harness.Workspace;

public sealed class WorkspaceTrustService
{
    private readonly WindowsWorkspaceIdentityResolver _resolver;
    private readonly SqliteWorkspaceStore _store;
    private readonly WorkspaceTrustRequestStore _trustStore;

    public WorkspaceTrustService(
        WindowsWorkspaceIdentityResolver resolver,
        SqliteWorkspaceStore store,
        WorkspaceTrustRequestStore trustStore)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _trustStore = trustStore ?? throw new ArgumentNullException(nameof(trustStore));
    }

    public Task<WorkspaceIdentity> ResolveAsync(string workspacePath, CancellationToken cancellationToken = default)
        => _resolver.ResolveAsync(workspacePath, cancellationToken);

    public async Task<WorkspaceTrustRequest> EnsureTrustRequestAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        var identity = await _resolver.ResolveAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        var existing = await _store.GetAsync(identity.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (existing is not null && existing.Identity == identity && existing.TrustRequest.AppliedDecision is null && existing.TrustRequest.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return existing.TrustRequest;
        }

        // Need to create or replace with fresh request
        var hex = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var requestId = new TrustRequestId($"trust_{hex}");

        var now = DateTimeOffset.UtcNow;
        var request = new WorkspaceTrustRequest(requestId, identity, now, now.AddMinutes(10));

        // Create registration with encrypted root placeholder (actual encryption is done elsewhere; for tracer we use dummy)
        var encrypted = new WorkspaceRootCiphertext(new byte[12], new byte[] { 1, 2, 3 }, new byte[16]);
        var registration = new WorkspaceRegistration(identity, encrypted);
        var stored = await _store.RegisterAsync(registration, request, cancellationToken).ConfigureAwait(false);
        return stored.TrustRequest;
    }

    public Task<bool> IsTrustedAsync(WorkspaceIdentity identity, AgentPermissionPreset preset, CancellationToken cancellationToken = default)
        => _store.IsTrustedAsync(identity, preset, cancellationToken);

    public Task<WorkspaceTrustStoreResult> DecideAsync(
        WorkspaceTrustResponse response,
        WorkspaceIdentity currentIdentity,
        DateTimeOffset decidedAt,
        CancellationToken cancellationToken = default)
        => _trustStore.ApplyAsync(response, currentIdentity, decidedAt, cancellationToken);
}
