using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Subagents;

namespace RuAgent.Harness.Persistence.Subagents;

public sealed partial class SqliteSubagentScopeStore
{
    /// <summary>Retained encrypted artifact evidence only; cannot restore delegation or model selection.</summary>
    public async Task<SubagentRuntimeScope?> GetRetainedForCleanupAsync(RunId rootId, RunId childId, CancellationToken ct)
    {
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM agent_internal_runs WHERE run_id=$child AND root_run_id=$root)";
        command.Parameters.AddWithValue("$child", childId.Value);
        command.Parameters.AddWithValue("$root", rootId.Value);
        if ((long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) != 1)
            throw new HarnessStoreException("subagent_cleanup_ownership_denied");
        var root = await HarnessSqliteQueries.GetRunAsync(connection, transaction, rootId, ct).ConfigureAwait(false)
            ?? throw new HarnessStoreException("subagent_root_not_found");
        // Do not validate a current execution policy here: cleanup may follow a
        // hardening or relation loss. The marker and authenticated child-bound
        // payload are retained proof; the Broker validates every physical target.
        return await ReadAsync(connection, transaction, childId, root.WorkspaceId, ct).ConfigureAwait(false);
    }
}
