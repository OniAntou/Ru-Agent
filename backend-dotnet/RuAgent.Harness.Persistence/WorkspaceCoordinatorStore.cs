using System.Text.Json;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;

namespace RuAgent.Harness.Persistence;

public sealed record WorkspaceCoordinatorRecord(
    string WorkspaceId,
    string Mode,
    int MaxConcurrentRuns,
    int LeaseTtlSeconds,
    bool MergeTextConflicts,
    bool ExternalWriterDetection,
    long CommitSequence,
    string PolicyJson,
    long Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record WorkspaceRunMembershipRecord(
    string MemberId,
    string WorkspaceId,
    string RunId,
    string WriterKind,
    string State,
    DateTimeOffset HeartbeatAtUtc,
    DateTimeOffset LeaseExpiresAtUtc);

public sealed record WorkspaceChangeRecord(
    string ChangeId,
    string WorkspaceId,
    string? RunId,
    long CommitSequence,
    string NormalizedPath,
    string? BeforeSha256,
    string? AfterSha256,
    string Status,
    DateTimeOffset CreatedAtUtc);

public sealed record WorkspaceChangeEnvelope(
    WorkspaceChangeRecord Change,
    EncryptedWorkspaceJournal Journal);

public sealed record WorkspaceReconciliationResult(
    int ExpiredMemberships,
    int ExpiredClaims,
    int RejectedPreparedChanges);

public sealed partial class WorkspaceCoordinatorStore
{
    private readonly HarnessSqliteOptions _options;

    public WorkspaceCoordinatorStore(HarnessSqliteOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<WorkspaceCoordinatorRecord> GetOrCreateAsync(
        WorkspaceId workspaceId,
        CancellationToken cancellationToken)
    {
        _ = workspaceId.Value;
        await using var connection = await _options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            var existing = await ReadAsync(connection, transaction, workspaceId.Value, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return existing;
            }

            await using (var workspaceCommand = connection.CreateCommand())
            {
                workspaceCommand.Transaction = transaction;
                workspaceCommand.CommandText = "SELECT 1 FROM agent_workspaces WHERE workspace_id = $workspace_id";
                workspaceCommand.Parameters.AddWithValue("$workspace_id", workspaceId.Value);
                if (await workspaceCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
                {
                    throw new HarnessStoreException("workspace_not_found");
                }
            }

            var now = DateTimeOffset.UtcNow;
            var policyJson = JsonSerializer.Serialize(new
            {
                mode = "exclusive",
                max_concurrent_runs = 1,
                lease_ttl_seconds = 30,
                merge_text_conflicts = true,
                external_writer_detection = true,
            });
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO workspace_coordinators(
                    workspace_id, mode, max_concurrent_runs, lease_ttl_seconds,
                    merge_text_conflicts, external_writer_detection, commit_sequence,
                    policy_json, version, created_at_utc, updated_at_utc)
                VALUES(
                    $workspace_id, 'exclusive', 1, 30, 1, 1, 0,
                    $policy_json, 1, $created_at, $updated_at)
                """;
            insert.Parameters.AddWithValue("$workspace_id", workspaceId.Value);
            insert.Parameters.AddWithValue("$policy_json", policyJson);
            insert.Parameters.AddWithValue("$created_at", now.ToString("O"));
            insert.Parameters.AddWithValue("$updated_at", now.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new WorkspaceCoordinatorRecord(
                workspaceId.Value,
                "exclusive",
                1,
                30,
                true,
                true,
                0,
                policyJson,
                1,
                now,
                now);
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
            throw new HarnessStoreException("workspace_coordinator_store_failed", exception);
        }
    }

    public async Task<WorkspaceCoordinatorRecord> UpdatePolicyAsync(
        WorkspaceId workspaceId,
        long expectedVersion,
        string mode,
        int maxConcurrentRuns,
        int leaseTtlSeconds,
        bool mergeTextConflicts,
        bool externalWriterDetection,
        CancellationToken cancellationToken)
    {
        _ = workspaceId.Value;
        if (mode is not ("exclusive" or "shared")
            || maxConcurrentRuns is < 1 or > 32
            || leaseTtlSeconds is < 5 or > 600
            || expectedVersion < 1)
        {
            throw new HarnessStoreException("invalid_workspace_concurrency_policy");
        }

        await using var connection = await _options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            var policyJson = JsonSerializer.Serialize(new
            {
                mode,
                max_concurrent_runs = maxConcurrentRuns,
                lease_ttl_seconds = leaseTtlSeconds,
                merge_text_conflicts = mergeTextConflicts,
                external_writer_detection = externalWriterDetection,
            });
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE workspace_coordinators
                SET mode = $mode,
                    max_concurrent_runs = $max_concurrent_runs,
                    lease_ttl_seconds = $lease_ttl_seconds,
                    merge_text_conflicts = $merge_text_conflicts,
                    external_writer_detection = $external_writer_detection,
                    policy_json = $policy_json,
                    version = version + 1,
                    updated_at_utc = $updated_at
                WHERE workspace_id = $workspace_id
                  AND version = $expected_version
                """;
            command.Parameters.AddWithValue("$mode", mode);
            command.Parameters.AddWithValue("$max_concurrent_runs", maxConcurrentRuns);
            command.Parameters.AddWithValue("$lease_ttl_seconds", leaseTtlSeconds);
            command.Parameters.AddWithValue("$merge_text_conflicts", mergeTextConflicts ? 1 : 0);
            command.Parameters.AddWithValue("$external_writer_detection", externalWriterDetection ? 1 : 0);
            command.Parameters.AddWithValue("$policy_json", policyJson);
            command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$workspace_id", workspaceId.Value);
            command.Parameters.AddWithValue("$expected_version", expectedVersion);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new HarnessStoreException("workspace_concurrency_version_conflict");
            }

            var updated = await ReadAsync(connection, transaction, workspaceId.Value, cancellationToken).ConfigureAwait(false)
                ?? throw new HarnessStoreException("workspace_not_found");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return updated;
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
            throw new HarnessStoreException("workspace_policy_update_failed", exception);
        }
    }

    public async Task<int> CountActiveClaimsAsync(
        WorkspaceId workspaceId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        _ = workspaceId.Value;
        await using var connection = await _options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM workspace_path_claims
            WHERE workspace_id = $workspace_id
              AND state = 'active'
              AND lease_expires_at_utc > $now_utc
            """;
        command.Parameters.AddWithValue("$workspace_id", workspaceId.Value);
        command.Parameters.AddWithValue("$now_utc", nowUtc.ToString("O"));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<WorkspaceCoordinatorRecord?> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string workspaceId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT workspace_id, mode, max_concurrent_runs, lease_ttl_seconds,
                   merge_text_conflicts, external_writer_detection, commit_sequence,
                   policy_json, version, created_at_utc, updated_at_utc
            FROM workspace_coordinators
            WHERE workspace_id = $workspace_id
            """;
        command.Parameters.AddWithValue("$workspace_id", workspaceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new WorkspaceCoordinatorRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4) == 1,
            reader.GetInt32(5) == 1,
            reader.GetInt64(6),
            reader.GetString(7),
            reader.GetInt64(8),
            DateTimeOffset.Parse(reader.GetString(9), null, System.Globalization.DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(reader.GetString(10), null, System.Globalization.DateTimeStyles.RoundtripKind));
    }
}
