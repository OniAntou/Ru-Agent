using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;

namespace RuAgent.Harness.Persistence;

public sealed partial class SqliteAgentRunProviderSelectionStore
{
    public async Task<RunProviderIntent?> GetIntentAsync(RunId runId, CancellationToken ct)
    {
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await ReadIntentAsync(connection, null, runId, ct).ConfigureAwait(false);
    }

    private static async Task<RunProviderIntent?> ReadIntentAsync(SqliteConnection connection,
        SqliteTransaction? transaction, RunId runId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT backend,model FROM agent_run_provider_intents WHERE run_id=$run";
        command.Parameters.AddWithValue("$run", runId.Value);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        try { return new RunProviderIntent(reader.GetString(0), reader.GetString(1)); }
        catch (ArgumentException error) { throw new HarnessStoreException("provider_intent_invalid", error); }
    }
}
