using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Harness.Persistence.Subagents;

public sealed partial class SqliteSubagentResultStore
{
    public Task<SubagentResult> PublishTerminalAsync(RunId rootId, string agentId, RunId runId, CancellationToken ct) =>
        PublishAsync(rootId, agentId, runId, null, ct);

    public Task<SubagentResult> PublishFailureAsync(RunId rootId, string agentId, RunId runId, string code, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(code) || code.Length > 64 || code.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '_'))
            throw new HarnessStoreException("subagent_result_code_invalid");
        return PublishAsync(rootId, agentId, runId, code, ct);
    }

    private async Task<SubagentResult> PublishAsync(RunId rootId, string agentId, RunId runId, string? failureCode, CancellationToken ct)
    {
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$root", rootId.Value);
        command.Parameters.AddWithValue("$agent", agentId);
        command.Parameters.AddWithValue("$run", runId.Value);
        command.CommandText = """
            SELECT a.attempt_number,s.role FROM agent_subagents s JOIN agent_subagent_attempts a ON a.agent_id=s.agent_id
            WHERE s.root_run_id=$root AND s.agent_id=$agent AND a.run_id=$run
            """;
        long version;
        SubagentRole role;
        using (var owner = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await owner.ReadAsync(ct).ConfigureAwait(false)) throw new HarnessStoreException("subagent_not_found");
            version = owner.GetInt64(0);
            role = (SubagentRole)owner.GetInt32(1);
        }
        var run = await HarnessSqliteQueries.GetRunAsync(connection, transaction, runId, ct).ConfigureAwait(false)
            ?? throw new HarnessStoreException("subagent_not_found");
        if (!run.State.IsTerminal()) throw new HarnessStoreException("subagent_result_not_terminal");
        if (failureCode is not null && (role != SubagentRole.Coding || run.State != HarnessRunState.Completed))
            throw new HarnessStoreException("subagent_result_failure_denied");
        var state = failureCode is null ? run.State : HarnessRunState.Failed;
        var code = failureCode ?? run.TerminalErrorCode;
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue("$code", (object?)code ?? DBNull.Value);
        command.CommandText = """
            SELECT v.result_version,v.payload_id,v.delivered,o.state,o.code
            FROM agent_subagent_results v LEFT JOIN agent_subagent_result_outcomes o ON o.agent_id=v.agent_id AND o.run_id=v.run_id
            WHERE v.agent_id=$agent AND v.run_id=$run
            """;
        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (reader.IsDBNull(3)) throw new HarnessStoreException("subagent_result_outcome_missing");
                var savedState = (HarnessRunState)reader.GetInt32(3);
                SubagentResultRow.Validate(reader.GetInt64(0), version, savedState, run.State);
                var savedCode = reader.IsDBNull(4) ? null : reader.GetString(4);
                if (failureCode is not null && (savedState != state || savedCode != failureCode))
                    throw new HarnessStoreException("subagent_result_conflict");
                return new(agentId, reader.GetInt64(0), runId, savedState, savedCode, new(reader.GetString(1)), reader.GetInt32(2) != 0);
            }
        }
        if (version > SubagentLimits.MaxAttempts) throw new HarnessStoreException("subagent_result_limit");
        if (failureCode is null && role == SubagentRole.Coding && run.State == HarnessRunState.Completed)
        {
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM agent_subagent_change_sets WHERE run_id=$run)";
            if ((long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) != 1)
                throw new HarnessStoreException("subagent_changes_missing");
        }
        PayloadId payloadId;
        command.CommandText = """
            SELECT p.payload_id FROM agent_payloads p JOIN agent_events e ON e.run_id=p.run_id AND e.payload_id=p.payload_id
            WHERE p.run_id=$run AND p.kind=$text_kind AND e.kind=$completed_kind ORDER BY e.sequence DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$text_kind", (int)HarnessPayloadKind.AssistantText);
        command.Parameters.AddWithValue("$completed_kind", HarnessSqliteMapper.EventKind(HarnessEventKind.ModelStepCompleted));
        var finalPayload = failureCode is null ? await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string : null;
        if (finalPayload is not null) payloadId = new(finalPayload);
        else
        {
            if (state == HarnessRunState.Completed) throw new HarnessStoreException("subagent_result_payload_missing");
            payloadId = new("payload_" + Guid.NewGuid().ToString("N"));
            var text = Encoding.UTF8.GetBytes($"Subagent result ended with {state}: {code ?? "subagent_canceled"}.");
            await payloads.InsertAsync(connection, transaction, runId,
                [new(payloadId, HarnessPayloadKind.AssistantText, HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                    HarnessPayloadEncoding.Utf8Text, text.Length, text)], [], 1, run.UpdatedAt, ct).ConfigureAwait(false);
        }
        command.CommandText = """
            INSERT INTO agent_subagent_results(agent_id,result_version,run_id,payload_id) VALUES($agent,$version,$run,$payload);
            INSERT INTO agent_subagent_result_outcomes(agent_id,run_id,state,code) VALUES($agent,$run,$state,$code);
            """;
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$payload", payloadId.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new(agentId, version, runId, state, code, payloadId, false);
    }
}
