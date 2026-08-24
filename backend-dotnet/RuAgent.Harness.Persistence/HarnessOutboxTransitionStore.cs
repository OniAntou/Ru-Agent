using RuAgent.Harness.Core;

namespace RuAgent.Harness.Persistence;

public sealed partial class HarnessOutboxStore
{
    public async Task<IReadOnlyList<OutboxRunTransition>> ReadPendingRunTransitionsAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        await using var connection = await _options.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
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
            LIMIT $limit
            """;
        command.Parameters.AddWithValue(
            "$waiting_history",
            (int)HarnessRunState.WaitingHistory);
        command.Parameters.AddWithValue("$limit", limit);
        var transitions = new List<OutboxRunTransition>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var deliveryState = reader.GetInt32(3);
            transitions.Add(new OutboxRunTransition(
                new EventId(reader.GetString(0)),
                new RunId(reader.GetString(1)),
                (HistoryWaitKind)reader.GetInt32(2),
                deliveryState == 2
                    ? OutboxRunTransitionKind.AcknowledgeHistory
                    : OutboxRunTransitionKind.FailRun,
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return transitions;
    }
}
