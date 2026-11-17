using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Persistence;
using RuAgent.Runtime.Adapters.Bootstrap;

namespace RuBackend.Providers;

public sealed class SqliteProviderContinuationReader(HarnessSqliteOptions options, HarnessPayloadStore payloads)
{
    /// <summary>
    /// The most retained assistant steps one request may replay. Every retained step is at
    /// least one message of the projected history, so the message window bounds it. It used to
    /// be 256 while that window held far more single-call tool steps: a long
    /// run on a large-context reasoning model passed 256 retained steps before compaction
    /// triggered and failed with provider_continuation_invalid.
    /// </summary>
    internal const int MaximumRows = RuAgent.Runtime.Adapters.Models.ModelInputLimits.MaxMessages;

    /// <summary>
    /// The most provider-continuation payloads (a model's reasoning and output items, replayed
    /// with each retained assistant turn) one request may carry.
    /// </summary>
    /// <remarks>
    /// This is the same ceiling the message history itself answers to. It used to be 512 KiB,
    /// sixteen times smaller, while the history it accompanies may be 8 MiB: a short run on a
    /// reasoning model at high effort (about 50 KiB of reasoning a step) crossed it after ten
    /// steps and failed with a code that blamed the prompt. The reasoning is part of the
    /// history, so it gets the history's budget, and the request's own 64 MiB bound still holds.
    /// </remarks>
    internal const int MaximumHistoryBytes = RuAgent.Runtime.Adapters.Models.ModelInputLimits.MaxHistoryBytes;

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
            "chat_completions" when bootstrap.ReplayChatReasoning => "chat",
            _ => throw new ProviderCatalogException("provider_continuation_invalid"),
        };
        // A natively continued run replays its source run's steps too (ADR 0049). The source is
        // the one its own captured marker names, and both runs share provider, model and account,
        // so the items go back to the provider that produced them. No other run's state is read.
        RunId sourceRunId;
        try
        {
            sourceRunId = await payloads.ReadNativeContinuationSourceAsync(runId, cancellationToken).ConfigureAwait(false)
                ?? runId;
        }
        catch (HarnessStoreException)
        {
            throw new ProviderCatalogException("provider_continuation_invalid");
        }
        await using var connection = await options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var eventParameters = retainedAssistantEventIds.Select((_, index) => "$step_event_" + index).ToArray();
        command.CommandText = $"""
            SELECT assistant_step.event_id, continuation.model_step_id, continuation.payload_id, assistant_step.run_id
            FROM agent_events AS assistant_step
            JOIN agent_events AS continuation
              ON continuation.run_id = assistant_step.run_id
             AND continuation.model_step_id = assistant_step.model_step_id
             AND continuation.kind = 'model.provider_continuation_recorded'
            WHERE assistant_step.run_id IN ($run_id, $source_run_id)
              AND assistant_step.kind = 'model.step_completed'
              AND assistant_step.event_id IN ({string.Join(",", eventParameters)})
            ORDER BY assistant_step.run_id = $run_id, assistant_step.sequence
            LIMIT {MaximumRows + 1}
            """;
        command.Parameters.AddWithValue("$run_id", runId.Value);
        command.Parameters.AddWithValue("$source_run_id", sourceRunId.Value);
        for (var index = 0; index < retainedAssistantEventIds.Count; index++)
            command.Parameters.AddWithValue(eventParameters[index], retainedAssistantEventIds[index]);
        var references = new List<ContinuationReference>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2))
                    throw new ProviderCatalogException("provider_continuation_invalid");
                references.Add(new ContinuationReference(reader.GetString(0), new ModelStepId(reader.GetString(1)),
                    new PayloadId(reader.GetString(2)), new RunId(reader.GetString(3))));
            }
        }
        if (!HasExactAssociations(references, retainedAssistantEventIds))
            throw new ProviderCatalogException("provider_continuation_invalid");
        var result = new List<ProviderContinuation>(references.Count);
        var aggregateBytes = 0;
        foreach (var reference in references)
        {
            var content = await payloads.ReadProviderContinuationAsync(
                reference.RunId, reference.PayloadId, reference.ModelStepId, provider, bootstrap.Protocol, bootstrap.Model,
                cancellationToken)
                .ConfigureAwait(false);
            aggregateBytes = AddWithinBudget(aggregateBytes, content.Plaintext.Length);
            result.Add(Parse(content.Plaintext, bootstrap, provider));
        }
        return result;
    }

    /// <summary>Adds one continuation to the running total, refusing a total over the budget.</summary>
    internal static int AddWithinBudget(int aggregateBytes, int nextBytes)
    {
        var total = checked(aggregateBytes + nextBytes);
        if (total > MaximumHistoryBytes) throw new ProviderCatalogException("model_history_too_large");
        return total;
    }

    internal static bool HasExactAssociations(
        IReadOnlyList<ContinuationReference> references,
        IReadOnlyList<string> retainedEventIds)
    {
        if (references.Count != retainedEventIds.Count || references.Count > MaximumRows) return false;
        // A set keeps the check linear now that a request may retain up to MaximumRows steps.
        var retained = new HashSet<string>(retainedEventIds, StringComparer.Ordinal);
        var associated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in references)
        {
            if (!retained.Contains(reference.AssistantEventId)
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

    internal sealed record ContinuationReference(string AssistantEventId, ModelStepId ModelStepId, PayloadId PayloadId,
        RunId RunId);
}
