using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Harness.Persistence.Subagents;

public sealed record SubagentMessage(string MessageId, string Content, string? ContinuationErrorCode = null);

public sealed partial class SqliteSubagentStore
{
    public async Task<bool> HasUnscheduledMessagesAsync(RunId root, string agentId, CancellationToken ct)
    {
        await using var connection = await Options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM agent_subagent_messages m JOIN agent_subagents s ON s.agent_id=m.agent_id
            WHERE s.root_run_id=$root AND s.agent_id=$agent AND m.consumed_run_id IS NULL AND m.continuation_run_id IS NULL AND m.continuation_error_code IS NULL)
            """;
        command.Parameters.AddWithValue("$root", root.Value);
        command.Parameters.AddWithValue("$agent", agentId);
        return (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) != 0;
    }

    public async Task AppendMessageAsync(RunId rootRunId, string agentId, string messageId, string message, CancellationToken ct)
    {
        if (!SubagentContractValidator.IsValidId(messageId) || string.IsNullOrWhiteSpace(message)
            || message.Length > SubagentLimits.MaxTaskCharacters) throw new HarnessStoreException("subagent_message_invalid");
        byte[] bytes;
        try { bytes = new UTF8Encoding(false, true).GetBytes(message); }
        catch (EncoderFallbackException) { throw new HarnessStoreException("subagent_message_invalid"); }
        await using var connection = await Options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var attempt = await ReadLatestAttemptAsync(connection, transaction, rootRunId, agentId, ct).ConfigureAwait(false)
            ?? throw new HarnessStoreException("subagent_not_found");
        var root = await HarnessSqliteQueries.GetRunAsync(connection, transaction, rootRunId, ct).ConfigureAwait(false);
        if (root is null || root.State.IsTerminal() || attempt.Agent.Canceled) throw new HarnessStoreException("subagent_root_inactive");
        var payloadId = MessagePayloadId(agentId, messageId);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$agent", agentId);
        command.Parameters.AddWithValue("$message", messageId);
        command.CommandText = "SELECT p.run_id FROM agent_subagent_messages m JOIN agent_payloads p ON p.payload_id=m.payload_id WHERE m.agent_id=$agent AND m.message_id=$message";
        if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is string existingRun)
        {
            var existing = await Payloads.ReadForModelAsync(connection, transaction, new RunId(existingRun), payloadId, ct).ConfigureAwait(false);
            if (!existing.Plaintext.Span.SequenceEqual(bytes)) throw new HarnessStoreException("idempotency_conflict");
            return;
        }
        command.CommandText = "SELECT count(*) FROM agent_subagent_messages WHERE agent_id=$agent AND consumed_run_id IS NULL";
        if ((long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) >= 64)
            throw new HarnessStoreException("subagent_message_queue_full");
        command.CommandText = "SELECT COALESCE(sum(length(p.protected_content)),0) FROM agent_subagent_messages m JOIN agent_payloads p ON p.payload_id=m.payload_id WHERE m.agent_id=$agent AND m.consumed_run_id IS NULL";
        if ((long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) + bytes.Length > 1024 * 1024)
            throw new HarnessStoreException("subagent_message_queue_full");
        var payload = new HarnessPayloadIntent(payloadId, HarnessPayloadKind.UserPrompt,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text, Math.Max(bytes.Length, 1), bytes);
        await Payloads.InsertAsync(connection, transaction, attempt.Run.RunId, [payload], [], 1, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        command.CommandText = "INSERT INTO agent_subagent_messages(agent_id,message_id,payload_id) VALUES($agent,$message,$payload)";
        command.Parameters.AddWithValue("$payload", payloadId.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SubagentMessage>> ReadPendingMessagesAsync(RunId rootRunId, string agentId, CancellationToken ct)
    {
        await using var connection = await Options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT m.message_id,p.run_id,p.payload_id,m.continuation_error_code FROM agent_subagent_messages m
            JOIN agent_subagents s ON s.agent_id=m.agent_id JOIN agent_payloads p ON p.payload_id=m.payload_id
            WHERE s.root_run_id=$root AND m.agent_id=$agent AND m.consumed_run_id IS NULL ORDER BY p.created_at_utc,m.message_id LIMIT 64
            """;
        command.Parameters.AddWithValue("$root", rootRunId.Value);
        command.Parameters.AddWithValue("$agent", agentId);
        var references = new List<(string Message, RunId Run, PayloadId Payload, string? Error)>();
        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) references.Add((reader.GetString(0), new(reader.GetString(1)), new(reader.GetString(2)), reader.IsDBNull(3) ? null : reader.GetString(3)));
        var messages = new List<SubagentMessage>();
        foreach (var reference in references)
        {
            var content = await Payloads.ReadForModelAsync(connection, transaction, reference.Run, reference.Payload, ct).ConfigureAwait(false);
            messages.Add(new(reference.Message, Encoding.UTF8.GetString(content.Plaintext.Span), reference.Error));
        }
        return messages.AsReadOnly();
    }

    private static PayloadId MessagePayloadId(string agent, string message) => new("payload_" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(agent + ":" + message)))[..32].ToLowerInvariant());
}
