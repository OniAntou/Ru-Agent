using System.Security.Cryptography;
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

/// <summary>
/// One steer as the authenticated client sees it; text is the user's own message, empty once
/// the run has read it (the transcript holds it).
/// </summary>
public sealed record RunSteerRecord(string SteerId, string State, string Text, DateTimeOffset CreatedAt, int ImageCount = 0);

/// <summary>
/// Durable steers of root runs (ADR 0056). A steer waits in an encrypted payload that no
/// event binds, and its images in <c>SteerImage</c> payloads no run-image listing reads; then
/// <see cref="DeliverAsync"/> moves them into the run at a step boundary in the same
/// transaction that records the steer delivered, so a steer is delivered at most once.
/// </summary>
public sealed class SqliteRunSteerStore(HarnessSqliteOptions options, HarnessPayloadStore payloads) : IRunSteerInbox
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public Task<RunSteerAppendOutcome> AppendAsync(
        RunId runId, string steerId, string text, DateTimeOffset now, CancellationToken ct) =>
        AppendAsync(runId, steerId, text, [], now, ct);

    /// <summary>
    /// Stores one steer with its image data URLs (already validated by the caller). A retry
    /// with the same id, text and images answers <see cref="RunSteerAppendOutcome.Duplicate"/>.
    /// </summary>
    public async Task<RunSteerAppendOutcome> AppendAsync(
        RunId runId, string steerId, string text, IReadOnlyList<string> images, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (!RunSteerContract.IsValidId(steerId) || text is null
            || string.IsNullOrWhiteSpace(text) && images.Count == 0
            || text.Length > RunSteerContract.MaxCharacters
            || images.Count > RunSteerContract.MaxImages
            || images.Any(image => string.IsNullOrEmpty(image) || !image.StartsWith("data:image/", StringComparison.Ordinal)))
            throw new HarnessStoreException("steer_invalid");
        byte[] bytes;
        byte[][] imageBytes;
        try
        {
            bytes = StrictUtf8.GetBytes(text);
            imageBytes = images.Select(image => StrictUtf8.GetBytes(image)).ToArray();
        }
        catch (EncoderFallbackException) { throw new HarnessStoreException("steer_invalid"); }
        var imageHashes = imageBytes.Select(image => Convert.ToHexStringLower(SHA256.HashData(image))).ToArray();

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
            // A retried send answers as the first one did; the same id with other text or
            // other images is a conflict.
            if (!string.Equals(existingRun, runId.Value, StringComparison.Ordinal))
                return RunSteerAppendOutcome.IdempotencyConflict;
            var existing = await payloads.ReadForModelAsync(connection, transaction, runId,
                new PayloadId(existingPayload!), ct).ConfigureAwait(false);
            var existingHashes = (await ReadImageRowsAsync(connection, transaction, steerId, ct).ConfigureAwait(false))
                .Select(row => row.Sha256).ToArray();
            return existing.Plaintext.Span.SequenceEqual(bytes) && existingHashes.SequenceEqual(imageHashes)
                ? RunSteerAppendOutcome.Duplicate
                : RunSteerAppendOutcome.IdempotencyConflict;
        }

        if (!RunSteerContract.AcceptsSteer(run.State)) return RunSteerAppendOutcome.RunNotSteerable;
        var waiting = new List<PayloadId>();
        command.CommandText = "SELECT payload_id FROM agent_run_steers WHERE run_id=$run AND delivered_event_id IS NULL";
        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                waiting.Add(new PayloadId(reader.GetString(0)));
        if (waiting.Count >= RunSteerContract.MaxPending) return RunSteerAppendOutcome.QueueFull;
        long waitingCharacters = text.Length;
        foreach (var payloadId in waiting)
        {
            var content = await payloads.ReadForModelAsync(connection, transaction, runId, payloadId, ct).ConfigureAwait(false);
            waitingCharacters += Encoding.UTF8.GetCharCount(content.Plaintext.Span);
        }

        if (waitingCharacters > RunSteerContract.MaxPendingCharacters) return RunSteerAppendOutcome.QueueFull;

        // Unbound: no event names this payload, so the model context never reads it before delivery.
        var textPayloadId = NewPayloadId();
        var intents = new List<HarnessPayloadIntent>
        {
            new(textPayloadId, HarnessPayloadKind.UserPrompt,
                HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                HarnessPayloadEncoding.Utf8Text, Math.Max(bytes.Length, 1), bytes),
        };
        var imagePayloadIds = new PayloadId[imageBytes.Length];
        for (var index = 0; index < imageBytes.Length; index++)
        {
            imagePayloadIds[index] = NewPayloadId();
            intents.Add(new HarnessPayloadIntent(imagePayloadIds[index], HarnessPayloadKind.SteerImage,
                HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                HarnessPayloadEncoding.Utf8Text, Math.Max(imageBytes[index].Length, 1), imageBytes[index]));
        }

        await payloads.InsertAsync(connection, transaction, runId, intents, [], 1, now, ct).ConfigureAwait(false);
        command.CommandText = """
            INSERT INTO agent_run_steers(steer_id,run_id,payload_id,created_at_utc)
            VALUES($steer,$run,$payload,$created)
            """;
        command.Parameters.AddWithValue("$payload", textPayloadId.Value);
        command.Parameters.AddWithValue("$created", HarnessSqliteMapper.Utc(now));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        if (imagePayloadIds.Length > 0)
        {
            using var insertImage = connection.CreateCommand();
            insertImage.Transaction = transaction;
            insertImage.CommandText = """
                INSERT INTO agent_run_steer_images(steer_id,ordinal,payload_id,sha256)
                VALUES($steer,$ordinal,$payload,$sha)
                """;
            insertImage.Parameters.AddWithValue("$steer", steerId);
            var ordinal = insertImage.Parameters.Add("$ordinal", SqliteType.Integer);
            var payload = insertImage.Parameters.Add("$payload", SqliteType.Text);
            var sha = insertImage.Parameters.Add("$sha", SqliteType.Text);
            for (var index = 0; index < imagePayloadIds.Length; index++)
            {
                ordinal.Value = index;
                payload.Value = imagePayloadIds[index].Value;
                sha.Value = imageHashes[index];
                await insertImage.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return RunSteerAppendOutcome.Accepted;
    }

    public async Task<IReadOnlyList<RunSteerRecord>> ListAsync(RunId runId, CancellationToken ct)
    {
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var run = await HarnessSqliteQueries.GetRunAsync(connection, transaction, runId, ct).ConfigureAwait(false);
        if (run is null) return [];
        var rows = new List<(string Id, string Payload, string Created, bool Delivered, int Images)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$run", runId.Value);
            command.CommandText = """
                SELECT s.steer_id,s.payload_id,s.created_at_utc,s.delivered_event_id IS NOT NULL,
                    (SELECT count(*) FROM agent_run_steer_images i WHERE i.steer_id=s.steer_id)
                FROM agent_run_steers s
                WHERE s.run_id=$run ORDER BY s.created_at_utc,s.steer_id
                """;
            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetInt32(4)));
        }

        var result = new List<RunSteerRecord>(rows.Count);
        foreach (var row in rows)
        {
            var state = row.Delivered ? RunSteerContract.StateDelivered
                : run.State.IsTerminal() ? RunSteerContract.StateReturned
                : RunSteerContract.StatePending;
            // A read steer lives in the transcript; only steers the sender may still need to
            // resend carry their text, which keeps the list bounded.
            var text = string.Empty;
            if (!row.Delivered)
            {
                var content = await payloads.ReadForModelAsync(connection, transaction, runId, new PayloadId(row.Payload), ct)
                    .ConfigureAwait(false);
                text = Encoding.UTF8.GetString(content.Plaintext.Span);
            }

            result.Add(new RunSteerRecord(row.Id, state, text, HarnessSqliteMapper.ParseUtc(row.Created), row.Images));
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

    /// <summary>
    /// Whether this steer id is already stored, for this run or another. A retried send is
    /// answered from the stored row without running the prompt hooks again or asking whether
    /// a run in hand still accepts new steers (ADR 0056).
    /// </summary>
    public async Task<bool> ExistsAsync(string steerId, CancellationToken ct)
    {
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$steer", steerId);
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM agent_run_steers WHERE steer_id=$steer)";
        return (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) != 0;
    }

    /// <summary>
    /// The state <see cref="ListAsync"/> reports for one stored steer, or null when this run
    /// holds no such steer. A retry answers with it instead of repeating the first reply's
    /// <c>pending</c>, which would outlive the run's reading of the steer.
    /// </summary>
    public async Task<string?> FindStateAsync(RunId runId, string steerId, CancellationToken ct)
    {
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var run = await HarnessSqliteQueries.GetRunAsync(connection, transaction, runId, ct).ConfigureAwait(false);
        if (run is null) return null;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$steer", steerId);
        command.Parameters.AddWithValue("$run", runId.Value);
        command.CommandText = "SELECT delivered_event_id IS NOT NULL FROM agent_run_steers WHERE steer_id=$steer AND run_id=$run";
        if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not long delivered) return null;
        return delivered != 0 ? RunSteerContract.StateDelivered
            : run.State.IsTerminal() ? RunSteerContract.StateReturned
            : RunSteerContract.StatePending;
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
            var images = new List<string>();
            var sources = new List<PayloadId>();
            foreach (var image in await ReadImageRowsAsync(connection, transaction, id, ct).ConfigureAwait(false))
            {
                var imageContent = await payloads.ReadForModelAsync(connection, transaction, runId, image.PayloadId, ct)
                    .ConfigureAwait(false);
                images.Add(Encoding.UTF8.GetString(imageContent.Plaintext.Span));
                sources.Add(image.PayloadId);
            }

            steers.Add(new DeliveredSteer(id, Encoding.UTF8.GetString(content.Plaintext.Span), images, sources));
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

        // Each image now lives in the run as the UserImage whose id derives from its waiting
        // copy, so the waiting copy goes: an image is stored once, and the steer's image rows
        // still name it through RunSteerContract.DeliveredImagePayloadId.
        await DeleteWaitingImagesAsync(connection, transaction, runId,
            steers.SelectMany(steer => steer.ImageSources ?? []).ToArray(), ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return steers;
    }

    private static PayloadId NewPayloadId() => new("payload_" + Guid.NewGuid().ToString("N"));

    private static async Task DeleteWaitingImagesAsync(
        SqliteConnection connection, SqliteTransaction transaction, RunId runId, IReadOnlyList<PayloadId> waiting, CancellationToken ct)
    {
        foreach (var chunk in waiting.Chunk(128))
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            var names = chunk.Select((_, index) => "$image" + index).ToArray();
            delete.CommandText =
                $"DELETE FROM agent_payloads WHERE run_id=$run AND kind=$kind AND payload_id IN ({string.Join(',', names)})";
            delete.Parameters.AddWithValue("$run", runId.Value);
            delete.Parameters.AddWithValue("$kind", (int)HarnessPayloadKind.SteerImage);
            for (var index = 0; index < chunk.Length; index++) delete.Parameters.AddWithValue(names[index], chunk[index].Value);
            await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<(PayloadId PayloadId, string Sha256)>> ReadImageRowsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string steerId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$steer", steerId);
        command.CommandText = "SELECT payload_id,sha256 FROM agent_run_steer_images WHERE steer_id=$steer ORDER BY ordinal";
        var rows = new List<(PayloadId, string)>();
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add((new PayloadId(reader.GetString(0)), reader.GetString(1)));
        return rows;
    }
}
