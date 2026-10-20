using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Protocol.Harness;

// Task 2: canonical root is stored only as encrypted payload; volume/root file identity and display hash are plaintext queryable.
// One active trusted identity per workspace ID is enforced via workspace_id PK; moved identity creates fresh trust request.

namespace RuAgent.Harness.Persistence.Workspace;

public sealed record WorkspaceRootCiphertext
{
    public WorkspaceRootCiphertext(
        ReadOnlyMemory<byte> nonce,
        ReadOnlyMemory<byte> ciphertext,
        ReadOnlyMemory<byte> authenticationTag)
    {
        if (nonce.Length != 12)
        {
            throw new ArgumentException("Workspace root nonce must contain 12 bytes.", nameof(nonce));
        }

        if (ciphertext.Length is < 1 or > 1024 * 1024)
        {
            throw new ArgumentException("Workspace root ciphertext must be bounded.", nameof(ciphertext));
        }

        if (authenticationTag.Length != 16)
        {
            throw new ArgumentException(
                "Workspace root authentication tag must contain 16 bytes.",
                nameof(authenticationTag));
        }

        Nonce = nonce.ToArray();
        Ciphertext = ciphertext.ToArray();
        AuthenticationTag = authenticationTag.ToArray();
    }

    public ReadOnlyMemory<byte> Nonce { get; }

    public ReadOnlyMemory<byte> Ciphertext { get; }

    public ReadOnlyMemory<byte> AuthenticationTag { get; }
}

public sealed record WorkspaceRegistration
{
    public WorkspaceRegistration(
        WorkspaceIdentity identity,
        WorkspaceRootCiphertext encryptedCanonicalRoot)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(encryptedCanonicalRoot);
        Identity = identity;
        EncryptedCanonicalRoot = encryptedCanonicalRoot;
    }

    public WorkspaceIdentity Identity { get; }

    public WorkspaceRootCiphertext EncryptedCanonicalRoot { get; }
}

public sealed record StoredWorkspace(
    WorkspaceIdentity Identity,
    WorkspaceRootCiphertext EncryptedCanonicalRoot,
    WorkspaceTrustRequest TrustRequest,
    bool IsTrusted,
    WorkspaceTrustSource? TrustSource,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed class SqliteWorkspaceStore
{
    private const int CurrentTrustProfileVersion = 1;
    private const string ExplicitTrustSourceWireValue = "explicit";

    private readonly HarnessSqliteOptions _options;

    public SqliteWorkspaceStore(HarnessSqliteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public async Task<StoredWorkspace> RegisterAsync(
        WorkspaceRegistration registration,
        WorkspaceTrustRequest proposedRequest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(proposedRequest);
        if (registration.Identity != proposedRequest.Workspace
            || proposedRequest.AppliedDecision is not null)
        {
            throw new ArgumentException(
                "A registration requires a fresh request for the exact workspace identity.",
                nameof(proposedRequest));
        }

        await using var connection = await _options.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            var existing = await LoadAsync(
                    connection,
                    transaction,
                    registration.Identity.WorkspaceId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null
                && existing.Identity == registration.Identity
                && (existing.IsTrusted
                    || (existing.TrustRequest.AppliedDecision is null
                        && existing.TrustRequest.ExpiresAt > proposedRequest.CreatedAt)))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return existing;
            }

            if (existing is null)
            {
                await InsertAsync(
                        connection,
                        transaction,
                        registration,
                        proposedRequest,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await ReplaceIdentityOrTrustRequestAsync(
                        connection,
                        transaction,
                        registration,
                        proposedRequest,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new StoredWorkspace(
                registration.Identity,
                registration.EncryptedCanonicalRoot,
                proposedRequest,
                IsTrusted: false,
                TrustSource: null,
                existing?.CreatedAt ?? proposedRequest.CreatedAt,
                proposedRequest.CreatedAt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            throw new HarnessStoreException("workspace_store_failed", exception);
        }
    }

    public async Task<StoredWorkspace?> GetAsync(
        WorkspaceId workspaceId,
        CancellationToken cancellationToken)
    {
        _ = workspaceId.Value;
        await using var connection = await _options.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await LoadAsync(connection, null, workspaceId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> IsTrustedAsync(
        WorkspaceIdentity identity,
        AgentPermissionPreset preset,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!Enum.IsDefined(preset))
        {
            throw new ArgumentOutOfRangeException(nameof(preset));
        }

        var stored = await GetAsync(identity.WorkspaceId, cancellationToken).ConfigureAwait(false);
        return stored is not null
            && stored.Identity == identity
            && stored.TrustRequest.AppliedDecision == WorkspaceTrustDecision.Approve
            && stored.TrustSource == WorkspaceTrustSource.Explicit;
    }

    internal static async Task<StoredWorkspace?> LoadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        WorkspaceId workspaceId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT canonical_root_nonce,
                   canonical_root_ciphertext,
                   canonical_root_tag,
                   volume_serial_number,
                   root_file_id,
                   display_identity_hash,
                   trust_request_id,
                   trust_requested_at_utc,
                   trust_expires_at_utc,
                   trust_decision,
                   trust_decided_at_utc,
                   trust_source,
                   trust_profile_version,
                   created_at_utc,
                   updated_at_utc
            FROM agent_workspaces
            WHERE workspace_id = $workspace_id
            """;
        command.Parameters.AddWithValue("$workspace_id", workspaceId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var identity = new WorkspaceIdentity(
            workspaceId,
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5));
        var root = new WorkspaceRootCiphertext(
            HarnessSqliteMapper.ReadBoundedBlob(reader, 0, 12, "workspace_store_corrupt", 12),
            HarnessSqliteMapper.ReadBoundedBlob(reader, 1, 1024 * 1024, "workspace_store_corrupt"),
            HarnessSqliteMapper.ReadBoundedBlob(reader, 2, 16, "workspace_store_corrupt", 16));
        var requestedAt = ParseUtc(reader.GetString(7));
        var expiresAt = ParseUtc(reader.GetString(8));
        var request = new WorkspaceTrustRequest(
            new TrustRequestId(reader.GetString(6)),
            identity,
            requestedAt,
            expiresAt);
        if (!reader.IsDBNull(9))
        {
            var response = new WorkspaceTrustResponse(
                request.RequestId,
                identity,
                identity.DisplayIdentityHash,
                (WorkspaceTrustDecision)reader.GetInt32(9));
            var decidedAt = ParseUtc(reader.GetString(10));
            var application = request.TryApply(response, identity, decidedAt);
            if (!application.Accepted)
            {
                throw new HarnessStoreException("workspace_trust_corrupt");
            }

            request = application.Next;
        }

        WorkspaceTrustSource? trustSource = reader.IsDBNull(11)
            ? null
            : ParseTrustSource(reader.GetString(11));
        if (reader.GetInt32(12) != CurrentTrustProfileVersion)
        {
            throw new HarnessStoreException("workspace_trust_corrupt");
        }

        return new StoredWorkspace(
            identity,
            root,
            request,
            request.AppliedDecision == WorkspaceTrustDecision.Approve
                && trustSource == WorkspaceTrustSource.Explicit,
            trustSource,
            ParseUtc(reader.GetString(13)),
            ParseUtc(reader.GetString(14)));
    }

    private static async Task InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        WorkspaceRegistration registration,
        WorkspaceTrustRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO agent_workspaces(
                workspace_id,
                canonical_root_nonce,
                canonical_root_ciphertext,
                canonical_root_tag,
                volume_serial_number,
                root_file_id,
                display_identity_hash,
                trust_request_id,
                trust_requested_at_utc,
                trust_expires_at_utc,
                trust_source,
                trust_profile_version,
                created_at_utc,
                updated_at_utc)
            VALUES (
                $workspace_id,
                $root_nonce,
                $root_ciphertext,
                $root_tag,
                $volume_serial,
                $root_file_id,
                $display_hash,
                $trust_request_id,
                $requested_at,
                $expires_at,
                NULL,
                $trust_profile_version,
                $created_at,
                $updated_at)
            """;
        AddRegistrationParameters(command, registration, request);
        command.Parameters.AddWithValue("$created_at", request.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", request.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReplaceIdentityOrTrustRequestAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        WorkspaceRegistration registration,
        WorkspaceTrustRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE agent_workspaces
            SET canonical_root_nonce = $root_nonce,
                canonical_root_ciphertext = $root_ciphertext,
                canonical_root_tag = $root_tag,
                volume_serial_number = $volume_serial,
                root_file_id = $root_file_id,
                display_identity_hash = $display_hash,
                trust_request_id = $trust_request_id,
                trust_requested_at_utc = $requested_at,
                trust_expires_at_utc = $expires_at,
                trust_decision = NULL,
                trust_decided_at_utc = NULL,
                trust_source = NULL,
                trust_profile_version = $trust_profile_version,
                updated_at_utc = $updated_at
            WHERE workspace_id = $workspace_id
            """;
        AddRegistrationParameters(command, registration, request);
        command.Parameters.AddWithValue("$updated_at", request.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddRegistrationParameters(
        SqliteCommand command,
        WorkspaceRegistration registration,
        WorkspaceTrustRequest request)
    {
        command.Parameters.AddWithValue("$workspace_id", registration.Identity.WorkspaceId.Value);
        command.Parameters.AddWithValue("$root_nonce", registration.EncryptedCanonicalRoot.Nonce.ToArray());
        command.Parameters.AddWithValue(
            "$root_ciphertext",
            registration.EncryptedCanonicalRoot.Ciphertext.ToArray());
        command.Parameters.AddWithValue(
            "$root_tag",
            registration.EncryptedCanonicalRoot.AuthenticationTag.ToArray());
        command.Parameters.AddWithValue("$volume_serial", registration.Identity.VolumeSerialNumber);
        command.Parameters.AddWithValue("$root_file_id", registration.Identity.RootFileId);
        command.Parameters.AddWithValue("$display_hash", registration.Identity.DisplayIdentityHash);
        command.Parameters.AddWithValue("$trust_request_id", request.RequestId.Value);
        command.Parameters.AddWithValue("$requested_at", request.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$expires_at", request.ExpiresAt.ToString("O"));
        command.Parameters.AddWithValue(
            "$trust_profile_version",
            CurrentTrustProfileVersion);
    }

    private static WorkspaceTrustSource ParseTrustSource(string value) => value switch
    {
        ExplicitTrustSourceWireValue => WorkspaceTrustSource.Explicit,
        _ => throw new HarnessStoreException("workspace_trust_corrupt"),
    };

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);
}
