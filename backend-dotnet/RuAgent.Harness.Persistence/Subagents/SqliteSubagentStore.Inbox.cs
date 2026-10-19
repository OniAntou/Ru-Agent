using System.Text;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Subagents;

namespace RuAgent.Harness.Persistence.Subagents;

public sealed partial class SqliteSubagentStore : ISubagentInbox
{
    public async Task<int> DeliverMessagesAsync(RunId runId, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await Options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var run = await HarnessSqliteQueries.GetRunAsync(connection, transaction, runId, ct).ConfigureAwait(false);
        if (run?.State != HarnessRunState.Running) return 0;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$run", runId.Value);
        command.CommandText = """
            SELECT s.agent_id FROM agent_subagents s
            JOIN agent_subagent_families f ON f.root_run_id=s.root_run_id
            JOIN agent_runs root ON root.run_id=s.root_run_id
            JOIN agent_subagent_attempts a ON a.agent_id=s.agent_id
            WHERE a.run_id=$run AND s.canceled=0 AND f.canceled=0 AND root.terminal_at_utc IS NULL
            AND a.attempt_number=(SELECT max(latest.attempt_number) FROM agent_subagent_attempts latest WHERE latest.agent_id=s.agent_id)
            """;
        if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string agent) return 0;
        if (!await IsInboxBoundaryAsync(connection, transaction, runId, ct).ConfigureAwait(false)) return 0;
        command.Parameters.AddWithValue("$agent", agent);
        command.CommandText = """
            SELECT m.message_id,p.run_id,p.payload_id FROM agent_subagent_messages m
            JOIN agent_payloads p ON p.payload_id=m.payload_id
            WHERE m.agent_id=$agent AND m.consumed_run_id IS NULL ORDER BY p.created_at_utc,m.message_id LIMIT 8
            """;
        var references = new List<(string Id, RunId Run, PayloadId Payload)>();
        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                references.Add((reader.GetString(0), new(reader.GetString(1)), new(reader.GetString(2))));
        if (references.Count == 0) return 0;
        var messages = new List<SubagentMessage>();
        foreach (var reference in references)
        {
            var payload = await Payloads.ReadForModelAsync(connection, transaction, reference.Run, reference.Payload, ct).ConfigureAwait(false);
            messages.Add(new(reference.Id, Encoding.UTF8.GetString(payload.Plaintext.Span)));
        }
        var content = JsonSerializer.SerializeToUtf8Bytes(new { type = "untrusted_subagent_messages",
            messages = messages.Select(message => new { message_id = message.MessageId, content = message.Content }) });
        var commit = SubagentInboxDelivery.CreateCommit(run, content, messages.Count, now);
        HarnessSqliteWriteSet.ValidateCommit(run, commit);
        if (!await HarnessSqliteWriteSet.UpdateRunAsync(connection, transaction, commit, ct).ConfigureAwait(false))
            throw new HarnessStoreException("subagent_inbox_stale");
        await Payloads.InsertAsync(connection, transaction, runId, commit.Payloads, commit.Events,
            run.LastSequence + 1, commit.NextRun.UpdatedAt, ct).ConfigureAwait(false);
        await HarnessSqliteWriteSet.InsertEventsAsync(connection, transaction, runId, run.LastSequence + 1,
            commit.Events, commit.NextRun.UpdatedAt, ct).ConfigureAwait(false);
        command.CommandText = "UPDATE agent_subagent_messages SET consumed_run_id=$run WHERE agent_id=$agent AND message_id=$message AND consumed_run_id IS NULL";
        var messageParameter = command.Parameters.Add("$message", Microsoft.Data.Sqlite.SqliteType.Text);
        foreach (var reference in references)
        {
            messageParameter.Value = reference.Id;
            if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                throw new HarnessStoreException("subagent_inbox_stale");
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return references.Count;
    }
}
