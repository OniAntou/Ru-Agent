using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;

namespace RuAgent.Harness.Persistence;

public sealed record AgentRunProviderSelection(
    string Backend,
    string Alias,
    string Protocol,
    string BaseUri,
    string Model,
    string? Account,
    string RequestOptionsJson,
    int? MaximumOutputTokens,
    string? ModelLabel = null,
    string? CallingMode = null,
    long? ContextWindowTokens = null,
    string ContextWindowSource = "unknown",
    string ContextWindowVersion = "unknown-v1",
    int OutputTokenReserve = AgentRunProviderSelection.DefaultOutputTokenReserve)
{
    public const int DefaultOutputTokenReserve = 8192;
}

public sealed record AgentRunProviderSelectionState(
    bool Exists,
    AgentRunProviderSelection? Selection);

public sealed partial class SqliteAgentRunProviderSelectionStore(HarnessSqliteOptions options)
{
    public async Task<AgentRunProviderSelectionState?> GetAsync(
        RunId runId,
        CancellationToken cancellationToken)
    {
        _ = runId.Value;
        await using var connection = await options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var state = await ReadAsync(connection, transaction: null, runId, cancellationToken).ConfigureAwait(false);
        if (state is { Selection: null })
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM agent_internal_runs WHERE run_id=$run) OR EXISTS(SELECT 1 FROM agent_subagent_attempts WHERE run_id=$run)";
            command.Parameters.AddWithValue("$run", runId.Value);
            if ((long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L) != 0)
                return null; // A delegated run must never pin current global defaults during recovery.
        }
        return state;
    }

    public async Task<AgentRunProviderSelectionState> PinAsync(
        RunId runId,
        AgentRunProviderSelection selection,
        CancellationToken cancellationToken)
    {
        _ = runId.Value;
        selection = Normalize(selection);
        Validate(selection);
        await using var connection = await options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadAsync(connection, transaction, runId, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            throw new HarnessStoreException("provider_selection_missing");
        }

        if (current.Selection is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return current;
        }

        var intent = await ReadIntentAsync(connection, transaction, runId, cancellationToken).ConfigureAwait(false);
        if (intent is not null && (intent.Backend != selection.Backend || intent.Model != selection.Model))
            throw new HarnessStoreException("provider_intent_mismatch");

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE agent_run_provider_selections
            SET backend = $backend,
                alias = $alias,
                protocol = $protocol,
                base_uri = $base_uri,
                model = $model,
                account = $account,
                request_options_json = $request_options_json,
                maximum_output_tokens = $maximum_output_tokens,
                model_label = $model_label,
                calling_mode = $calling_mode,
                context_window_tokens = $context_window_tokens,
                context_window_source = $context_window_source,
                context_window_version = $context_window_version,
                output_token_reserve = $output_token_reserve
            WHERE run_id = $run_id
              AND backend IS NULL
            """;
        command.Parameters.AddWithValue("$backend", selection.Backend);
        command.Parameters.AddWithValue("$alias", selection.Alias);
        command.Parameters.AddWithValue("$protocol", selection.Protocol);
        command.Parameters.AddWithValue("$base_uri", selection.BaseUri);
        command.Parameters.AddWithValue("$model", selection.Model);
        command.Parameters.AddWithValue("$account", (object?)selection.Account ?? DBNull.Value);
        command.Parameters.AddWithValue("$request_options_json", selection.RequestOptionsJson);
        command.Parameters.AddWithValue("$maximum_output_tokens", (object?)selection.MaximumOutputTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$model_label", selection.ModelLabel!);
        command.Parameters.AddWithValue("$calling_mode", selection.CallingMode!);
        command.Parameters.AddWithValue("$context_window_tokens", (object?)selection.ContextWindowTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$context_window_source", selection.ContextWindowSource);
        command.Parameters.AddWithValue("$context_window_version", selection.ContextWindowVersion);
        command.Parameters.AddWithValue("$output_token_reserve", selection.OutputTokenReserve);
        command.Parameters.AddWithValue("$run_id", runId.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new HarnessStoreException("provider_selection_unavailable");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AgentRunProviderSelectionState(true, selection);
    }

    private static async Task<AgentRunProviderSelectionState?> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        RunId runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT backend, alias, protocol, base_uri, model, account,
                   request_options_json, maximum_output_tokens, model_label,
                   calling_mode, context_window_tokens, context_window_source,
                   context_window_version, output_token_reserve
            FROM agent_run_provider_selections
            WHERE run_id = $run_id
            """;
        command.Parameters.AddWithValue("$run_id", runId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (reader.IsDBNull(0))
        {
            return new AgentRunProviderSelectionState(true, null);
        }

        if (Enumerable.Range(1, 4).Any(reader.IsDBNull)
            || reader.IsDBNull(6)
            || reader.IsDBNull(8)
            || reader.IsDBNull(9)
            || reader.IsDBNull(11)
            || reader.IsDBNull(12)
            || reader.IsDBNull(13))
        {
            throw new HarnessStoreException("provider_selection_invalid");
        }

        var selection = new AgentRunProviderSelection(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetInt32(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetInt64(10),
            reader.GetString(11),
            reader.GetString(12),
            reader.GetInt32(13));
        Validate(selection);
        return new AgentRunProviderSelectionState(true, selection);
    }

    private static void Validate(AgentRunProviderSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (!IsBounded(selection.Backend, 128)
            || !IsBounded(selection.Alias, 64)
            || !IsBounded(selection.Protocol, 32)
            || !Uri.TryCreate(selection.BaseUri, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(baseUri.UserInfo)
            || baseUri.Query.Length != 0
            || baseUri.Fragment.Length != 0
            || selection.BaseUri.Length > 2048
            || !IsBounded(selection.Model, 256)
            || !IsBounded(selection.ModelLabel!, 256)
            || selection.CallingMode is not ("native" or "text_selection")
            || (selection.ContextWindowTokens is < 1 or > 100_000_000)
            || !IsBounded(selection.ContextWindowSource, 64)
            || !IsBounded(selection.ContextWindowVersion, 64)
            || selection.OutputTokenReserve is < 1 or > 128_000
            || (selection.Account is not null && !IsCredentialAccount(selection.Account))
            || selection.RequestOptionsJson is not { Length: >= 2 and <= 8192 }
            || selection.MaximumOutputTokens is < 1 or > 8192)
        {
            throw new HarnessStoreException("provider_selection_invalid");
        }

        try
        {
            if (JsonNode.Parse(selection.RequestOptionsJson) is not JsonObject)
            {
                throw new HarnessStoreException("provider_selection_invalid");
            }
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new HarnessStoreException("provider_selection_invalid", exception);
        }
    }

    private static AgentRunProviderSelection Normalize(AgentRunProviderSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var callingMode = selection.CallingMode ?? (selection.Protocol == "chat_completions" ? "native" : "text_selection");
        return selection with
        {
            ModelLabel = selection.ModelLabel ?? selection.Model,
            CallingMode = callingMode,
        };
    }

    private static bool IsBounded(string value, int maximum) =>
        value is { Length: > 0 } && value.Length <= maximum && !value.Any(char.IsControl);

    private static bool IsCredentialAccount(string value) =>
        (value is { Length: >= 9 and <= 128 }
            && value.EndsWith("_api_key", StringComparison.Ordinal)
            && value[..^8].All(character =>
                character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_'))
        || CustomProviderAccountPattern().IsMatch(value);

    // A named custom provider's own account namespace (ADR 0034), disjoint from "<id>_api_key".
    [System.Text.RegularExpressions.GeneratedRegex("^custom_provider_[a-z][a-z0-9-]{0,31}_key$")]
    private static partial System.Text.RegularExpressions.Regex CustomProviderAccountPattern();
}
