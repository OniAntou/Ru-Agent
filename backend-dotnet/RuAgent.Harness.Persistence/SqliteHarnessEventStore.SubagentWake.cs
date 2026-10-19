using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Subagents;

namespace RuAgent.Harness.Persistence;

public sealed partial class SqliteHarnessEventStore
{
    public async Task<IReadOnlyList<RunId>> ListPendingSubagentWaitsAsync(string? afterRootId, CancellationToken ct)
    {
        await using var connection = await _options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT w.root_run_id FROM agent_subagent_waits w JOIN agent_runs r ON r.run_id=w.root_run_id
            WHERE w.delivered=0 AND r.state=$waiting AND w.root_run_id>$after ORDER BY w.root_run_id LIMIT 128
            """;
        command.Parameters.AddWithValue("$waiting", (int)HarnessRunState.WaitingSubagents);
        command.Parameters.AddWithValue("$after", afterRootId ?? "");
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var runs = new List<RunId>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) runs.Add(new(reader.GetString(0)));
        return runs.AsReadOnly();
    }

    public async Task<bool> TryWakeSubagentWaitAsync(RunId rootId, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await _options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var run = await HarnessSqliteQueries.GetRunAsync(connection, transaction, rootId, ct).ConfigureAwait(false);
        if (run?.State != HarnessRunState.WaitingSubagents) return false;
        var stored = await LoadSubagentWaitAsync(connection, transaction, rootId, ct).ConfigureAwait(false);
        if (stored is null) return false;
        var previews = await LoadSubagentWaitResultsAsync(connection, transaction, rootId, stored.Intent, ct).ConfigureAwait(false);
        if (previews.Count == 0 && now < stored.Deadline) return false;
        var timedOut = previews.Count == 0;
        var output = SubagentWaitOutput(previews, timedOut);
        var commit = SubagentWaitCompletion.CreateCommit(run, stored.Intent, output, timedOut,
            new("payload_" + Guid.NewGuid().ToString("N")), new("evt_" + Guid.NewGuid().ToString("N")),
            new("evt_" + Guid.NewGuid().ToString("N")), now);
        HarnessSqliteWriteSet.ValidateCommit(run, commit);
        if (!await HarnessSqliteWriteSet.UpdateRunAsync(connection, transaction, commit, ct).ConfigureAwait(false))
            throw new HarnessStoreException("harness_cas_failed");
        await _payloadStore.InsertAsync(connection, transaction, rootId, commit.Payloads, commit.Events,
            run.LastSequence + 1, commit.NextRun.UpdatedAt, ct).ConfigureAwait(false);
        await HarnessSqliteWriteSet.InsertEventsAsync(connection, transaction, rootId, run.LastSequence + 1,
            commit.Events, commit.NextRun.UpdatedAt, ct).ConfigureAwait(false);
        await UpdateWorkStateFromReceiptsAsync(connection, transaction, commit, run, ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$root", rootId.Value);
        command.CommandText = "UPDATE agent_subagent_waits SET delivered=1 WHERE root_run_id=$root AND delivered=0";
        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) throw new HarnessStoreException("subagent_wait_stale");
        foreach (var preview in previews)
        {
            command.CommandText = "UPDATE agent_subagent_results SET delivered=1 WHERE agent_id=$agent AND result_version=$version AND delivered=0";
            command.Parameters.AddWithValue("$agent", preview.Result.AgentId);
            command.Parameters.AddWithValue("$version", preview.Result.Version);
            if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) throw new HarnessStoreException("subagent_result_stale");
            command.Parameters.RemoveAt("$agent");
            command.Parameters.RemoveAt("$version");
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    private sealed record StoredSubagentWait(SubagentWaitIntent Intent, DateTimeOffset Deadline);

    private async Task<StoredSubagentWait?> LoadSubagentWaitAsync(SqliteConnection connection, SqliteTransaction transaction,
        RunId rootId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT w.arguments_payload_id,w.deadline_at_utc,e.model_step_id,e.tool_call_id,e.execution_id,e.provider_call_id
            FROM agent_subagent_waits w JOIN agent_events e ON e.run_id=w.root_run_id AND e.tool_call_id=w.invocation_id
            WHERE w.root_run_id=$root AND w.delivered=0 AND e.kind=$requested LIMIT 2
            """;
        command.Parameters.AddWithValue("$root", rootId.Value);
        command.Parameters.AddWithValue("$requested", HarnessSqliteMapper.EventKind(HarnessEventKind.ToolExecutionRequested));
        PayloadId argumentsId;
        DateTimeOffset deadline;
        ModelStepId step;
        ToolCallId call;
        ExecutionId execution;
        string provider;
        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
            if (Enumerable.Range(0, 6).Any(reader.IsDBNull)) throw new HarnessStoreException("subagent_wait_call_invalid");
            argumentsId = new(reader.GetString(0));
            deadline = HarnessSqliteMapper.ParseUtc(reader.GetString(1));
            step = new(reader.GetString(2)); call = new(reader.GetString(3)); execution = new(reader.GetString(4)); provider = reader.GetString(5);
            if (await reader.ReadAsync(ct).ConfigureAwait(false)) throw new HarnessStoreException("subagent_wait_call_invalid");
        }
        var payload = await _payloadStore.ReadForModelAsync(connection, transaction, rootId, argumentsId, ct).ConfigureAwait(false);
        var envelope = ToolArgumentsCodec.Deserialize(payload.Plaintext);
        var intent = new SubagentWaitIntent(step, call, execution, provider, envelope.Arguments);
        if (await ReadWaitArgumentsAsync(connection, transaction, rootId, intent, ct).ConfigureAwait(false) != argumentsId)
            throw new HarnessStoreException("subagent_wait_call_invalid");
        return new(intent, deadline);
    }
}
