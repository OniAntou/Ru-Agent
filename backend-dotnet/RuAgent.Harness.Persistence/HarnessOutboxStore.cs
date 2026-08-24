using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;

namespace RuAgent.Harness.Persistence;

public sealed partial class HarnessOutboxStore
{
    private readonly HarnessSqliteOptions _options;
    private readonly TimeSpan _leaseDuration;

    public HarnessOutboxStore(
        HarnessSqliteOptions options,
        TimeSpan? leaseDuration = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _leaseDuration = leaseDuration ?? TimeSpan.FromSeconds(30);
        if (_leaseDuration < TimeSpan.FromSeconds(1)
            || _leaseDuration > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }
    }

    public async Task<OutboxClaim?> ClaimNextAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await _options.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT intent_id,
                   run_id,
                   session_id,
                   payload_id,
                   role,
                   history_wait_kind,
                   attempt_count
            FROM agent_conversation_outbox
            WHERE (delivery_state = 0
                    AND (next_attempt_at_utc IS NULL OR next_attempt_at_utc <= $now))
               OR (delivery_state = 1 AND lease_expires_at_utc <= $now)
            ORDER BY created_at_utc, intent_id
            LIMIT 1
            """;
        select.Parameters.AddWithValue("$now", HarnessSqliteMapper.Utc(now));
        OutboxClaim? selected = null;
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken)
                         .ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var token = "lease_" + RandomNumberGenerator.GetHexString(16, lowercase: true);
                selected = new OutboxClaim(
                    new EventId(reader.GetString(0)),
                    new RunId(reader.GetString(1)),
                    new SessionId(reader.GetString(2)),
                    new PayloadId(reader.GetString(3)),
                    (ConversationOutboxRole)reader.GetInt32(4),
                    (HistoryWaitKind)reader.GetInt32(5),
                    checked(reader.GetInt32(6) + 1),
                    token,
                    now + _leaseDuration);
            }
        }

        if (selected is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE agent_conversation_outbox
            SET delivery_state = 1,
                attempt_count = $attempt_count,
                claim_token = $claim_token,
                lease_expires_at_utc = $lease_expires,
                next_attempt_at_utc = NULL
            WHERE intent_id = $intent_id
              AND (delivery_state = 0
                   OR (delivery_state = 1 AND lease_expires_at_utc <= $now))
            """;
        update.Parameters.AddWithValue("$attempt_count", selected.AttemptCount);
        update.Parameters.AddWithValue("$claim_token", selected.ClaimToken);
        update.Parameters.AddWithValue(
            "$lease_expires",
            HarnessSqliteMapper.Utc(selected.LeaseExpiresAt));
        update.Parameters.AddWithValue("$intent_id", selected.IntentId.Value);
        update.Parameters.AddWithValue("$now", HarnessSqliteMapper.Utc(now));
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new HarnessStoreException("outbox_claim_conflict");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return selected;
    }

    public Task<OutboxMutationResult> AcknowledgeAsync(
        EventId intentId,
        string claimToken,
        DateTimeOffset acknowledgedAt,
        CancellationToken cancellationToken) =>
        CompleteAsync(
            intentId,
            claimToken,
            targetState: 2,
            errorCode: null,
            acknowledgedAt,
            cancellationToken);

    public async Task<OutboxMutationResult> RetryAsync(
        EventId intentId,
        string claimToken,
        string errorCode,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken)
    {
        ValidateMutation(intentId, claimToken, errorCode);
        await using var connection = await _options.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var state = await ReadStateAsync(
                connection,
                transaction,
                intentId,
                cancellationToken)
            .ConfigureAwait(false);
        if (state is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new OutboxMutationResult(OutboxMutationOutcome.NotFound);
        }

        if (state.Value.DeliveryState != 1
            || !string.Equals(state.Value.ClaimToken, claimToken, StringComparison.Ordinal))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new OutboxMutationResult(OutboxMutationOutcome.Conflict);
        }

        var delaySeconds = Math.Min(
            60,
            1 << Math.Min(Math.Max(state.Value.AttemptCount - 1, 0), 6));
        var nextAttempt = failedAt.AddSeconds(delaySeconds);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE agent_conversation_outbox
            SET delivery_state = 0,
                next_attempt_at_utc = $next_attempt,
                claim_token = NULL,
                lease_expires_at_utc = NULL,
                last_error_code = $error_code
            WHERE intent_id = $intent_id
              AND delivery_state = 1
              AND claim_token = $claim_token
            """;
        update.Parameters.AddWithValue("$next_attempt", HarnessSqliteMapper.Utc(nextAttempt));
        update.Parameters.AddWithValue("$error_code", errorCode);
        update.Parameters.AddWithValue("$intent_id", intentId.Value);
        update.Parameters.AddWithValue("$claim_token", claimToken);
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new HarnessStoreException("outbox_retry_conflict");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new OutboxMutationResult(OutboxMutationOutcome.RetryScheduled, nextAttempt);
    }

    public Task<OutboxMutationResult> FailAsync(
        EventId intentId,
        string claimToken,
        string errorCode,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken)
    {
        ValidateMutation(intentId, claimToken, errorCode);
        return CompleteAsync(
            intentId,
            claimToken,
            targetState: 3,
            errorCode,
            failedAt,
            cancellationToken);
    }

    private async Task<OutboxMutationResult> CompleteAsync(
        EventId intentId,
        string claimToken,
        int targetState,
        string? errorCode,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        ValidateMutation(intentId, claimToken, errorCode);
        await using var connection = await _options.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var state = await ReadStateAsync(
                connection,
                transaction,
                intentId,
                cancellationToken)
            .ConfigureAwait(false);
        if (state is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new OutboxMutationResult(OutboxMutationOutcome.NotFound);
        }

        if (state.Value.DeliveryState == 2 && targetState == 2)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new OutboxMutationResult(OutboxMutationOutcome.AlreadyAcknowledged);
        }

        if (state.Value.DeliveryState != 1
            || !string.Equals(state.Value.ClaimToken, claimToken, StringComparison.Ordinal))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new OutboxMutationResult(OutboxMutationOutcome.Conflict);
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE agent_conversation_outbox
            SET delivery_state = $target_state,
                claim_token = NULL,
                lease_expires_at_utc = NULL,
                next_attempt_at_utc = NULL,
                last_error_code = $error_code,
                acknowledged_at_utc = $acknowledged_at
            WHERE intent_id = $intent_id
              AND delivery_state = 1
              AND claim_token = $claim_token
            """;
        update.Parameters.AddWithValue("$target_state", targetState);
        update.Parameters.AddWithValue(
            "$error_code",
            errorCode is null ? DBNull.Value : errorCode);
        update.Parameters.AddWithValue("$acknowledged_at", HarnessSqliteMapper.Utc(completedAt));
        update.Parameters.AddWithValue("$intent_id", intentId.Value);
        update.Parameters.AddWithValue("$claim_token", claimToken);
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new HarnessStoreException("outbox_complete_conflict");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new OutboxMutationResult(
            targetState == 2 ? OutboxMutationOutcome.Acknowledged : OutboxMutationOutcome.Failed);
    }

    private static async Task<OutboxState?> ReadStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EventId intentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT delivery_state, attempt_count, claim_token
            FROM agent_conversation_outbox
            WHERE intent_id = $intent_id
            """;
        command.Parameters.AddWithValue("$intent_id", intentId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new OutboxState(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetString(2))
            : null;
    }

    private static void ValidateMutation(
        EventId intentId,
        string claimToken,
        string? errorCode)
    {
        _ = intentId.Value;
        if (claimToken is not { Length: >= 1 and <= 64 })
        {
            throw new ArgumentException("Claim token must be bounded.", nameof(claimToken));
        }

        if (errorCode is not null
            && (errorCode is not { Length: >= 1 and <= 64 }
                || errorCode.Any(character =>
                    character is not (>= 'a' and <= 'z')
                        and not (>= '0' and <= '9')
                        and not '_'
                        and not '.')))
        {
            throw new ArgumentException("Outbox error code must be safe.", nameof(errorCode));
        }
    }

    private readonly record struct OutboxState(
        int DeliveryState,
        int AttemptCount,
        string? ClaimToken);
}
