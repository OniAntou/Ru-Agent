using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence.Privacy;

namespace RuAgent.Harness.Persistence;

public sealed partial class HarnessPayloadStore
{
    internal async Task<HarnessPayloadContent> ReadForClientAsync(SqliteConnection connection,
        SqliteTransaction transaction, RunId runId, PayloadId payloadId, CancellationToken ct)
    {
        var stored = await LoadAsync(connection, transaction, runId, payloadId, requireActiveRun: false, ct).ConfigureAwait(false);
        if (!CanReadForClient(stored.Intent)) throw new HarnessStoreException("payload_client_forbidden");
        var plaintext = await _cryptography.DecryptAsync(runId, stored.Intent, stored.Encrypted, ct).ConfigureAwait(false);
        return Content(runId, stored.Intent, plaintext);
    }

    internal async Task<HarnessPayloadContent> ReadForModelAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RunId runId,
        PayloadId payloadId,
        CancellationToken cancellationToken)
    {
        var stored = await LoadAsync(connection, transaction, runId, payloadId,
            requireActiveRun: false, cancellationToken).ConfigureAwait(false);
        if (!CanReadForModel(stored.Intent)) throw new HarnessStoreException("payload_model_forbidden");
        var plaintext = await _cryptography.DecryptAsync(runId, stored.Intent,
            stored.Encrypted, cancellationToken).ConfigureAwait(false);
        return Content(runId, stored.Intent, plaintext);
    }

    internal async Task InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RunId runId,
        IReadOnlyList<HarnessPayloadIntent> payloads,
        IReadOnlyList<HarnessEventIntent> events,
        long firstEventSequence,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        if (firstEventSequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(firstEventSequence));
        }

        foreach (var payload in payloads)
        {
            var protectedPayload = await ProtectAsync(runId, payload, cancellationToken)
                .ConfigureAwait(false);
            var context = RunContextBinding(
                protectedPayload.Intent,
                events,
                firstEventSequence);
            await InsertProtectedAsync(
                    connection,
                    transaction,
                    runId,
                    protectedPayload,
                    context,
                    occurredAt,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One-time recovery read on the caller's transaction. The payload marker
    /// and any caller-owned marker (for example the recovery mapping row) then
    /// commit or roll back together, so a crash between markers cannot leave a
    /// recovery marked consumed but never handed back.
    /// </summary>
    internal async Task<HarnessPayloadContent> ReadRecoveryOnceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RunId runId,
        PayloadId payloadId,
        DateTimeOffset consumedAt,
        CancellationToken cancellationToken)
    {
        var stored = await LoadAsync(
                connection,
                transaction,
                runId,
                payloadId,
                requireActiveRun: true,
                cancellationToken)
            .ConfigureAwait(false);
        if (stored.Intent.Kind != HarnessPayloadKind.RecoveryOpaque)
        {
            throw new HarnessStoreException("payload_recovery_forbidden");
        }

        if (stored.ConsumedAt is not null)
        {
            throw new HarnessStoreException("payload_recovery_consumed");
        }

        var plaintext = await _cryptography.DecryptAsync(
                runId,
                stored.Intent,
                stored.Encrypted,
                cancellationToken)
            .ConfigureAwait(false);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE agent_payloads
            SET consumed_at_utc = $consumed_at
            WHERE run_id = $run_id
              AND payload_id = $payload_id
              AND consumed_at_utc IS NULL
            """;
        update.Parameters.AddWithValue("$consumed_at", HarnessSqliteMapper.Utc(consumedAt));
        update.Parameters.AddWithValue("$run_id", runId.Value);
        update.Parameters.AddWithValue("$payload_id", payloadId.Value);
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new HarnessStoreException("payload_recovery_consumed");
        }

        return Content(runId, stored.Intent, plaintext);
    }

    private async Task<HarnessPayloadContent> ReadAsync(
        RunId runId,
        PayloadId payloadId,
        Func<HarnessPayloadIntent, bool> audienceCheck,
        string forbiddenCode,
        CancellationToken cancellationToken)
    {
        var options = RequireOptions();
        await using var connection = await options.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var stored = await LoadAsync(
                connection,
                null,
                runId,
                payloadId,
                requireActiveRun: false,
                cancellationToken)
            .ConfigureAwait(false);
        if (!audienceCheck(stored.Intent))
        {
            throw new HarnessStoreException(forbiddenCode);
        }

        var plaintext = await _cryptography.DecryptAsync(
                runId,
                stored.Intent,
                stored.Encrypted,
                cancellationToken)
            .ConfigureAwait(false);
        return Content(runId, stored.Intent, plaintext);
    }

    private static async Task<StoredPayload> LoadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        RunId runId,
        PayloadId payloadId,
        bool requireActiveRun,
        CancellationToken cancellationToken)
    {
        _ = runId.Value;
        _ = payloadId.Value;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT p.kind,
                   p.audience,
                   p.encoding,
                   p.byte_cap,
                   p.protected_content,
                   p.protection_version,
                   p.nonce,
                   p.authentication_tag,
                   p.consumed_at_utc
            FROM agent_payloads AS p
            INNER JOIN agent_runs AS r ON r.run_id = p.run_id
            WHERE p.run_id = $run_id
              AND p.payload_id = $payload_id
              {(requireActiveRun ? "AND r.terminal_at_utc IS NULL" : string.Empty)}
            """;
        command.Parameters.AddWithValue("$run_id", runId.Value);
        command.Parameters.AddWithValue("$payload_id", payloadId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new HarnessStoreException(
                requireActiveRun ? "payload_recovery_unavailable" : "payload_not_found");
        }

        var byteCap = reader.GetInt32(3);
        var ciphertextLength = reader.GetBytes(4, 0, null, 0, 0);
        var nonceLength = reader.GetBytes(6, 0, null, 0, 0);
        var tagLength = reader.GetBytes(7, 0, null, 0, 0);
        if (byteCap is <= 0 or > HarnessPayloadIntent.MaximumByteCap
            || ciphertextLength > byteCap
            || nonceLength != 12
            || tagLength != 16)
        {
            throw new HarnessStoreException("payload_envelope_invalid");
        }

        var nonce = new byte[checked((int)nonceLength)];
        var ciphertext = new byte[checked((int)ciphertextLength)];
        var authenticationTag = new byte[checked((int)tagLength)];
        reader.GetBytes(6, 0, nonce, 0, nonce.Length);
        reader.GetBytes(4, 0, ciphertext, 0, ciphertext.Length);
        reader.GetBytes(7, 0, authenticationTag, 0, authenticationTag.Length);

        var intent = new HarnessPayloadIntent(
            payloadId,
            (HarnessPayloadKind)reader.GetInt32(0),
            (HarnessPayloadAudience)reader.GetInt32(1),
            (HarnessPayloadEncoding)reader.GetInt32(2),
            byteCap,
            ReadOnlyMemory<byte>.Empty);
        var encrypted = new EncryptedHarnessPayload(
            reader.GetInt32(5),
            nonce,
            ciphertext,
            authenticationTag);
        return new StoredPayload(
            intent,
            encrypted,
            reader.IsDBNull(8)
                ? null
                : HarnessSqliteMapper.ParseUtc(reader.GetString(8)));
    }

    private static ContextBinding? RunContextBinding(
        HarnessPayloadIntent intent,
        IReadOnlyList<HarnessEventIntent> events,
        long firstEventSequence)
    {
        // Context scope is the model-read contract: SqliteModelContextSource throws
        // context_payload_forbidden on any scoped payload the model may not read, so
        // audit-only rows (an InternalOnly AssistantStep, for example) must never bind.
        if (!HarnessPayloadStore.CanReadForModel(intent))
        {
            return null;
        }

        ModelContextRole role;
        ModelContextEntryKind entryKind;
        switch (intent.Kind)
        {
            case HarnessPayloadKind.UserPrompt:
                role = ModelContextRole.User;
                entryKind = ModelContextEntryKind.Text;
                break;
            case HarnessPayloadKind.AssistantText:
            case HarnessPayloadKind.AssistantStep:
                role = ModelContextRole.Assistant;
                entryKind = ModelContextEntryKind.AssistantStep;
                break;
            case HarnessPayloadKind.ToolOutput:
                role = ModelContextRole.Tool;
                entryKind = ModelContextEntryKind.ToolResult;
                break;
            case HarnessPayloadKind.ModelContextNote:
                role = ModelContextRole.System;
                entryKind = ModelContextEntryKind.Text;
                break;
            case HarnessPayloadKind.RuntimeContextSnapshot:
            case HarnessPayloadKind.SkillInvocation:
                role = ModelContextRole.User;
                entryKind = ModelContextEntryKind.Text;
                break;
            default:
                return null;
        }

        var matches = events
            .Select((item, index) => (Event: item, Index: index))
            .Where(item => item.Event.PayloadId == intent.PayloadId)
            .ToArray();
        if (matches.Length == 0)
        {
            return null;
        }

        if (matches.Length != 1)
        {
            throw new HarnessStoreException("context_payload_event_ambiguous");
        }

        var match = matches[0];
        // Only a Host-created invocation note gets the reserved provenance marker.
        // Ordinary model/tool/user text cannot manufacture an active workflow snapshot.
        var skillInvocation = intent.Kind == HarnessPayloadKind.SkillInvocation
            && match.Event.SafeCode == "skill_invoked";
        return new ContextBinding(
            Scope: 1,
            skillInvocation
                ? "run:invoked-skills:" + match.Event.EventId.Value
                : intent.Kind == HarnessPayloadKind.RuntimeContextSnapshot
                ? RuAgent.Harness.Core.Prompting.RuntimeContextSnapshot.SourcePrefix + match.Event.EventId.Value
                : $"event:{match.Event.EventId.Value}",
            checked(firstEventSequence + match.Index),
            skillInvocation ? ModelContextRole.User : role,
            entryKind);
    }

    private static async Task InsertProtectedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RunId runId,
        ProtectedHarnessPayload payload,
        ContextBinding? context,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO agent_payloads(
                payload_id,
                run_id,
                kind,
                audience,
                encoding,
                byte_cap,
                protected_content,
                protection_version,
                nonce,
                authentication_tag,
                context_scope,
                context_source_id,
                context_sequence,
                context_role,
                context_entry_kind,
                created_at_utc)
            VALUES (
                $payload_id,
                $run_id,
                $kind,
                $audience,
                $encoding,
                $byte_cap,
                $content,
                $protection_version,
                $nonce,
                $authentication_tag,
                $context_scope,
                $context_source_id,
                $context_sequence,
                $context_role,
                $context_entry_kind,
                $created_at)
            """;
        command.Parameters.AddWithValue("$payload_id", payload.Intent.PayloadId.Value);
        command.Parameters.AddWithValue("$run_id", runId.Value);
        command.Parameters.AddWithValue("$kind", (int)payload.Intent.Kind);
        command.Parameters.AddWithValue("$audience", (int)payload.Intent.Audience);
        command.Parameters.AddWithValue("$encoding", (int)payload.Intent.Encoding);
        command.Parameters.AddWithValue("$byte_cap", payload.Intent.ByteCap);
        command.Parameters.AddWithValue("$content", payload.Encrypted.Ciphertext.ToArray());
        command.Parameters.AddWithValue("$protection_version", payload.Encrypted.ProtectionVersion);
        command.Parameters.AddWithValue("$nonce", payload.Encrypted.Nonce.ToArray());
        command.Parameters.AddWithValue(
            "$authentication_tag",
            payload.Encrypted.AuthenticationTag.ToArray());
        command.Parameters.AddWithValue(
            "$context_scope",
            context is null ? DBNull.Value : context.Scope);
        command.Parameters.AddWithValue(
            "$context_source_id",
            context is null ? DBNull.Value : context.SourceId);
        command.Parameters.AddWithValue(
            "$context_sequence",
            context is null ? DBNull.Value : context.Sequence);
        command.Parameters.AddWithValue(
            "$context_role",
            context is null ? DBNull.Value : (int)context.Role);
        command.Parameters.AddWithValue(
            "$context_entry_kind",
            context is null ? DBNull.Value : (int)context.EntryKind);
        command.Parameters.AddWithValue("$created_at", HarnessSqliteMapper.Utc(occurredAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record StoredPayload(
        HarnessPayloadIntent Intent,
        EncryptedHarnessPayload Encrypted,
        DateTimeOffset? ConsumedAt);

    private sealed record ContextBinding(
        int Scope,
        string SourceId,
        long Sequence,
        ModelContextRole Role,
        ModelContextEntryKind EntryKind);
}
