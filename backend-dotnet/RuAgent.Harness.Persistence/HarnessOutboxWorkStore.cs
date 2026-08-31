using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;

namespace RuAgent.Harness.Persistence;

public sealed partial class HarnessOutboxStore
{
    public async Task<OutboxWork> ReadNextWorkAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await _options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // 1. Read prioritized candidates without a write transaction
        var transition = await ReadTransitionCandidateAsync(connection, null, cancellationToken).ConfigureAwait(false);
        if (transition is not null)
        {
            return new OutboxWork.Transition(transition);
        }

        var deliveryCandidate = await ReadDeliveryCandidateAsync(connection, null, now, cancellationToken).ConfigureAwait(false);
        if (deliveryCandidate is null)
        {
            return new OutboxWork.Idle();
        }

        // 2. Delivery exists without transition: start write transaction and re-read under lock
        await using var transaction = connection.BeginTransaction(deferred: false);
        // Re-check transition under transaction - it has priority
        var transitionUnderLock = await ReadTransitionCandidateAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (transitionUnderLock is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new OutboxWork.Transition(transitionUnderLock);
        }

        var deliveryUnderLock = await ReadDeliveryCandidateAsync(connection, transaction, now, cancellationToken).ConfigureAwait(false);
        if (deliveryUnderLock is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new OutboxWork.Idle();
        }

        // Apply conditional claim with fresh token/lease
        var token = "lease_" + RandomNumberGenerator.GetHexString(16, lowercase: true);
        var claim = new OutboxClaim(
            deliveryUnderLock.Value.IntentId,
            deliveryUnderLock.Value.RunId,
            deliveryUnderLock.Value.SessionId,
            deliveryUnderLock.Value.PayloadId,
            deliveryUnderLock.Value.Role,
            deliveryUnderLock.Value.HistoryWaitKind,
            checked(deliveryUnderLock.Value.AttemptCount + 1),
            token,
            now + _leaseDuration);

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
        update.Parameters.AddWithValue("$attempt_count", claim.AttemptCount);
        update.Parameters.AddWithValue("$claim_token", claim.ClaimToken);
        update.Parameters.AddWithValue("$lease_expires", HarnessSqliteMapper.Utc(claim.LeaseExpiresAt));
        update.Parameters.AddWithValue("$intent_id", claim.IntentId.Value);
        update.Parameters.AddWithValue("$now", HarnessSqliteMapper.Utc(now));
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            // Another contender claimed it or it became ineligible; treat as conflict and return Idle for this dispatch
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new OutboxWork.Idle();
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new OutboxWork.Delivery(claim);
    }

    private static async Task<OutboxRunTransition?> ReadTransitionCandidateAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT o.intent_id,
                   o.run_id,
                   o.history_wait_kind,
                   o.delivery_state,
                   o.last_error_code
            FROM agent_conversation_outbox AS o
            INNER JOIN agent_runs AS r ON r.run_id = o.run_id
            WHERE (o.delivery_state = 2
                   AND r.state = $waiting_history
                   AND r.history_wait_kind = o.history_wait_kind)
               OR (o.delivery_state = 3 AND r.terminal_at_utc IS NULL)
            ORDER BY o.acknowledged_at_utc, o.intent_id
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$waiting_history", (int)HarnessRunState.WaitingHistory);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var deliveryState = reader.GetInt32(3);
        return new OutboxRunTransition(
            new EventId(reader.GetString(0)),
            new RunId(reader.GetString(1)),
            (HistoryWaitKind)reader.GetInt32(2),
            deliveryState == 2 ? OutboxRunTransitionKind.AcknowledgeHistory : OutboxRunTransitionKind.FailRun,
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    private static async Task<DeliveryCandidate?> ReadDeliveryCandidateAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
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
        command.Parameters.AddWithValue("$now", HarnessSqliteMapper.Utc(now));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new DeliveryCandidate(
            new EventId(reader.GetString(0)),
            new RunId(reader.GetString(1)),
            new SessionId(reader.GetString(2)),
            new PayloadId(reader.GetString(3)),
            (ConversationOutboxRole)reader.GetInt32(4),
            (HistoryWaitKind)reader.GetInt32(5),
            reader.GetInt32(6));
    }

    private readonly record struct DeliveryCandidate(
        EventId IntentId,
        RunId RunId,
        SessionId SessionId,
        PayloadId PayloadId,
        ConversationOutboxRole Role,
        HistoryWaitKind HistoryWaitKind,
        int AttemptCount);
}
