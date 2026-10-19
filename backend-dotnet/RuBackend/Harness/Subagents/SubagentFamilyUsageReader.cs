using System.Security.Cryptography;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence;

namespace RuBackend.Harness.Subagents;

public sealed record SubagentFamilyUsage(long ModelSteps, long ReportedSteps,
    long? InputTokens, long? OutputTokens, long? TotalTokens, bool IsComplete);

public sealed record SubagentUsageSnapshot(SubagentFamilyUsage Family, IReadOnlyDictionary<string, SubagentFamilyUsage> Runs);

public sealed class SubagentFamilyUsageReader(HarnessSqliteOptions options, HarnessPayloadStore payloads)
{
    public async Task<SubagentFamilyUsage> ReadAsync(RunId rootId, CancellationToken ct) =>
        (await ReadDetailedAsync(rootId, ct).ConfigureAwait(false)).Family;

    public async Task<SubagentUsageSnapshot> ReadDetailedAsync(RunId rootId, CancellationToken ct)
    {
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$root", rootId.Value);
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM agent_runs r WHERE r.run_id=$root
                AND NOT EXISTS(SELECT 1 FROM agent_internal_runs i WHERE i.run_id=r.run_id)
                AND NOT EXISTS(SELECT 1 FROM agent_subagent_attempts a WHERE a.run_id=r.run_id))
            """;
        if ((long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) != 1)
            throw new HarnessStoreException("subagent_root_not_found");

        // The marker remains authoritative for attribution after relation loss.
        // Group inside one read snapshot and stream the bounded usage payloads;
        // do not retain an ever-growing set of event/step IDs or trust model text.
        command.CommandText = """
            WITH family(run_id) AS (
                SELECT $root
                UNION SELECT run_id FROM agent_internal_runs WHERE root_run_id=$root
                UNION SELECT a.run_id FROM agent_subagent_attempts a
                    JOIN agent_subagents s ON s.agent_id=a.agent_id WHERE s.root_run_id=$root
            )
            SELECT e.run_id,e.model_step_id,
                sum(e.kind='model.step_started'),sum(e.kind='model.usage_recorded'),
                max(CASE WHEN e.kind='model.usage_recorded' THEN e.payload_id END)
            FROM agent_events e JOIN family f ON f.run_id=e.run_id
            WHERE e.kind IN ('model.step_started','model.usage_recorded')
            GROUP BY e.run_id,e.model_step_id
            """;
        var family = new UsageAccumulator();
        var perRun = new Dictionary<string, UsageAccumulator>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var runId = reader.GetString(0);
            if (!perRun.TryGetValue(runId, out var run)) perRun.Add(runId, run = new());
            var starts = reader.GetInt64(2);
            ModelUsage? usage = null;
            if (!reader.IsDBNull(1) && starts == 1 && reader.GetInt64(3) == 1 && !reader.IsDBNull(4))
                usage = await ReadUsageAsync(new(runId), new(reader.GetString(4)), ct).ConfigureAwait(false);
            family.Record(starts, usage);
            run.Record(starts, usage);
        }
        return new(family.Snapshot(), perRun.ToDictionary(p => p.Key, p => p.Value.Snapshot(), StringComparer.Ordinal));
    }

    private sealed class UsageAccumulator
    {
        private long _steps, _reported;
        private long? _input, _output, _total;
        private bool _complete = true;
        public void Record(long starts, ModelUsage? usage)
        {
            _steps = checked(_steps + starts);
            if (usage is null) { _complete = false; return; }
            _reported++;
            Add(ref _input, usage.InputTokens);
            Add(ref _output, usage.OutputTokens);
            Add(ref _total, usage.TotalTokens);
            _complete &= usage.InputTokens is not null && usage.OutputTokens is not null && usage.TotalTokens is not null;
        }
        public SubagentFamilyUsage Snapshot() => new(_steps, _reported, _input, _output, _total, _complete && _steps > 0);
    }

    private async Task<ModelUsage?> ReadUsageAsync(RunId runId, PayloadId payloadId, CancellationToken ct)
    {
        try
        {
            var content = await payloads.ReadForClientAsync(runId, payloadId, ct).ConfigureAwait(false);
            if (content.Kind != HarnessPayloadKind.ModelUsage || content.Encoding != HarnessPayloadEncoding.Json
                || content.Plaintext.Length > 32 * 1024) return null;
            using var document = JsonDocument.Parse(content.Plaintext);
            return ModelUsage.Read(document.RootElement);
        }
        catch (Exception error) when (error is HarnessStoreException or CryptographicException or JsonException or ArgumentException or InvalidDataException)
        {
            // Missing/corrupt usage cannot become a zero cost assertion. Storage
            // availability failures and cancellation still reach their owner.
            return null;
        }
    }

    private static void Add(ref long? sum, int? value)
    {
        if (value is { } count) sum = checked(sum.GetValueOrDefault() + count);
    }
}
