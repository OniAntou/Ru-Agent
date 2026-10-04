using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Persistence;
using RuAgent.Runtime.Adapters.Bootstrap;

namespace RuBackend.Providers;

public sealed class SqliteProviderContinuationReader(HarnessSqliteOptions options, HarnessPayloadStore payloads)
{
    private const int MaximumRows = 256;
    private const int MaximumHistoryBytes = 512 * 1024;

    public async Task<IReadOnlyList<ProviderContinuation>> ReadAsync(
        RunId runId,
        ProviderBootstrapData bootstrap,
        CancellationToken cancellationToken)
    {
        var provider = bootstrap.Protocol switch
        {
            "openai_responses" => "openai",
            "anthropic_messages" => "anthropic",
            _ => throw new ProviderCatalogException("provider_continuation_invalid"),
        };
        await using var connection = await options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT model_step_id, payload_id
            FROM agent_events
            WHERE run_id = $run_id AND kind = 'model.provider_continuation_recorded'
            ORDER BY sequence DESC
            LIMIT $row_limit
            """;
        command.Parameters.AddWithValue("$run_id", runId.Value);
        command.Parameters.AddWithValue("$row_limit", MaximumRows + 1);
        var references = new List<ContinuationReference>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1))
                    throw new ProviderCatalogException("provider_continuation_invalid");
                references.Add(new ContinuationReference(new ModelStepId(reader.GetString(0)), new PayloadId(reader.GetString(1))));
            }
        }
        if (references.Count > MaximumRows) throw new ProviderCatalogException("model_history_too_large");

        references.Reverse();
        var result = new List<ProviderContinuation>(references.Count);
        var aggregateBytes = 0;
        foreach (var reference in references)
        {
            var content = await payloads.ReadProviderContinuationAsync(
                runId, reference.PayloadId, reference.ModelStepId, provider, bootstrap.Protocol, bootstrap.Model, cancellationToken)
                .ConfigureAwait(false);
            aggregateBytes = checked(aggregateBytes + content.Plaintext.Length);
            if (aggregateBytes > MaximumHistoryBytes) throw new ProviderCatalogException("model_history_too_large");
            result.Add(Parse(content.Plaintext, bootstrap, provider));
        }
        return result;
    }

    private static ProviderContinuation Parse(ReadOnlyMemory<byte> bytes, ProviderBootstrapData bootstrap, string provider = "openai")
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                || !root.TryGetProperty("output_items", out var outputItems)
                || outputItems.ValueKind != System.Text.Json.JsonValueKind.Array
                || outputItems.GetArrayLength() is < 1 or > 32)
                throw new System.Text.Json.JsonException("Continuation output shape is invalid.");
            var items = outputItems.EnumerateArray()
                .Select(item => System.Text.Json.Nodes.JsonNode.Parse(item.GetRawText()) as System.Text.Json.Nodes.JsonObject
                    ?? throw new System.Text.Json.JsonException("Continuation item is not an object."))
                .ToArray();
            return new ProviderContinuation(provider, bootstrap.Protocol, bootstrap.Model, 1, items);
        }
        catch (Exception error) when (error is System.Text.Json.JsonException
            or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            throw new ProviderCatalogException("provider_continuation_invalid");
        }
    }

    private sealed record ContinuationReference(ModelStepId ModelStepId, PayloadId PayloadId);
}
