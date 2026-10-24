using System.Text;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence.Privacy;

namespace RuAgent.Harness.Persistence.Context;

public sealed partial class SqliteModelContextSource : IModelContextSource
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly HarnessSqliteOptions _options;
    private readonly HarnessPayloadStore _payloadStore;
    private readonly SessionWorkStateStore? _workStates;
    private readonly ContextEntryCache? _entryCache;

    public SqliteModelContextSource(
        HarnessSqliteOptions options,
        HarnessPayloadStore payloadStore,
        SessionWorkStateStore? workStates = null,
        bool enableEntryCache = true)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _payloadStore = payloadStore ?? throw new ArgumentNullException(nameof(payloadStore));
        _workStates = workStates;
        _entryCache = enableEntryCache ? new ContextEntryCache() : null;
    }

    public Task<ModelContextSourceSnapshot> LoadAsync(
        RunId runId,
        CancellationToken cancellationToken) => LoadAsync(runId, 0, cancellationToken);

    private async Task<ModelContextSourceSnapshot> LoadAsync(RunId runId, int depth, CancellationToken cancellationToken)
    {
        _ = runId.Value;
        await using var connection = await _options.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var expectedBaseCount = await ReadBaseCountAsync(
                connection,
                runId,
                cancellationToken)
            .ConfigureAwait(false);
        var sessionBase = new List<ModelContextEntry>();
        var runLocal = new List<ModelContextEntry>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.payload_id,
                   p.kind,
                   p.audience,
                   p.encoding,
                   p.byte_cap,
                   p.protected_content,
                   p.protection_version,
                   p.nonce,
                   p.authentication_tag,
                   p.context_scope,
                   p.context_source_id,
                   p.context_sequence,
                   p.context_role,
                   p.context_entry_kind,
                   e.tool_call_id,
                   e.provider_call_id,
                   e.execution_id,
                   e.safe_code,
                   e.tool_outcome,
                   e.side_effects_possible
            FROM agent_payloads AS p
            LEFT JOIN agent_events AS e
              ON p.context_scope = 1
             AND e.run_id = p.run_id
             AND ('event:' || e.event_id) = p.context_source_id
            WHERE p.run_id = $run_id
              AND p.context_scope IS NOT NULL
            ORDER BY p.context_scope, p.context_sequence
            """;
        command.Parameters.AddWithValue("$run_id", runId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        string? keyStamp = null;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_entryCache is not null && keyStamp is null)
            {
                try { keyStamp = await _payloadStore.ReadModelCacheKeyStampAsync(cancellationToken).ConfigureAwait(false); }
                catch { _entryCache.Clear(); throw; }
            }
            var entry = await ReadEntryAsync(runId, reader, cancellationToken, keyStamp)
                .ConfigureAwait(false);
            if (reader.GetInt32(9) == 0)
            {
                sessionBase.Add(entry);
            }
            else
            {
                runLocal.Add(entry);
            }
        }

        // Original encrypted receipts remain durable. Derive private scope hints before replacing
        // their model projection, including for image/text-mode runs that never admit cache snapshots.
        var observedPaths = FileObservationPaths.Read(sessionBase.Concat(runLocal));
        var verificationEvidence = RunVerificationEvidence.Project(runLocal);
        await ApplyCompactionsAsync(
                connection,
                runId,
                runLocal,
                cancellationToken)
            .ConfigureAwait(false);

        await AttachToolImagesAsync(connection, runId, runLocal, cancellationToken).ConfigureAwait(false);

        if (sessionBase.Count != expectedBaseCount)
        {
            throw new HarnessStoreException("context_base_incomplete");
        }

        var inheritedPaths = await InheritSubagentContextAsync(connection, runId, sessionBase, depth, cancellationToken).ConfigureAwait(false);

        await AppendSessionWorkStateAsync(connection, runId, sessionBase, cancellationToken)
            .ConfigureAwait(false);

        return new ModelContextSourceSnapshot(sessionBase, runLocal, inheritedPaths.Concat(observedPaths).ToArray(), verificationEvidence);
    }

    private async Task AppendSessionWorkStateAsync(
        SqliteConnection connection,
        RunId runId,
        List<ModelContextEntry> sessionBase,
        CancellationToken cancellationToken)
    {
        if (_workStates is null) return;
        var run = await HarnessSqliteQueries.GetRunAsync(connection, null, runId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new HarnessStoreException("run_not_found");
        var snapshot = await _workStates.GetSnapshotAsync(run.SessionId, run.WorkspaceId, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null) return;
        var sequence = sessionBase.Count == 0 ? 1 : checked(sessionBase.Max(entry => entry.Sequence) + 1);
        sessionBase.Add(ModelContextEntry.Text(
            SessionWorkState.ContextSourceId, sequence, ModelContextRole.User,
            SessionWorkStateRenderer.Render(snapshot.State, snapshot.HasUnrepresentedReceipts)));
    }

    private async Task ApplyCompactionsAsync(
        SqliteConnection connection,
        RunId runId,
        List<ModelContextEntry> runLocal,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.compaction_id,
                   c.summary_payload_id,
                   c.covered_ranges_json,
                   p.kind,
                   p.audience,
                   p.encoding,
                   p.byte_cap,
                   p.protected_content,
                   p.protection_version,
                   p.nonce,
                   p.authentication_tag
            FROM agent_context_compactions AS c
            LEFT JOIN agent_payloads AS p
              ON p.run_id = c.run_id
             AND p.payload_id = c.summary_payload_id
            WHERE c.run_id = $run_id
            ORDER BY c.expected_run_version, c.compaction_id
            """;
        command.Parameters.AddWithValue("$run_id", runId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(3)
                || reader.IsDBNull(4)
                || reader.IsDBNull(5)
                || reader.IsDBNull(6)
                || reader.IsDBNull(7)
                || reader.IsDBNull(8)
                || reader.IsDBNull(9)
                || reader.IsDBNull(10))
            {
                throw new HarnessStoreException("context_compaction_payload_missing");
            }

            var payloadId = new PayloadId(reader.GetString(1));
            var metadata = new HarnessPayloadIntent(
                payloadId,
                (HarnessPayloadKind)reader.GetInt32(3),
                (HarnessPayloadAudience)reader.GetInt32(4),
                (HarnessPayloadEncoding)reader.GetInt32(5),
                reader.GetInt32(6),
                ReadOnlyMemory<byte>.Empty);
            if (metadata.Kind != HarnessPayloadKind.ModelContextSummary
                || metadata.Audience != HarnessPayloadAudience.ModelContext
                || metadata.Encoding != HarnessPayloadEncoding.Utf8Text
                || !HarnessPayloadStore.CanReadForModel(metadata))
            {
                throw new HarnessStoreException("context_compaction_payload_invalid");
            }

            var encrypted = new EncryptedHarnessPayload(
                reader.GetInt32(8),
                HarnessSqliteMapper.ReadBoundedBlob(reader, 9, 12, "context_compaction_payload_invalid", 12),
                HarnessSqliteMapper.ReadBoundedBlob(reader, 7, metadata.ByteCap, "context_compaction_payload_invalid"),
                HarnessSqliteMapper.ReadBoundedBlob(reader, 10, 16, "context_compaction_payload_invalid", 16));
            var plaintext = await _payloadStore.DecryptAsync(
                    runId,
                    metadata,
                    encrypted,
                    cancellationToken)
                .ConfigureAwait(false);
            string summaryText;
            try
            {
                summaryText = StrictUtf8.GetString(plaintext.Span);
            }
            catch (DecoderFallbackException exception)
            {
                throw new HarnessStoreException("context_compaction_payload_invalid", exception);
            }

            var ranges = ContextCompactionStoreCodec.DeserializeRanges(reader.GetString(2));
            var summary = ModelContextEntry.Summary(
                reader.GetString(0),
                ranges.Min(range => range.StartSequence),
                summaryText,
                ranges);
            ApplyCompaction(runLocal, summary);
        }
    }

    private static void ApplyCompaction(
        List<ModelContextEntry> runLocal,
        ModelContextEntry summary)
    {
        var ranges = summary.SummaryRanges!;
        var covered = runLocal
            .Where(entry => ranges.Any(range =>
                string.Equals(entry.SourceId, range.SourceId, StringComparison.Ordinal)
                && entry.Sequence >= range.StartSequence
                && entry.Sequence <= range.EndSequence))
            .ToHashSet();
        if (covered.Count == 0)
        {
            throw new HarnessStoreException("context_compaction_range_missing");
        }

        var insertionIndex = runLocal.FindIndex(entry => covered.Contains(entry));
        runLocal.RemoveAll(entry => covered.Contains(entry));
        if (insertionIndex < 0)
        {
            insertionIndex = runLocal.FindIndex(entry => entry.Sequence > summary.Sequence);
        }

        if (insertionIndex < 0 || insertionIndex > runLocal.Count)
        {
            insertionIndex = runLocal.Count;
        }

        runLocal.Insert(insertionIndex, summary);
    }

    private static async Task<int> ReadBaseCountAsync(
        SqliteConnection connection,
        RunId runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT base_context_captured_at_utc, base_context_entry_count
            FROM agent_runs
            WHERE run_id = $run_id
            """;
        command.Parameters.AddWithValue("$run_id", runId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new HarnessStoreException("context_run_not_found");
        }

        if (reader.IsDBNull(0))
        {
            throw new HarnessStoreException("context_base_missing");
        }

        return reader.GetInt32(1);
    }

    internal async Task<ModelContextEntry> ReadEntryAsync(
        RunId runId,
        SqliteDataReader reader,
        CancellationToken cancellationToken,
        string? validatedKeyStamp = null)
    {
        var payloadId = new PayloadId(reader.GetString(0));
        var metadata = new HarnessPayloadIntent(
            payloadId,
            (HarnessPayloadKind)reader.GetInt32(1),
            (HarnessPayloadAudience)reader.GetInt32(2),
            (HarnessPayloadEncoding)reader.GetInt32(3),
            reader.GetInt32(4),
            ReadOnlyMemory<byte>.Empty);
        if (!HarnessPayloadStore.CanReadForModel(metadata))
        {
            throw new HarnessStoreException("context_payload_forbidden");
        }

        var encrypted = new EncryptedHarnessPayload(
            reader.GetInt32(6),
            HarnessSqliteMapper.ReadBoundedBlob(reader, 7, 12, "context_payload_invalid", 12),
            HarnessSqliteMapper.ReadBoundedBlob(reader, 5, metadata.ByteCap, "context_payload_invalid"),
            HarnessSqliteMapper.ReadBoundedBlob(reader, 8, 16, "context_payload_invalid", 16));
        string? cacheKey = null;
        if (_entryCache is not null && validatedKeyStamp is not null)
        {
            try
            {
                _entryCache.UseKeyStamp(validatedKeyStamp);
                cacheKey = ContextEntryCache.Fingerprint(runId, validatedKeyStamp, reader);
                if (_entryCache.TryGet(cacheKey, out var cached))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Core.Diagnostics.HarnessPerformance.RecordContextCache(true);
                    return cached;
                }
                Core.Diagnostics.HarnessPerformance.RecordContextCache(false);
            }
            catch { _entryCache.Clear(); throw; }
        }
        var plaintext = await _payloadStore.DecryptAsync(
                runId,
                metadata,
                encrypted,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var entry = BuildEntry(
                reader.GetString(10),
                reader.GetInt64(11),
                (ModelContextRole)reader.GetInt32(12),
                (ModelContextEntryKind)reader.GetInt32(13),
                metadata,
                plaintext,
                reader);
            if (cacheKey is not null) _entryCache!.Add(cacheKey, entry, encrypted.Ciphertext.Length);
            return entry;
        }
        catch (Exception exception) when (exception is
            DecoderFallbackException
            or InvalidDataException
            or ArgumentException
            or InvalidOperationException)
        {
            throw new HarnessStoreException("context_payload_invalid", exception);
        }
    }

    private static ModelContextEntry BuildEntry(
        string sourceId,
        long sequence,
        ModelContextRole role,
        ModelContextEntryKind entryKind,
        HarnessPayloadIntent metadata,
        ReadOnlyMemory<byte> plaintext,
        SqliteDataReader reader)
    {
        var content = StrictUtf8.GetString(plaintext.Span);
        return entryKind switch
        {
            ModelContextEntryKind.Text when role is ModelContextRole.System or ModelContextRole.User =>
                ModelContextEntry.Text(sourceId, sequence, role, content),
            ModelContextEntryKind.AssistantStep when role == ModelContextRole.Assistant =>
                ModelContextEntry.Assistant(
                    sourceId,
                    sequence,
                    metadata.Kind == HarnessPayloadKind.AssistantStep
                        ? AssistantStepJsonCodec.Deserialize(plaintext)
                        : new AssistantStep(
                            content,
                            Array.Empty<ModelToolCall>(),
                            AssistantFinishReason.Stop,
                            estimatedInputTokens: 0)),
            ModelContextEntryKind.ToolResult when role == ModelContextRole.Tool =>
                BuildToolResult(sourceId, sequence, metadata, content, reader),
            _ => throw new InvalidDataException("context_entry_shape_invalid"),
        };
    }

    private static ModelContextEntry BuildToolResult(
        string sourceId,
        long sequence,
        HarnessPayloadIntent metadata,
        string content,
        SqliteDataReader reader)
    {
        if (reader.IsDBNull(14)
            || reader.IsDBNull(15)
            || reader.IsDBNull(16)
            || reader.IsDBNull(17)
            || reader.IsDBNull(18)
            || reader.IsDBNull(19))
        {
            throw new InvalidDataException("tool_context_metadata_missing");
        }

        var receipt = new ToolExecutionReceipt(
            new ExecutionId(reader.GetString(16)),
            new ToolCallId(reader.GetString(14)),
            (ToolExecutionOutcome)reader.GetInt32(18),
            reader.GetString(17),
            metadata.PayloadId,
            reader.GetBoolean(19),
            contentEncoding: metadata.Encoding,
            providerCallId: reader.GetString(15));
        return ModelContextEntry.ToolResult(sourceId, sequence, receipt, content);
    }
}
