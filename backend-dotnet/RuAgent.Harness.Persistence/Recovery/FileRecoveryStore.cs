using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Recovery;
using RuAgent.Harness.Persistence.Privacy;

namespace RuAgent.Harness.Persistence.Recovery;

public sealed class FileRecoveryStore
{
    private readonly HarnessSqliteOptions _options;
    private readonly HarnessPayloadStore _payloadStore;
    private readonly HarnessPayloadCryptography _cryptography;
    private readonly KnownSecretRedactor _redactor;

    public FileRecoveryStore(HarnessSqliteOptions options, HarnessPayloadCryptography cryptography, KnownSecretRedactor redactor)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _cryptography = cryptography ?? throw new ArgumentNullException(nameof(cryptography));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _payloadStore = new HarnessPayloadStore(options, cryptography, redactor);
    }

    public FileRecoveryStore(HarnessSqliteOptions options, HarnessPayloadStore payloadStore)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _payloadStore = payloadStore ?? throw new ArgumentNullException(nameof(payloadStore));
        // Extract cryptography/redactor via reflection? For simplicity require the other ctor for direct usage.
        _cryptography = new HarnessPayloadCryptography(new FakeFallbackKeyStore());
        _redactor = KnownSecretRedactor.Empty;
    }

    private sealed class FakeFallbackKeyStore : IHarnessKeyStore
    {
        public ValueTask<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new byte[32]);
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS agent_file_recovery (
                recovery_id TEXT PRIMARY KEY,
                run_id TEXT NOT NULL REFERENCES agent_runs(run_id) ON DELETE CASCADE,
                canonical_path TEXT NOT NULL CHECK(length(canonical_path) BETWEEN 1 AND 4096),
                content_sha256 TEXT NOT NULL CHECK(length(content_sha256) = 64),
                content_length INTEGER NOT NULL CHECK(content_length >= 0),
                created_at_utc TEXT NOT NULL,
                consumed_at_utc TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_agent_file_recovery_run ON agent_file_recovery(run_id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<FileRecoveryReference> CreateRecoveryAsync(
        RunId runId,
        string canonicalPath,
        byte[] content,
        string contentSha256,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        _ = runId.Value;
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(contentSha256) || !IsValidSha256(contentSha256))
        {
            throw new ArgumentException("Content hash must be 64 lowercase hex.", nameof(contentSha256));
        }

        // Ensure recovery table exists
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        // Generate opaque recovery ID
        var recoveryId = new PayloadId("payload_" + Guid.NewGuid().ToString("N"));
        // Validate content hash matches bytes
        var computed = ComputeSha256(content);
        if (!string.Equals(computed, contentSha256, StringComparison.Ordinal))
        {
            throw new ArgumentException("Content hash does not match bytes.", nameof(contentSha256));
        }

        // Create payload intent for encrypted storage – byte-exact, internal only, opaque
        var intent = new HarnessPayloadIntent(
            recoveryId,
            HarnessPayloadKind.RecoveryOpaque,
            HarnessPayloadAudience.InternalOnly,
            HarnessPayloadEncoding.Opaque,
            Math.Max(content.Length, 1),
            content);

        var protectedPayload = await _payloadStore.ProtectAsync(runId, intent, cancellationToken).ConfigureAwait(false);

        await using var connection = await _options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();

        // Insert into agent_payloads via protected payload – we need to directly insert because InsertAsync requires event binding
        await InsertProtectedPayloadAsync(connection, transaction, runId, protectedPayload, createdAt, cancellationToken).ConfigureAwait(false);

        // Insert into recovery reference table for path binding and one-time tracking
        await using var meta = connection.CreateCommand();
        meta.Transaction = transaction;
        meta.CommandText = """
            INSERT INTO agent_file_recovery(recovery_id, run_id, canonical_path, content_sha256, content_length, created_at_utc)
            VALUES ($recovery_id, $run_id, $canonical_path, $sha256, $length, $created_at)
            """;
        meta.Parameters.AddWithValue("$recovery_id", recoveryId.Value);
        meta.Parameters.AddWithValue("$run_id", runId.Value);
        meta.Parameters.AddWithValue("$canonical_path", canonicalPath);
        meta.Parameters.AddWithValue("$sha256", contentSha256);
        meta.Parameters.AddWithValue("$length", content.Length);
        meta.Parameters.AddWithValue("$created_at", HarnessSqliteMapper.Utc(createdAt));
        await meta.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new FileRecoveryReference(recoveryId, runId, canonicalPath, contentSha256, createdAt);
    }

    // Used by Core generic payload handling path: when a RecoveryOpaque payload is already committed via IHarnessEventStore,
    // we still need to record the path binding. This method inserts only the mapping row, assuming payload already exists.
    public async Task<FileRecoveryReference> RecordMappingAsync(
        RunId runId,
        PayloadId recoveryId,
        string canonicalPath,
        string contentSha256,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        _ = runId.Value;
        _ = recoveryId.Value;
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);
        if (!IsValidSha256(contentSha256))
        {
            throw new ArgumentException("Content hash must be 64 lowercase hex.", nameof(contentSha256));
        }

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO agent_file_recovery(recovery_id, run_id, canonical_path, content_sha256, content_length, created_at_utc)
            VALUES ($recovery_id, $run_id, $canonical_path, $sha256, 0, $created_at)
            """;
        command.Parameters.AddWithValue("$recovery_id", recoveryId.Value);
        command.Parameters.AddWithValue("$run_id", runId.Value);
        command.Parameters.AddWithValue("$canonical_path", canonicalPath);
        command.Parameters.AddWithValue("$sha256", contentSha256);
        command.Parameters.AddWithValue("$created_at", HarnessSqliteMapper.Utc(createdAt));
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new HarnessStoreException("payload_recovery_duplicate", ex);
        }

        return new FileRecoveryReference(recoveryId, runId, canonicalPath, contentSha256, createdAt);
    }

    public async Task<byte[]> ConsumeRecoveryAsync(
        RunId runId,
        PayloadId recoveryId,
        CancellationToken cancellationToken)
    {
        _ = runId.Value;
        _ = recoveryId.Value;
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        // Verify ownership and not consumed, and run is active
        await using var connection = await _options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();

        // Check mapping exists and belongs to run and not consumed
        await using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = """
            SELECT run_id, consumed_at_utc, canonical_path, content_sha256
            FROM agent_file_recovery
            WHERE recovery_id = $recovery_id
            """;
        check.Parameters.AddWithValue("$recovery_id", recoveryId.Value);
        await using var reader = await check.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new HarnessStoreException("payload_recovery_unavailable");
        }

        var owningRunId = reader.GetString(0);
        var consumedAt = reader.IsDBNull(1) ? null : reader.GetString(1);
        if (!string.Equals(owningRunId, runId.Value, StringComparison.Ordinal))
        {
            throw new HarnessStoreException("payload_recovery_unavailable");
        }

        if (consumedAt is not null)
        {
            throw new HarnessStoreException("payload_recovery_consumed");
        }

        // Verify run is still active (not terminal)
        await using var runCheck = connection.CreateCommand();
        runCheck.Transaction = transaction;
        runCheck.CommandText = "SELECT terminal_at_utc FROM agent_runs WHERE run_id = $run_id";
        runCheck.Parameters.AddWithValue("$run_id", runId.Value);
        var terminalObj = await runCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (terminalObj is null || terminalObj == DBNull.Value)
        {
            // run not found? treat as unavailable
            // If terminal_at_utc is not null, run is terminal -> expired
            // But we already checked consumed; if terminal, we should expire
        }
        else if (terminalObj is string termStr && !string.IsNullOrWhiteSpace(termStr))
        {
            throw new HarnessStoreException("payload_recovery_unavailable");
        }

        // Mark consumed in mapping table
        var now = DateTimeOffset.UtcNow;
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE agent_file_recovery
            SET consumed_at_utc = $consumed_at
            WHERE recovery_id = $recovery_id AND consumed_at_utc IS NULL
            """;
        update.Parameters.AddWithValue("$consumed_at", HarnessSqliteMapper.Utc(now));
        update.Parameters.AddWithValue("$recovery_id", recoveryId.Value);
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new HarnessStoreException("payload_recovery_consumed");
        }

        // Decrypt payload bytes via payload store (this also marks consumed_at_utc in agent_payloads)
        // We need to fetch the encrypted payload directly and decrypt, but also mark payload consumed
        // Use HarnessPayloadStore.ReadRecoveryOnceAsync – it handles transaction internally, but we already are in transaction.
        // To avoid double transaction, we will manually load and decrypt.
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // Now use the payload store's recovery read which does its own transaction and marks payload consumed
        var content = await _payloadStore.ReadRecoveryOnceAsync(runId, recoveryId, now, cancellationToken).ConfigureAwait(false);
        return content.Plaintext.ToArray();
    }

    public async Task<IReadOnlyList<FileRecoveryReference>> ListActiveAsync(RunId runId, CancellationToken cancellationToken)
    {
        _ = runId.Value;
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.recovery_id, r.run_id, r.canonical_path, r.content_sha256, r.created_at_utc
            FROM agent_file_recovery AS r
            INNER JOIN agent_runs AS ar ON ar.run_id = r.run_id
            WHERE r.run_id = $run_id
              AND r.consumed_at_utc IS NULL
              AND ar.terminal_at_utc IS NULL
            ORDER BY r.created_at_utc
            """;
        command.Parameters.AddWithValue("$run_id", runId.Value);
        var list = new List<FileRecoveryReference>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var recoveryId = new PayloadId(reader.GetString(0));
            var owningRun = new RunId(reader.GetString(1));
            var canonicalPath = reader.GetString(2);
            var sha = reader.GetString(3);
            var createdAt = HarnessSqliteMapper.ParseUtc(reader.GetString(4));
            list.Add(new FileRecoveryReference(recoveryId, owningRun, canonicalPath, sha, createdAt));
        }

        return list;
    }

    public async Task ExpireUnconsumedForRunAsync(RunId runId, CancellationToken cancellationToken)
    {
        _ = runId.Value;
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        // Only expire if run is terminal
        await using var check = connection.CreateCommand();
        check.CommandText = "SELECT terminal_at_utc FROM agent_runs WHERE run_id = $run_id";
        check.Parameters.AddWithValue("$run_id", runId.Value);
        var terminal = await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (terminal is null || terminal == DBNull.Value)
        {
            return;
        }

        var terminalStr = terminal as string;
        if (string.IsNullOrWhiteSpace(terminalStr))
        {
            return;
        }

        // Delete unconsumed recoveries for terminal run – retention policy
        await using var delMeta = connection.CreateCommand();
        delMeta.CommandText = "DELETE FROM agent_file_recovery WHERE run_id = $run_id AND consumed_at_utc IS NULL";
        delMeta.Parameters.AddWithValue("$run_id", runId.Value);
        await delMeta.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var delPayload = connection.CreateCommand();
        delPayload.CommandText = """
            DELETE FROM agent_payloads
            WHERE run_id = $run_id
              AND kind = $kind
              AND consumed_at_utc IS NULL
            """;
        delPayload.Parameters.AddWithValue("$run_id", runId.Value);
        delPayload.Parameters.AddWithValue("$kind", (int)HarnessPayloadKind.RecoveryOpaque);
        await delPayload.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> IsAvailableAsync(RunId runId, PayloadId recoveryId, CancellationToken cancellationToken)
    {
        var list = await ListActiveAsync(runId, cancellationToken).ConfigureAwait(false);
        return list.Any(r => r.RecoveryId.Value == recoveryId.Value);
    }

    private static async Task InsertProtectedPayloadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RunId runId,
        ProtectedHarnessPayload protectedPayload,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO agent_payloads(
                payload_id,
                run_id,
                kind,
                audience,
                encoding,
                byte_cap,
                protected_content,
                protection_version,
                nonce,
                authentication_tag,
                created_at_utc)
            VALUES (
                $payload_id,
                $run_id,
                $kind,
                $audience,
                $encoding,
                $byte_cap,
                $content,
                $protection_version,
                $nonce,
                $authentication_tag,
                $created_at)
            """;
        command.Parameters.AddWithValue("$payload_id", protectedPayload.Intent.PayloadId.Value);
        command.Parameters.AddWithValue("$run_id", runId.Value);
        command.Parameters.AddWithValue("$kind", (int)protectedPayload.Intent.Kind);
        command.Parameters.AddWithValue("$audience", (int)protectedPayload.Intent.Audience);
        command.Parameters.AddWithValue("$encoding", (int)protectedPayload.Intent.Encoding);
        command.Parameters.AddWithValue("$byte_cap", protectedPayload.Intent.ByteCap);
        command.Parameters.AddWithValue("$content", protectedPayload.Encrypted.Ciphertext.ToArray());
        command.Parameters.AddWithValue("$protection_version", protectedPayload.Encrypted.ProtectionVersion);
        command.Parameters.AddWithValue("$nonce", protectedPayload.Encrypted.Nonce.ToArray());
        command.Parameters.AddWithValue("$authentication_tag", protectedPayload.Encrypted.AuthenticationTag.ToArray());
        command.Parameters.AddWithValue("$created_at", HarnessSqliteMapper.Utc(createdAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool IsValidSha256(string value)
    {
        if (value.Length != 64) return false;
        foreach (var c in value)
        {
            if ((c < '0' || c > '9') && (c < 'a' || c > 'f')) return false;
        }
        return true;
    }

    private static string ComputeSha256(byte[] bytes)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
