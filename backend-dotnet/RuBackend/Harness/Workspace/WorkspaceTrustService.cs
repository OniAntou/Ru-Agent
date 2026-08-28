using System.Security.Cryptography;
using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Privacy;
using RuAgent.Harness.Persistence.Workspace;

namespace RuBackend.Harness.Workspace;

public sealed class WorkspaceTrustService
{
    private readonly WindowsWorkspaceIdentityResolver _resolver;
    private readonly SqliteWorkspaceStore _store;
    private readonly WorkspaceTrustRequestStore _trustStore;
    private readonly IHarnessKeyStore? _keyStore;

    private const string RootAssociatedDataPrefix = "ru-workspace-root-v1:";

    public WorkspaceTrustService(
        WindowsWorkspaceIdentityResolver resolver,
        SqliteWorkspaceStore store,
        WorkspaceTrustRequestStore trustStore,
        IHarnessKeyStore? keyStore = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _trustStore = trustStore ?? throw new ArgumentNullException(nameof(trustStore));
        _keyStore = keyStore;
    }

    public Task<WorkspaceIdentity> ResolveAsync(string workspacePath, CancellationToken cancellationToken = default)
        => _resolver.ResolveAsync(workspacePath, cancellationToken);

    public async Task<WorkspaceTrustRequest> EnsureTrustRequestAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        var identity = await _resolver.ResolveAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        var existing = await _store.GetAsync(identity.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (existing is not null
            && existing.Identity == identity
            && existing.EncryptedCanonicalRoot.Ciphertext.Length >= 4
            && existing.TrustRequest.AppliedDecision is null
            && existing.TrustRequest.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return existing.TrustRequest;
        }

        // Need to create or replace with fresh request
        var hex = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var requestId = new TrustRequestId($"trust_{hex}");

        var now = DateTimeOffset.UtcNow;
        var request = new WorkspaceTrustRequest(requestId, identity, now, now.AddMinutes(10));

        var canonicalRoot = WindowsWorkspaceIdentityResolver.GetCanonicalRoot(workspacePath);
        var encrypted = await EncryptCanonicalRootAsync(
            identity,
            canonicalRoot,
            cancellationToken).ConfigureAwait(false);
        var registration = new WorkspaceRegistration(identity, encrypted);
        var stored = await _store.RegisterAsync(registration, request, cancellationToken).ConfigureAwait(false);
        return stored.TrustRequest;
    }

    /// <summary>
    /// Decrypts and revalidates a persisted workspace root for process restart recovery.
    /// A root is accepted only when it still resolves to the exact persisted identity.
    /// </summary>
    public async Task<string> DecryptCanonicalRootAsync(
        StoredWorkspace stored,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var keyStore = _keyStore ?? throw new HarnessStoreException("workspace_root_key_unavailable");
        var key = await keyStore.GetOrCreateKeyAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (key.Length != 32)
            {
                throw new HarnessStoreException("harness_key_invalid");
            }

            var plaintext = new byte[stored.EncryptedCanonicalRoot.Ciphertext.Length];
            using var aes = new AesGcm(key, stored.EncryptedCanonicalRoot.AuthenticationTag.Length);
            aes.Decrypt(
                stored.EncryptedCanonicalRoot.Nonce.Span,
                stored.EncryptedCanonicalRoot.Ciphertext.Span,
                stored.EncryptedCanonicalRoot.AuthenticationTag.Span,
                plaintext,
                AssociatedData(stored.Identity));
            var root = Encoding.UTF8.GetString(plaintext);
            var canonical = WindowsWorkspaceIdentityResolver.GetCanonicalRoot(root);
            var current = await _resolver.ResolveAsync(canonical, cancellationToken).ConfigureAwait(false);
            if (current != stored.Identity)
            {
                throw new HarnessStoreException("workspace_identity_changed");
            }

            return canonical;
        }
        catch (CryptographicException exception)
        {
            _ = exception;
            throw new HarnessStoreException("workspace_root_decrypt_failed");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private async Task<WorkspaceRootCiphertext> EncryptCanonicalRootAsync(
        WorkspaceIdentity identity,
        string canonicalRoot,
        CancellationToken cancellationToken)
    {
        var keyStore = _keyStore ?? throw new HarnessStoreException("workspace_root_key_unavailable");
        var key = await keyStore.GetOrCreateKeyAsync(cancellationToken).ConfigureAwait(false);
        var plaintext = Encoding.UTF8.GetBytes(canonicalRoot);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        try
        {
            if (key.Length != 32)
            {
                throw new HarnessStoreException("harness_key_invalid");
            }

            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(identity));
            return new WorkspaceRootCiphertext(nonce, ciphertext, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] AssociatedData(WorkspaceIdentity identity) =>
        Encoding.UTF8.GetBytes(RootAssociatedDataPrefix + identity.WorkspaceId.Value);

    public Task<bool> IsTrustedAsync(WorkspaceIdentity identity, AgentPermissionPreset preset, CancellationToken cancellationToken = default)
        => _store.IsTrustedAsync(identity, preset, cancellationToken);

    public Task<WorkspaceTrustStoreResult> DecideAsync(
        WorkspaceTrustResponse response,
        WorkspaceIdentity currentIdentity,
        DateTimeOffset decidedAt,
        CancellationToken cancellationToken = default)
        => _trustStore.ApplyAsync(response, currentIdentity, decidedAt, cancellationToken);
}
