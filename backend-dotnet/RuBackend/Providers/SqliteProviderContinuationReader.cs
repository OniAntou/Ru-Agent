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
        IReadOnlyList<string> retainedAssistantEventIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(retainedAssistantEventIds);
        if (retainedAssistantEventIds.Count == 0) return Array.Empty<ProviderContinuation>();
        if (retainedAssistantEventIds.Count > MaximumRows
            || retainedAssistantEventIds.Distinct(StringComparer.Ordinal).Count() != retainedAssistantEventIds.Count
            || retainedAssistantEventIds.Any(id => !IsEventId(id)))
            throw new ProviderCatalogException("provider_continuation_invalid");
        var provider = bootstrap.Protocol switch
        {
            "openai_responses" => "openai",
            "anthropic_messages" => "anthropic",
            _ => throw new ProviderCatalogException("provider_continuation_invalid"),
        };
        await using var connection = await options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var eventParameters = retainedAssistantEventIds.Select((_, index) => "$step_event_" + index).ToArray();
        command.CommandText = $"""
            SELECT assistant_step.event_id, continuation.model_step_id, continuation.payload_id
            FROM agent_events AS assistant_step
            JOIN agent_events AS continuation
              ON continuation.run_id = assistant_step.run_id
             AND continuation.model_step_id = assistant_step.model_step_id
             AND continuation.kind = 'model.provider_continuation_recorded'
            WHERE assistant_step.run_id = $run_id
              AND assistant_step.kind = 'model.step_completed'
              AND assistant_step.event_id IN ({string.Join(",", eventParameters)})
            ORDER BY assistant_step.sequence
            LIMIT {MaximumRows + 1}
            """;
        command.Parameters.AddWithValue("$run_id", runId.Value);
        for (var index = 0; index < retainedAssistantEventIds.Count; index++)
            command.Parameters.AddWithValue(eventParameters[index], retainedAssistantEventIds[index]);
        var references = new List<ContinuationReference>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2))
                    throw new ProviderCatalogException("provider_continuation_invalid");
                references.Add(new ContinuationReference(reader.GetString(0), new ModelStepId(reader.GetString(1)), new PayloadId(reader.GetString(2))));
            }
        }
        if (!HasExactAssociations(references, retainedAssistantEventIds))
            throw new ProviderCatalogException("provider_continuation_invalid");
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

    internal static bool HasExactAssociations(
        IReadOnlyList<ContinuationReference> references,
        IReadOnlyList<string> retainedEventIds)
    {
        if (references.Count != retainedEventIds.Count || references.Count > MaximumRows) return false;
        var associated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in references)
        {
            if (!retainedEventIds.Contains(reference.AssistantEventId, StringComparer.Ordinal)
                || !associated.Add(reference.AssistantEventId)) return false;
        }
        return associated.Count == retainedEventIds.Count;
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

    private static bool IsEventId(string value) =>
        value is { Length: 36 }
        && value.StartsWith("evt_", StringComparison.Ordinal)
        && value.AsSpan(4).ToString().All(character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    internal sealed record ContinuationReference(string AssistantEventId, ModelStepId ModelStepId, PayloadId PayloadId);
}
