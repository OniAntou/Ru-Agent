using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence.Privacy;
using RuAgent.Protocol;

namespace RuAgent.Harness.Persistence.Context;

public sealed partial class SqliteModelContextSource
{
    /// <summary>
    /// Attaches stored tool images to this run's own tool results (ADR 0026). Only the
    /// newest <see cref="ToolOutputImage.MaximumInContext"/> are decrypted; an older one is
    /// marked omitted so the request says so instead of dropping it silently. Runs after
    /// compaction and outside the entry cache, whose fingerprint does not cover images.
    /// </summary>
    private async Task AttachToolImagesAsync(
        SqliteConnection connection,
        RunId runId,
        List<ModelContextEntry> runLocal,
        CancellationToken cancellationToken)
    {
        var stored = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT payload_id FROM agent_payloads
                WHERE run_id = $run_id AND kind = $kind
                """;
            command.Parameters.AddWithValue("$run_id", runId.Value);
            command.Parameters.AddWithValue("$kind", (int)HarnessPayloadKind.ToolImage);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                stored.Add(reader.GetString(0));
            }
        }

        if (stored.Count == 0)
        {
            return;
        }

        var seen = 0;
        for (var index = runLocal.Count - 1; index >= 0; index--)
        {
            var entry = runLocal[index];
            if (entry.Kind != ModelContextEntryKind.ToolResult
                || entry.ToolReceipt?.ResultPayloadId is not { } resultPayloadId)
            {
                continue;
            }

            var imagePayloadId = ToolImagePayloadId.For(resultPayloadId);
            if (!stored.Contains(imagePayloadId.Value))
            {
                continue;
            }

            runLocal[index] = entry.WithToolImage(seen < ToolOutputImage.MaximumInContext
                ? await ReadToolImageAsync(connection, runId, imagePayloadId, cancellationToken).ConfigureAwait(false)
                : null);
            seen++;
        }
    }

    private async Task<ToolOutputImage> ReadToolImageAsync(
        SqliteConnection connection,
        RunId runId,
        PayloadId payloadId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT kind, audience, encoding, byte_cap, protected_content,
                   protection_version, nonce, authentication_tag
            FROM agent_payloads
            WHERE run_id = $run_id AND payload_id = $payload_id
            """;
        command.Parameters.AddWithValue("$run_id", runId.Value);
        command.Parameters.AddWithValue("$payload_id", payloadId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new HarnessStoreException("context_payload_invalid");
        }

        var metadata = new HarnessPayloadIntent(
            payloadId,
            (HarnessPayloadKind)reader.GetInt32(0),
            (HarnessPayloadAudience)reader.GetInt32(1),
            (HarnessPayloadEncoding)reader.GetInt32(2),
            reader.GetInt32(3),
            ReadOnlyMemory<byte>.Empty);
        if (metadata.Kind != HarnessPayloadKind.ToolImage || !HarnessPayloadStore.CanReadForModel(metadata))
        {
            throw new HarnessStoreException("context_payload_forbidden");
        }

        var encrypted = new EncryptedHarnessPayload(
            reader.GetInt32(5),
            HarnessSqliteMapper.ReadBoundedBlob(reader, 6, 12, "context_payload_invalid", 12),
            HarnessSqliteMapper.ReadBoundedBlob(reader, 4, metadata.ByteCap, "context_payload_invalid"),
            HarnessSqliteMapper.ReadBoundedBlob(reader, 7, 16, "context_payload_invalid", 16));
        var plaintext = await _payloadStore.DecryptAsync(runId, metadata, encrypted, cancellationToken)
            .ConfigureAwait(false);
        var mediaType = ToolOutputImage.SniffMediaType(plaintext.Span)
            ?? throw new HarnessStoreException("context_payload_invalid");
        try
        {
            return new ToolOutputImage(mediaType, plaintext);
        }
        catch (ArgumentException exception)
        {
            throw new HarnessStoreException("context_payload_invalid", exception);
        }
    }
}
