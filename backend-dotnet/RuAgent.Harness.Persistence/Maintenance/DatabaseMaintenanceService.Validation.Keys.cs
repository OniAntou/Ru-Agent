using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Persistence.Privacy;
using RuAgent.Harness.Persistence.Workspace;

namespace RuAgent.Harness.Persistence.Maintenance;

public sealed partial class DatabaseMaintenanceService
{
    private static async Task ValidateSessionSearchIndexAsync(string databasePath, CancellationToken cancellationToken)
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "ru-database-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var copyPath = Path.Combine(temporaryRoot, DatabaseFileName);
            await CreateSqliteSnapshotAsync(databasePath, copyPath, cancellationToken).ConfigureAwait(false);
            await using var connection = await OpenReadWriteAsync(copyPath, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO business_session_message_search(business_session_message_search, rank)
                VALUES ('integrity-check', 1);
                """;
            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException exception)
            {
                throw new DatabaseMaintenanceException("database_search_integrity_failed", exception);
            }
        }
        finally
        {
            TryDeleteDirectory(temporaryRoot);
        }
    }

    private static async Task ValidateProtectedDataKeyAsync(
        string databasePath,
        byte[] key,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadOnlyAsync(databasePath, cancellationToken).ConfigureAwait(false);
        var keyStore = new FixedHarnessKeyStore(key);
        await ValidatePayloadRowsAsync(connection, new HarnessPayloadCryptography(keyStore), cancellationToken)
            .ConfigureAwait(false);
        await ValidateWorkspaceRootRowsAsync(connection, key, cancellationToken).ConfigureAwait(false);
        await ValidateSessionWorkStateRowsAsync(connection, new SessionWorkStateCryptography(keyStore), cancellationToken)
            .ConfigureAwait(false);
        await ValidateWorkspaceProtectedRowsAsync(connection, new WorkspaceJournalProtector(keyStore), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task ValidateWorkspaceRootRowsAsync(
        SqliteConnection connection,
        byte[] key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT workspace_id, canonical_root_nonce, canonical_root_ciphertext, canonical_root_tag
            FROM agent_workspaces ORDER BY workspace_id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                var plaintext = WorkspaceRootCryptography.Decrypt(
                    new WorkspaceId(reader.GetString(0)),
                    key,
                    new WorkspaceRootCiphertext((byte[])reader[1], (byte[])reader[2], (byte[])reader[3]));
                CryptographicOperations.ZeroMemory(plaintext);
            }
            catch (CryptographicException exception)
            {
                throw new DatabaseMaintenanceException("database_key_mismatch", exception);
            }
            catch (Exception exception) when (exception is HarnessStoreException or ArgumentException
                or InvalidCastException or InvalidOperationException)
            {
                throw new DatabaseMaintenanceException("database_key_validation_failed", exception);
            }
        }
    }

    private static async Task ValidatePayloadRowsAsync(
        SqliteConnection connection,
        HarnessPayloadCryptography cryptography,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, payload_id, kind, audience, encoding, byte_cap, protected_content,
                   protection_version, nonce, authentication_tag
            FROM agent_payloads ORDER BY rowid;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                var runId = new RunId(reader.GetString(0));
                var intent = new HarnessPayloadIntent(
                    new PayloadId(reader.GetString(1)),
                    (HarnessPayloadKind)reader.GetInt32(2),
                    (HarnessPayloadAudience)reader.GetInt32(3),
                    (HarnessPayloadEncoding)reader.GetInt32(4),
                    reader.GetInt32(5),
                    ReadOnlyMemory<byte>.Empty);
                var envelope = new EncryptedHarnessPayload(
                    reader.GetInt32(7),
                    (byte[])reader[8],
                    (byte[])reader[6],
                    (byte[])reader[9]);
                var plaintext = await cryptography.DecryptAsync(runId, intent, envelope, cancellationToken)
                    .ConfigureAwait(false);
                ZeroMemory(plaintext);
            }
            catch (HarnessStoreException exception) when (exception.Code == "payload_authentication_failed")
            {
                throw new DatabaseMaintenanceException("database_key_mismatch", exception);
            }
            catch (Exception exception) when (exception is HarnessStoreException or ArgumentException
                or InvalidCastException or InvalidOperationException)
            {
                throw new DatabaseMaintenanceException("database_key_validation_failed", exception);
            }
        }
    }

    private static async Task ValidateSessionWorkStateRowsAsync(
        SqliteConnection connection,
        SessionWorkStateCryptography cryptography,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id, workspace_id, workspace_identity_sha256, source_run_id, source_event_id,
                   version, protection_version, nonce, ciphertext, authentication_tag
            FROM agent_session_work_states ORDER BY session_id, workspace_id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                _ = await cryptography.UnprotectAsync(
                    new SessionId(reader.GetString(0)),
                    new WorkspaceId(reader.GetString(1)),
                    reader.GetString(2),
                    new RunId(reader.GetString(3)),
                    reader.GetInt64(5),
                    new EventId(reader.GetString(4)),
                    new EncryptedHarnessPayload(reader.GetInt32(6), (byte[])reader[7],
                        (byte[])reader[8], (byte[])reader[9]),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (HarnessStoreException exception) when (exception.Code is "session_work_state_authentication_failed")
            {
                throw new DatabaseMaintenanceException("database_key_mismatch", exception);
            }
            catch (Exception exception) when (exception is HarnessStoreException or ArgumentException
                or InvalidCastException or InvalidOperationException)
            {
                throw new DatabaseMaintenanceException("database_key_validation_failed", exception);
            }
        }
    }

    private static async Task ValidateWorkspaceProtectedRowsAsync(
        SqliteConnection connection,
        WorkspaceJournalProtector protector,
        CancellationToken cancellationToken)
    {
        await DecryptWorkspaceRowsAsync(connection, protector, """
            SELECT workspace_id, change_id, normalized_path, protection_version, nonce, protected_payload, authentication_tag
            FROM workspace_change_journal ORDER BY change_id;
            """, cancellationToken).ConfigureAwait(false);

        await DecryptWorkspaceRowsAsync(connection, protector, """
            SELECT root.workspace_id, scope.run_id, 'subagent.scope', scope.protection_version,
                   scope.nonce, scope.ciphertext, scope.authentication_tag
            FROM agent_subagent_scopes AS scope
            JOIN agent_internal_runs AS internal_run ON internal_run.run_id = scope.run_id
            JOIN agent_runs AS root ON root.run_id = internal_run.root_run_id
            ORDER BY scope.run_id;
            """, cancellationToken).ConfigureAwait(false);

        await DecryptWorkspaceRowsAsync(connection, protector, """
            SELECT root.workspace_id, changes.run_id, 'subagent.changes', changes.protection_version,
                   changes.nonce, changes.ciphertext, changes.authentication_tag
            FROM agent_subagent_change_sets AS changes
            JOIN agent_internal_runs AS internal_run ON internal_run.run_id = changes.run_id
            JOIN agent_runs AS root ON root.run_id = internal_run.root_run_id
            ORDER BY changes.run_id;
            """, cancellationToken).ConfigureAwait(false);

        await DecryptWorkspaceRowsAsync(connection, protector, """
            SELECT root.workspace_id, allocation.root_run_id, allocation.agent_id,
                   allocation.protection_version, allocation.nonce, allocation.ciphertext, allocation.authentication_tag
            FROM agent_subagent_workspace_allocations AS allocation
            JOIN agent_runs AS root ON root.run_id = allocation.root_run_id
            WHERE allocation.ciphertext IS NOT NULL
            ORDER BY allocation.root_run_id, allocation.agent_id;
            """, cancellationToken, async (reader, ct) =>
            {
                var identity = AllocationProtectionId(reader.GetString(1), reader.GetString(2));
                await DecryptWorkspaceRecordAsync(protector, reader.GetString(0), identity,
                    "subagent.allocation", reader.GetInt32(3), (byte[])reader[4], (byte[])reader[5],
                    (byte[])reader[6], ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

        await DecryptWorkspaceRowsAsync(connection, protector, """
            SELECT root.workspace_id, changes.run_id, changes.file_index, changes.chunk_index,
                   changes.protection_version, changes.nonce, changes.ciphertext, changes.authentication_tag
            FROM agent_subagent_change_chunks AS changes
            JOIN agent_internal_runs AS internal_run ON internal_run.run_id = changes.run_id
            JOIN agent_runs AS root ON root.run_id = internal_run.root_run_id
            ORDER BY changes.run_id, changes.file_index, changes.chunk_index;
            """, cancellationToken, async (reader, ct) =>
            {
                var identity = ChangeChunkIdentity(reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3));
                await DecryptWorkspaceRecordAsync(protector, reader.GetString(0), identity,
                    "subagent.content", reader.GetInt32(4), (byte[])reader[5], (byte[])reader[6],
                    (byte[])reader[7], ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

        await DecryptWorkspaceRowsAsync(connection, protector, """
            SELECT root.workspace_id, file.root_run_id, file.agent_id, file.result_version, file.file_index,
                   file.protection_version, file.nonce, file.ciphertext, file.authentication_tag
            FROM agent_subagent_apply_files AS file
            JOIN agent_runs AS root ON root.run_id = file.root_run_id
            ORDER BY file.root_run_id, file.agent_id, file.result_version, file.file_index;
            """, cancellationToken, async (reader, ct) =>
            {
                var identity = ApplyFileIdentity(reader.GetString(1), reader.GetString(2),
                    reader.GetInt64(3), reader.GetInt32(4));
                await DecryptWorkspaceRecordAsync(protector, reader.GetString(0), "apply_" + identity,
                    "subagent.apply", reader.GetInt32(5), (byte[])reader[6], (byte[])reader[7],
                    (byte[])reader[8], ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    private static async Task DecryptWorkspaceRowsAsync(
        SqliteConnection connection,
        WorkspaceJournalProtector protector,
        string sql,
        CancellationToken cancellationToken,
        Func<SqliteDataReader, CancellationToken, Task>? decrypt = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (decrypt is not null)
            {
                await decrypt(reader, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await DecryptWorkspaceRecordAsync(protector, reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetInt32(3), (byte[])reader[4], (byte[])reader[5],
                (byte[])reader[6], cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task DecryptWorkspaceRecordAsync(
        WorkspaceJournalProtector protector,
        string workspaceId,
        string changeId,
        string associatedPath,
        int protectionVersion,
        byte[] nonce,
        byte[] ciphertext,
        byte[] authenticationTag,
        CancellationToken cancellationToken)
    {
        try
        {
            var plaintext = await protector.DecryptAsync(new WorkspaceId(workspaceId), changeId, associatedPath,
                new EncryptedWorkspaceJournal(protectionVersion, nonce, ciphertext, authenticationTag),
                cancellationToken).ConfigureAwait(false);
            if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(plaintext, out var segment)
                && segment.Array is not null)
                CryptographicOperations.ZeroMemory(segment.Array.AsSpan(segment.Offset, segment.Count));
        }
        catch (HarnessStoreException exception) when (exception.Code == "journal_authentication_failed")
        {
            throw new DatabaseMaintenanceException("database_key_mismatch", exception);
        }
        catch (Exception exception) when (exception is HarnessStoreException or ArgumentException
            or InvalidCastException or InvalidOperationException)
        {
            throw new DatabaseMaintenanceException("database_key_validation_failed", exception);
        }
    }

    private static string AllocationProtectionId(string root, string agent) => "allocation_" +
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(root + ":" + agent))).ToLowerInvariant();

    private static string ChangeChunkIdentity(string run, int file, int chunk) => "delta_" +
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{run}:{file}:{chunk}"))).ToLowerInvariant();

    private static string ApplyFileIdentity(string root, string agent, long version, int index) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{root}:{agent}:{version}:{index}"))).ToLowerInvariant();

    private static void ZeroMemory(ReadOnlyMemory<byte> memory)
    {
        if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(memory, out var segment)
            && segment.Array is not null)
            CryptographicOperations.ZeroMemory(segment.Array.AsSpan(segment.Offset, segment.Count));
    }

    private sealed class FixedHarnessKeyStore(byte[] key) : IHarnessKeyStore
    {
        public ValueTask<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult((byte[])key.Clone());
        }
    }
}
