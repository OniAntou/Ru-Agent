using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;

namespace RuAgent.Harness.Persistence;

public sealed partial class WorkspaceCoordinatorStore
{
    public async Task<WorkspaceRunMembershipRecord> AcquireRunMembershipAsync(
        WorkspaceId workspaceId,
        RunId runId,
        string writerKind,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        _ = workspaceId.Value;
        _ = runId.Value;
        if (writerKind is not ("ru_peer" or "unknown_external")
            || leaseDuration <= TimeSpan.Zero
            || leaseDuration > TimeSpan.FromMinutes(10))
        {
            throw new HarnessStoreException("invalid_workspace_membership");
        }

        await using var connection = await _options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            var coordinator = await ReadAsync(connection, transaction, workspaceId.Value, cancellationToken).ConfigureAwait(false)
                ?? throw new HarnessStoreException("workspace_not_found");
            await EnsureRunBelongsToWorkspaceAsync(connection, transaction, workspaceId.Value, runId.Value, cancellationToken).ConfigureAwait(false);
            await ExpireMembershipsAsync(connection, transaction, workspaceId.Value, nowUtc, cancellationToken).ConfigureAwait(false);

            var existing = await ReadMembershipAsync(connection, transaction, workspaceId.Value, runId.Value, cancellationToken).ConfigureAwait(false);
            var expiresAt = nowUtc.Add(leaseDuration);
            if (existing is not null && existing.State == "active")
            {
                await using var renew = connection.CreateCommand();
                renew.Transaction = transaction;
                renew.CommandText = """
                    UPDATE workspace_run_members
                    SET writer_kind = $writer_kind, heartbeat_at_utc = $heartbeat_at_utc,
                        lease_expires_at_utc = $lease_expires_at_utc, state = 'active'
                    WHERE workspace_id = $workspace_id AND run_id = $run_id
                    """;
                renew.Parameters.AddWithValue("$writer_kind", writerKind);
                renew.Parameters.AddWithValue("$heartbeat_at_utc", nowUtc.ToString("O"));
                renew.Parameters.AddWithValue("$lease_expires_at_utc", expiresAt.ToString("O"));
                renew.Parameters.AddWithValue("$workspace_id", workspaceId.Value);
                renew.Parameters.AddWithValue("$run_id", runId.Value);
                await renew.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return existing with { WriterKind = writerKind, HeartbeatAtUtc = nowUtc, LeaseExpiresAtUtc = expiresAt };
            }

            await using (var count = connection.CreateCommand())
            {
                count.Transaction = transaction;
                count.CommandText = "SELECT COUNT(*) FROM workspace_run_members WHERE workspace_id = $workspace_id AND state = 'active'";
                count.Parameters.AddWithValue("$workspace_id", workspaceId.Value);
                var active = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
                if (active >= coordinator.MaxConcurrentRuns)
                {
                    throw new HarnessStoreException("workspace_busy");
                }
            }

            var memberId = existing?.MemberId ?? $"member_{Guid.NewGuid():N}";
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO workspace_run_members(
                    member_id, workspace_id, run_id, writer_kind, state,
                    heartbeat_at_utc, lease_expires_at_utc, created_at_utc)
                VALUES($member_id, $workspace_id, $run_id, $writer_kind, 'active',
                       $heartbeat_at_utc, $lease_expires_at_utc, $created_at_utc)
                ON CONFLICT(workspace_id, run_id) DO UPDATE SET
                    writer_kind = excluded.writer_kind,
                    state = 'active',
                    heartbeat_at_utc = excluded.heartbeat_at_utc,
                    lease_expires_at_utc = excluded.lease_expires_at_utc
                """;
            insert.Parameters.AddWithValue("$member_id", memberId);
            insert.Parameters.AddWithValue("$workspace_id", workspaceId.Value);
            insert.Parameters.AddWithValue("$run_id", runId.Value);
            insert.Parameters.AddWithValue("$writer_kind", writerKind);
            insert.Parameters.AddWithValue("$heartbeat_at_utc", nowUtc.ToString("O"));
            insert.Parameters.AddWithValue("$lease_expires_at_utc", expiresAt.ToString("O"));
            insert.Parameters.AddWithValue("$created_at_utc", existing?.HeartbeatAtUtc.ToString("O") ?? nowUtc.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new WorkspaceRunMembershipRecord(memberId, workspaceId.Value, runId.Value, writerKind, "active", nowUtc, expiresAt);
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
            throw new HarnessStoreException("workspace_membership_store_failed", exception);
        }
    }

    public async Task<bool> RenewRunMembershipAsync(
        WorkspaceId workspaceId,
        RunId runId,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromMinutes(10))
        {
            throw new HarnessStoreException("invalid_workspace_membership");
        }

        await using var connection = await _options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE workspace_run_members
            SET heartbeat_at_utc = $heartbeat_at_utc,
                lease_expires_at_utc = $lease_expires_at_utc
            WHERE workspace_id = $workspace_id AND run_id = $run_id
              AND state = 'active' AND lease_expires_at_utc > $now_utc
            """;
        command.Parameters.AddWithValue("$heartbeat_at_utc", nowUtc.ToString("O"));
        command.Parameters.AddWithValue("$lease_expires_at_utc", nowUtc.Add(leaseDuration).ToString("O"));
        command.Parameters.AddWithValue("$workspace_id", workspaceId.Value);
        command.Parameters.AddWithValue("$run_id", runId.Value);
        command.Parameters.AddWithValue("$now_utc", nowUtc.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<WorkspaceReconciliationResult> ReconcileExpiredAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await _options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var memberships = connection.CreateCommand();
        memberships.Transaction = transaction;
        memberships.CommandText = """
            UPDATE workspace_run_members
            SET state = 'expired'
            WHERE state = 'active' AND lease_expires_at_utc <= $now_utc
            """;
        memberships.Parameters.AddWithValue("$now_utc", nowUtc.ToString("O"));
        var expiredMemberships = await memberships.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var claims = connection.CreateCommand();
        claims.Transaction = transaction;
        claims.CommandText = """
            UPDATE workspace_path_claims
            SET state = 'expired'
            WHERE state = 'active' AND lease_expires_at_utc <= $now_utc
            """;
        claims.Parameters.AddWithValue("$now_utc", nowUtc.ToString("O"));
        var expiredClaims = await claims.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var prepared = connection.CreateCommand();
        prepared.Transaction = transaction;
        prepared.CommandText = """
            UPDATE workspace_change_journal
            SET status = 'rejected'
            WHERE status = 'prepared' AND created_at_utc <= $cutoff_utc
            """;
        prepared.Parameters.AddWithValue("$cutoff_utc", nowUtc.AddMinutes(-10).ToString("O"));
        var rejectedPrepared = await prepared.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new WorkspaceReconciliationResult(expiredMemberships, expiredClaims, rejectedPrepared);
    }

    public async Task ReleaseRunMembershipAsync(
        WorkspaceId workspaceId,
        RunId runId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await _options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE workspace_run_members
            SET state = 'released', heartbeat_at_utc = $now_utc, lease_expires_at_utc = $now_utc
            WHERE workspace_id = $workspace_id AND run_id = $run_id AND state <> 'released'
            """;
        command.Parameters.AddWithValue("$now_utc", nowUtc.ToString("O"));
        command.Parameters.AddWithValue("$workspace_id", workspaceId.Value);
        command.Parameters.AddWithValue("$run_id", runId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureRunBelongsToWorkspaceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string workspaceId,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT workspace_id FROM agent_runs WHERE run_id = $run_id";
        command.Parameters.AddWithValue("$run_id", runId);
        var runWorkspace = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (runWorkspace is null)
        {
            throw new HarnessStoreException("run_not_found");
        }

        if (!string.Equals(runWorkspace, workspaceId, StringComparison.Ordinal))
        {
            throw new HarnessStoreException("run_workspace_mismatch");
        }
    }

    private static async Task ExpireMembershipsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string workspaceId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE workspace_run_members
            SET state = 'expired'
            WHERE workspace_id = $workspace_id
              AND state = 'active'
              AND lease_expires_at_utc <= $now_utc
            """;
        command.Parameters.AddWithValue("$workspace_id", workspaceId);
        command.Parameters.AddWithValue("$now_utc", nowUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<WorkspaceRunMembershipRecord?> ReadMembershipAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string workspaceId,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT member_id, workspace_id, run_id, writer_kind, state,
                   heartbeat_at_utc, lease_expires_at_utc
            FROM workspace_run_members
            WHERE workspace_id = $workspace_id AND run_id = $run_id
            """;
        command.Parameters.AddWithValue("$workspace_id", workspaceId);
        command.Parameters.AddWithValue("$run_id", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new WorkspaceRunMembershipRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            DateTimeOffset.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(reader.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind));
    }
}
