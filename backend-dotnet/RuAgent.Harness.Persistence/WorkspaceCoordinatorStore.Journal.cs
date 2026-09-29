using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;

namespace RuAgent.Harness.Persistence;

public sealed partial class WorkspaceCoordinatorStore
{
    public async Task<long> AllocateCommitSequenceAsync(
        WorkspaceId workspaceId,
        CancellationToken cancellationToken)
    {
        _ = workspaceId.Value;
        await using var connection = await _options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE workspace_coordinators
                SET commit_sequence = commit_sequence + 1,
                    version = version + 1,
                    updated_at_utc = $updated_at
                WHERE workspace_id = $workspace_id
                RETURNING commit_sequence
                """;
            command.Parameters.AddWithValue("$workspace_id", workspaceId.Value);
            command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is null)
            {
                throw new HarnessStoreException("workspace_not_found");
            }

            var sequence = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return sequence;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HarnessStoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            throw new HarnessStoreException("workspace_sequence_allocation_failed", exception);
        }
    }

    public async Task<IReadOnlyList<WorkspaceChangeRecord>> ListChangesAsync(
        WorkspaceId workspaceId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken)
    {
        _ = workspaceId.Value;
        if (afterSequence < 0 || limit is < 1 or > 200)
        {
            throw new HarnessStoreException("invalid_workspace_changes_query");
        }

        await using var connection = await _options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT change_id, workspace_id, run_id, commit_sequence, normalized_path,
                   before_sha256, after_sha256, status, created_at_utc
            FROM workspace_change_journal
            WHERE workspace_id = $workspace_id
              AND commit_sequence > $after_sequence
            ORDER BY commit_sequence, created_at_utc, change_id
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$workspace_id", workspaceId.Value);
        command.Parameters.AddWithValue("$after_sequence", afterSequence);
        command.Parameters.AddWithValue("$limit", limit);
        var result = new List<WorkspaceChangeRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new WorkspaceChangeRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt64(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetString(7),
                DateTimeOffset.Parse(reader.GetString(8), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        }

        return result;
    }

    public async Task<WorkspaceChangeEnvelope?> GetChangeEnvelopeAsync(
        WorkspaceId workspaceId,
        string changeId,
        CancellationToken cancellationToken)
    {
        _ = workspaceId.Value;
        if (string.IsNullOrWhiteSpace(changeId) || changeId.Length > 256)
        {
            throw new HarnessStoreException("invalid_workspace_change");
        }

        await using var connection = await _options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await LoadChangeEnvelopeAsync(
                connection,
                transaction: null,
                workspaceId.Value,
                changeId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<WorkspaceChangeEnvelope?> LoadChangeEnvelopeAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string workspaceId,
        string changeId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT change_id, workspace_id, run_id, commit_sequence, normalized_path,
                   before_sha256, after_sha256, protected_payload, nonce,
                   authentication_tag, protection_version, status, created_at_utc
            FROM workspace_change_journal
            WHERE workspace_id = $workspace_id AND change_id = $change_id
            """;
        command.Parameters.AddWithValue("$workspace_id", workspaceId);
        command.Parameters.AddWithValue("$change_id", changeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var change = new WorkspaceChangeRecord(
            reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetInt64(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetString(11),
            DateTimeOffset.Parse(reader.GetString(12), null, System.Globalization.DateTimeStyles.RoundtripKind));

        var nonceLength = reader.GetBytes(8, 0, null, 0, 0);
        var ciphertextLength = reader.GetBytes(7, 0, null, 0, 0);
        var tagLength = reader.GetBytes(9, 0, null, 0, 0);
        if (nonceLength != 12 || tagLength != 16)
        {
            throw new HarnessStoreException("journal_envelope_invalid");
        }

        if (ciphertextLength > WorkspaceJournalProtector.MaximumJournalBytes)
        {
            throw new HarnessStoreException("journal_too_large");
        }

        var nonce = new byte[checked((int)nonceLength)];
        var ciphertext = new byte[checked((int)ciphertextLength)];
        var authenticationTag = new byte[checked((int)tagLength)];
        reader.GetBytes(8, 0, nonce, 0, nonce.Length);
        reader.GetBytes(7, 0, ciphertext, 0, ciphertext.Length);
        reader.GetBytes(9, 0, authenticationTag, 0, authenticationTag.Length);
        return new WorkspaceChangeEnvelope(
            change,
            new EncryptedWorkspaceJournal(
                reader.GetInt32(10),
                nonce,
                ciphertext,
                authenticationTag));
    }

    public async Task<WorkspaceChangeRecord> MarkChangeRestoredAsync(
        WorkspaceId workspaceId,
        string changeId,
        CancellationToken cancellationToken)
    {
        _ = workspaceId.Value;
        if (string.IsNullOrWhiteSpace(changeId) || changeId.Length > 256)
        {
            throw new HarnessStoreException("invalid_workspace_change");
        }

        await using var connection = await _options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE workspace_change_journal
                SET status = 'restored'
                WHERE workspace_id = $workspace_id AND change_id = $change_id
                  AND status IN ('prepared', 'committed')
                """;
            command.Parameters.AddWithValue("$workspace_id", workspaceId.Value);
            command.Parameters.AddWithValue("$change_id", changeId);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new HarnessStoreException("workspace_change_not_found");
            }

            var envelope = await LoadChangeEnvelopeAsync(
                    connection,
                    transaction,
                    workspaceId.Value,
                    changeId,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new HarnessStoreException("workspace_change_not_found");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return envelope.Change;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HarnessStoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            throw new HarnessStoreException("workspace_change_store_failed", exception);
        }
    }

    public async Task<WorkspaceChangeRecord> AppendChangeAsync(
        WorkspaceId workspaceId,
        RunId? runId,
        string changeId,
        string normalizedPath,
        string? beforeSha256,
        string? afterSha256,
        EncryptedWorkspaceJournal encrypted,
        string status,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        _ = workspaceId.Value;
        if (string.IsNullOrWhiteSpace(changeId)
            || changeId.Length > 256
            || string.IsNullOrWhiteSpace(normalizedPath)
            || normalizedPath.Length > 4096
            || Path.IsPathRooted(normalizedPath)
            || normalizedPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or "..")
            || status is not ("prepared" or "committed" or "restored" or "rejected")
            || encrypted.ProtectionVersion != WorkspaceJournalProtector.CurrentProtectionVersion
            || encrypted.Nonce.Length != 12
            || encrypted.AuthenticationTag.Length != 16
            || encrypted.Ciphertext.Length > WorkspaceJournalProtector.MaximumJournalBytes)
        {
            throw new HarnessStoreException(
                encrypted.Ciphertext.Length > WorkspaceJournalProtector.MaximumJournalBytes
                    ? "journal_too_large"
                    : "invalid_workspace_change");
        }

        static bool IsHash(string? value) => value is null || (value.Length == 64 && value.All(Uri.IsHexDigit));
        if (!IsHash(beforeSha256) || !IsHash(afterSha256))
        {
            throw new HarnessStoreException("invalid_workspace_change_hash");
        }

        await using var connection = await _options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            var coordinator = await ReadAsync(connection, transaction, workspaceId.Value, cancellationToken).ConfigureAwait(false)
                ?? throw new HarnessStoreException("workspace_not_found");

            var existingChange = await ReadChangeByIdAsync(connection, transaction, changeId, cancellationToken).ConfigureAwait(false);
            if (existingChange is not null)
            {
                // Idempotent append: the same change id may only replay with the
                // exact same content. Matching just path+workspace let a replay
                // silently return a record whose hashes, run binding, status, or
                // encrypted payload (the undo bytes) belonged to another write.
                var existingEnvelope = await LoadChangeEnvelopeAsync(
                        connection,
                        transaction,
                        workspaceId.Value,
                        changeId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (existingEnvelope is null
                    || !string.Equals(existingChange.WorkspaceId, workspaceId.Value, StringComparison.Ordinal)
                    || !string.Equals(existingChange.NormalizedPath, normalizedPath, StringComparison.Ordinal)
                    || !string.Equals(existingChange.RunId, runId is null ? null : runId.Value.Value, StringComparison.Ordinal)
                    || !string.Equals(existingChange.BeforeSha256, beforeSha256, StringComparison.Ordinal)
                    || !string.Equals(existingChange.AfterSha256, afterSha256, StringComparison.Ordinal)
                    || !string.Equals(existingChange.Status, status, StringComparison.Ordinal)
                    || !MatchesEncrypted(existingEnvelope.Journal, encrypted))
                {
                    throw new HarnessStoreException("workspace_change_id_conflict");
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return existingChange;
            }

            if (runId is not null)
            {
                await using var runCommand = connection.CreateCommand();
                runCommand.Transaction = transaction;
                runCommand.CommandText = "SELECT workspace_id FROM agent_runs WHERE run_id = $run_id";
                runCommand.Parameters.AddWithValue("$run_id", runId.Value.Value);
                var runWorkspace = await runCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
                if (runWorkspace is null)
                {
                    throw new HarnessStoreException("run_not_found");
                }

                if (!string.Equals(runWorkspace, workspaceId.Value, StringComparison.Ordinal))
                {
                    throw new HarnessStoreException("run_workspace_mismatch");
                }
            }

            var sequence = coordinator.CommitSequence + 1;
            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE workspace_coordinators
                    SET commit_sequence = $commit_sequence,
                        version = version + 1,
                        updated_at_utc = $updated_at
                    WHERE workspace_id = $workspace_id
                    """;
                update.Parameters.AddWithValue("$commit_sequence", sequence);
                update.Parameters.AddWithValue("$updated_at", nowUtc.ToString("O"));
                update.Parameters.AddWithValue("$workspace_id", workspaceId.Value);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new HarnessStoreException("workspace_not_found");
                }
            }

            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO workspace_change_journal(
                        change_id, workspace_id, run_id, commit_sequence, normalized_path,
                        before_sha256, after_sha256, protected_payload, nonce,
                        authentication_tag, protection_version, status, created_at_utc)
                    VALUES($change_id, $workspace_id, $run_id, $commit_sequence, $normalized_path,
                           $before_sha256, $after_sha256, $protected_payload, $nonce,
                           $authentication_tag, $protection_version, $status, $created_at_utc)
                    """;
                insert.Parameters.AddWithValue("$change_id", changeId);
                insert.Parameters.AddWithValue("$workspace_id", workspaceId.Value);
                insert.Parameters.AddWithValue("$run_id", runId is null ? DBNull.Value : runId.Value.Value);
                insert.Parameters.AddWithValue("$commit_sequence", sequence);
                insert.Parameters.AddWithValue("$normalized_path", normalizedPath);
                insert.Parameters.AddWithValue("$before_sha256", beforeSha256 is null ? DBNull.Value : beforeSha256);
                insert.Parameters.AddWithValue("$after_sha256", afterSha256 is null ? DBNull.Value : afterSha256);
                insert.Parameters.AddWithValue("$protected_payload", encrypted.Ciphertext.ToArray());
                insert.Parameters.AddWithValue("$nonce", encrypted.Nonce.ToArray());
                insert.Parameters.AddWithValue("$authentication_tag", encrypted.AuthenticationTag.ToArray());
                insert.Parameters.AddWithValue("$protection_version", encrypted.ProtectionVersion);
                insert.Parameters.AddWithValue("$status", status);
                insert.Parameters.AddWithValue("$created_at_utc", nowUtc.ToString("O"));
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new WorkspaceChangeRecord(
                changeId,
                workspaceId.Value,
                runId?.Value,
                sequence,
                normalizedPath,
                beforeSha256,
                afterSha256,
                status,
                nowUtc);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HarnessStoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            throw new HarnessStoreException("workspace_change_store_failed", exception);
        }
    }

    private static bool MatchesEncrypted(
        EncryptedWorkspaceJournal existing,
        EncryptedWorkspaceJournal incoming) =>
        existing.ProtectionVersion == incoming.ProtectionVersion
        && existing.Nonce.Span.SequenceEqual(incoming.Nonce.Span)
        && existing.Ciphertext.Span.SequenceEqual(incoming.Ciphertext.Span)
        && existing.AuthenticationTag.Span.SequenceEqual(incoming.AuthenticationTag.Span);

    private static async Task<WorkspaceChangeRecord?> ReadChangeByIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string changeId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT change_id, workspace_id, run_id, commit_sequence, normalized_path,
                   before_sha256, after_sha256, status, created_at_utc
            FROM workspace_change_journal
            WHERE change_id = $change_id
            """;
        command.Parameters.AddWithValue("$change_id", changeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new WorkspaceChangeRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetInt64(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7),
            DateTimeOffset.Parse(reader.GetString(8), null, System.Globalization.DateTimeStyles.RoundtripKind));
    }
}
