using System.Text;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Steering;

namespace RuAgent.Harness.Persistence;

public enum RunSteerAppendOutcome
{
    Accepted,
    Duplicate,
    RunNotFound,
    RunNotSteerable,
    QueueFull,
    IdempotencyConflict,
}

/// <summary>One steer as the authenticated client sees it; text is the user's own message.</summary>
public sealed record RunSteerRecord(string SteerId, string State, string Text, DateTimeOffset CreatedAt);

/// <summary>
/// Durable steers of root runs (ADR 0056). A steer waits in an encrypted payload that no
/// event binds, then <see cref="DeliverAsync"/> moves it into the run at a step boundary in
/// the same transaction that records it delivered, so a steer is delivered at most once.
/// </summary>
public sealed class SqliteRunSteerStore(HarnessSqliteOptions options, HarnessPayloadStore payloads) : IRunSteerInbox
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<RunSteerAppendOutcome> AppendAsync(
        RunId runId, string steerId, string text, DateTimeOffset now, CancellationToken ct)
    {
        if (!RunSteerContract.IsValidId(steerId) || string.IsNullOrWhiteSpace(text)
            || text.Length > RunSteerContract.MaxCharacters)
            throw new HarnessStoreException("steer_invalid");
        byte[] bytes;
        try { bytes = StrictUtf8.GetBytes(text); }
        catch (EncoderFallbackException) { throw new HarnessStoreException("steer_invalid"); }

        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var run = await HarnessSqliteQueries.GetRunAsync(connection, transaction, runId, ct).ConfigureAwait(false);
        if (run is null) return RunSteerAppendOutcome.RunNotFound;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$steer", steerId);
        command.Parameters.AddWithValue("$run", runId.Value);
        command.CommandText = "SELECT run_id,payload_id FROM agent_run_steers WHERE steer_id=$steer";
        string? existingRun = null, existingPayload = null;
        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                existingRun = reader.GetString(0);
                existingPayload = reader.GetString(1);
            }
        }

        if (existingRun is not null)
        {
            // A retried send answers as the first one did; the same id with other text is a conflict.
            if (!string.Equals(existingRun, runId.Value, StringComparison.Ordinal))
                return RunSteerAppendOutcome.IdempotencyConflict;
            var existing = await payloads.ReadForModelAsync(connection, transaction, runId,
                new PayloadId(existingPayload!), ct).ConfigureAwait(false);
            return existing.Plaintext.Span.SequenceEqual(bytes)
                ? RunSteerAppendOutcome.Duplicate
                : RunSteerAppendOutcome.IdempotencyConflict;
        }

        if (!RunSteerContract.AcceptsSteer(run.State)) return RunSteerAppendOutcome.RunNotSteerable;
        command.CommandText = "SELECT count(*) FROM agent_run_steers WHERE run_id=$run AND delivered_event_id IS NULL";
        if ((long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) >= RunSteerContract.MaxPending)
            return RunSteerAppendOutcome.QueueFull;

        // Unbound: no event names this payload, so the model context never reads it before delivery.
        var payloadId = new PayloadId("payload_" + Guid.NewGuid().ToString("N"));
        var payload = new HarnessPayloadIntent(payloadId, HarnessPayloadKind.UserPrompt,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text, Math.Max(bytes.Length, 1), bytes);
        await payloads.InsertAsync(connection, transaction, runId, [payload], [], 1, now, ct).ConfigureAwait(false);
        command.CommandText = """
            INSERT INTO agent_run_steers(steer_id,run_id,payload_id,created_at_utc)
            VALUES($steer,$run,$payload,$created)
            """;
        command.Parameters.AddWithValue("$payload", payloadId.Value);
        command.Parameters.AddWithValue("$created", HarnessSqliteMapper.Utc(now));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return RunSteerAppendOutcome.Accepted;
    }

    public async Task<IReadOnlyList<RunSteerRecord>> ListAsync(RunId runId, CancellationToken ct)
    {
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var run = await HarnessSqliteQueries.GetRunAsync(connection, transaction, runId, ct).ConfigureAwait(false);
        if (run is null) return [];
        var rows = new List<(string Id, string Payload, string Created, bool Delivered)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$run", runId.Value);
            command.CommandText = """
                SELECT steer_id,payload_id,created_at_utc,delivered_event_id IS NOT NULL FROM agent_run_steers
                WHERE run_id=$run ORDER BY created_at_utc,steer_id
                """;
            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3)));
        }

        var result = new List<RunSteerRecord>(rows.Count);
        foreach (var row in rows)
        {
            var content = await payloads.ReadForModelAsync(connection, transaction, runId, new PayloadId(row.Payload), ct)
                .ConfigureAwait(false);
            var state = row.Delivered ? RunSteerContract.StateDelivered
                : run.State.IsTerminal() ? RunSteerContract.StateReturned
                : RunSteerContract.StatePending;
            result.Add(new RunSteerRecord(row.Id, state, Encoding.UTF8.GetString(content.Plaintext.Span),
                HarnessSqliteMapper.ParseUtc(row.Created)));
        }

        return result;
    }

    public async Task<bool> HasPendingAsync(RunId runId, CancellationToken ct)
    {
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$run", runId.Value);
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM agent_run_steers WHERE run_id=$run AND delivered_event_id IS NULL)";
        return (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) != 0;
    }

    public async Task<IReadOnlyList<DeliveredSteer>> DeliverAsync(RunId runId, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var run = await HarnessSqliteQueries.GetRunAsync(connection, transaction, runId, ct).ConfigureAwait(false);
        if (run?.State != HarnessRunState.Running) return [];
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$run", runId.Value);
        command.Parameters.AddWithValue("$limit", RunSteerContract.MaxPending);
        command.CommandText = """
            SELECT steer_id,payload_id FROM agent_run_steers
            WHERE run_id=$run AND delivered_event_id IS NULL ORDER BY created_at_utc,steer_id LIMIT $limit
            """;
        var pending = new List<(string Id, PayloadId Payload)>();
        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                pending.Add((reader.GetString(0), new PayloadId(reader.GetString(1))));
        if (pending.Count == 0) return [];
        if (!await HarnessStepBoundary.IsAtStepBoundaryAsync(connection, transaction, payloads, runId, ct).ConfigureAwait(false))
            return [];

        var steers = new List<DeliveredSteer>(pending.Count);
        foreach (var (id, payloadId) in pending)
        {
            var content = await payloads.ReadForModelAsync(connection, transaction, runId, payloadId, ct).ConfigureAwait(false);
            steers.Add(new DeliveredSteer(id, Encoding.UTF8.GetString(content.Plaintext.Span)));
        }

        var commit = RunSteerDelivery.CreateCommit(run, steers, now);
        HarnessSqliteWriteSet.ValidateCommit(run, commit);
        if (!await HarnessSqliteWriteSet.UpdateRunAsync(connection, transaction, commit, ct).ConfigureAwait(false))
            throw new HarnessStoreException("run_steer_stale");
        await payloads.InsertAsync(connection, transaction, runId, commit.Payloads, commit.Events,
            run.LastSequence + 1, commit.NextRun.UpdatedAt, ct).ConfigureAwait(false);
        await HarnessSqliteWriteSet.InsertEventsAsync(connection, transaction, runId, run.LastSequence + 1,
            commit.Events, commit.NextRun.UpdatedAt, ct).ConfigureAwait(false);

        command.CommandText = """
            UPDATE agent_run_steers SET delivered_event_id=$event,delivered_at_utc=$at
            WHERE steer_id=$steer AND delivered_event_id IS NULL
            """;
        var steerParameter = command.Parameters.Add("$steer", SqliteType.Text);
        var eventParameter = command.Parameters.Add("$event", SqliteType.Text);
        command.Parameters.AddWithValue("$at", HarnessSqliteMapper.Utc(commit.NextRun.UpdatedAt));
        for (var index = 0; index < steers.Count; index++)
        {
            steerParameter.Value = steers[index].SteerId;
            eventParameter.Value = commit.Events[index].EventId.Value;
            if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                throw new HarnessStoreException("run_steer_stale");
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return steers;
    }
}
