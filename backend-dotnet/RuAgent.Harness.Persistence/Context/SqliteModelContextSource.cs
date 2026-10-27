using System.Text;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence.Privacy;

namespace RuAgent.Harness.Persistence.Context;

public sealed partial class SqliteModelContextSource : IModelContextSource
{
    internal const string ContextEntriesSql = """
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
         AND e.event_id = substr(p.context_source_id, 7)
         AND e.run_id = p.run_id
         AND substr(p.context_source_id, 1, 6) = 'event:'
        WHERE p.run_id = $run_id
          AND p.context_scope IS NOT NULL
        ORDER BY p.context_scope, p.context_sequence
        """;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly HarnessSqliteOptions _options;
    private readonly HarnessPayloadStore _payloadStore;
    private readonly SessionWorkStateStore? _workStates;
    private readonly ContextEntryCache? _entryCache;
    private readonly ActiveContextCache? _activeCache;

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
        _activeCache = enableEntryCache ? new ActiveContextCache() : null;
    }

    public Task<ModelContextSourceSnapshot> LoadAsync(
        RunId runId,
        CancellationToken cancellationToken) => LoadAsync(runId, 0, cancellationToken);

    private Task<ModelContextSourceSnapshot> LoadAsync(RunId runId, int depth, CancellationToken cancellationToken) =>
        LoadProjectedAsync(runId, depth, cancellationToken);

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
