using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;

namespace RuAgent.Harness.Persistence;

public sealed partial class SqliteAgentRunProviderSelectionStore
{
    internal static async Task CopyPinnedAsync(SqliteConnection connection, SqliteTransaction transaction,
        RunId source, RunId target, CancellationToken ct)
    {
        var pin = await ReadAsync(connection, transaction, source, ct).ConfigureAwait(false);
        if (pin?.Selection is null) throw new HarnessStoreException("subagent_model_pin_missing");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE agent_run_provider_selections
            SET (backend,alias,protocol,base_uri,model,account,request_options_json,maximum_output_tokens,
                 model_label,calling_mode,context_window_tokens,context_window_source,context_window_version,output_token_reserve)
              = (SELECT backend,alias,protocol,base_uri,model,account,request_options_json,maximum_output_tokens,
                        model_label,calling_mode,context_window_tokens,context_window_source,context_window_version,output_token_reserve
                 FROM agent_run_provider_selections WHERE run_id=$source)
            WHERE run_id=$target AND backend IS NULL
            """;
        command.Parameters.AddWithValue("$source", source.Value);
        command.Parameters.AddWithValue("$target", target.Value);
        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            throw new HarnessStoreException("subagent_model_pin_conflict");
    }
}
