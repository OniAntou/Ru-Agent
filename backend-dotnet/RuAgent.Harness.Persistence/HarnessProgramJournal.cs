using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Programs;

namespace RuAgent.Harness.Persistence;

public sealed partial class HarnessPayloadStore : IProgramJournal
{
    async Task<JsonObject?> IProgramJournal.ReadAsync(string run, string key, CancellationToken ct)
    {
        var runId = new RunId(run);
        var payloadId = ProgramPayloadId(runId, key);
        await using var connection = await RequireOptions().OpenConnectionAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        await using var exists = connection.CreateCommand();
        exists.Transaction = transaction;
        exists.CommandText = "SELECT EXISTS(SELECT 1 FROM agent_payloads WHERE run_id=$run AND payload_id=$payload)";
        exists.Parameters.AddWithValue("$run", run);
        exists.Parameters.AddWithValue("$payload", payloadId.Value);
        if ((long)(await exists.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) == 0) return null;
        var content = await ReadForClientAsync(connection, transaction, runId, payloadId, ct).ConfigureAwait(false);
        return (JsonObject)JsonNode.Parse(content.Plaintext.Span)!;
    }

    async Task<ProgramJournalWrite> IProgramJournal.CreateAsync(string run, string key, JsonObject value, CancellationToken ct)
    {
        var runId = new RunId(run);
        var payloadId = ProgramPayloadId(runId, key);
        await using var connection = await RequireOptions().OpenConnectionAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var root = await HarnessSqliteQueries.GetRunAsync(connection, transaction, runId, ct).ConfigureAwait(false);
        if (root is null || root.State != HarnessRunState.Running
            || await HarnessSqliteQueries.SessionDeletedAsync(connection, transaction, root.SessionId, ct).ConfigureAwait(false))
            throw new HarnessStoreException("program_run_inactive");
        await using var exists = connection.CreateCommand();
        exists.Transaction = transaction;
        exists.CommandText = "SELECT EXISTS(SELECT 1 FROM agent_payloads WHERE run_id=$run AND payload_id=$payload)";
        exists.Parameters.AddWithValue("$run", run);
        exists.Parameters.AddWithValue("$payload", payloadId.Value);
        if ((long)(await exists.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) != 0)
        {
            var previous = await ReadForClientAsync(connection, transaction, runId, payloadId, ct).ConfigureAwait(false);
            return new(false, (JsonObject)JsonNode.Parse(previous.Plaintext.Span)!);
        }
        var intent = new HarnessPayloadIntent(payloadId, HarnessPayloadKind.ToolOutput,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Json, 4 * 1024 * 1024, Encoding.UTF8.GetBytes(value.ToJsonString()));
        var protectedPayload = await ProtectAsync(runId, intent, ct).ConfigureAwait(false);
        // No context binding and no root-version update: this is a nested audit
        // record, not a model message or a competing coordinator transition.
        await InsertProtectedAsync(connection, transaction, runId, protectedPayload, null, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new(true, (JsonObject)JsonNode.Parse(protectedPayload.Intent.Plaintext.Span)!);
    }

    Task<bool> IProgramJournal.ReserveToolAsync(string run, string key, CancellationToken ct) =>
        ReserveProgramToolsAsync(run, key, 1, batch: false, ct);

    Task<bool> IProgramJournal.ReserveToolsAsync(string run, string batchKey, int count, CancellationToken ct)
    {
        if (count is < 1 or > ProgramToolContract.MaximumParallelCalls)
            throw new HarnessStoreException("program_batch_budget_invalid");
        return ReserveProgramToolsAsync(run, batchKey, count, batch: true, ct);
    }

    private async Task<bool> ReserveProgramToolsAsync(string run, string key, int amount, bool batch, CancellationToken ct)
    {
        var runId = new RunId(run);
        var options = RequireOptions();
        AgentRun root;
        await using (var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            var found = await HarnessSqliteQueries.GetRunAsync(connection, transaction, runId, ct).ConfigureAwait(false);
            if (found is null || found.State != HarnessRunState.Running
                || await HarnessSqliteQueries.SessionDeletedAsync(connection, transaction, found.SessionId, ct).ConfigureAwait(false)) return false;
            root = found;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$root", run);
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM agent_internal_runs WHERE run_id=$root) OR EXISTS(SELECT 1 FROM agent_subagent_families WHERE root_run_id=$root AND canceled=1)";
            if ((long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) != 0) return false;
            // An empty family activates the existing shared window accounting;
            // subsequent root batches and children use the same 128/256 pool.
            command.CommandText = "INSERT INTO agent_subagent_families(root_run_id) VALUES($root) ON CONFLICT DO NOTHING";
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        return await new SqliteSubagentBudgetStore(options).ReserveAmountAsync(runId, root.BudgetWindow,
            "program_" + Digest((batch ? "batch|" : "call|") + key), SubagentBudgetKind.ToolCall, amount,
            root.ModelStepsInWindow, root.ToolCallsInWindow, ct).ConfigureAwait(false);
    }

    private static PayloadId ProgramPayloadId(RunId run, string key)
    {
        if (key.Length is < 1 or > 128 || !key.StartsWith("program_", StringComparison.Ordinal)
            || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) throw new HarnessStoreException("program_key_invalid");
        return new PayloadId("payload_" + Digest(run.Value + "|" + key)[..32]);
    }
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
