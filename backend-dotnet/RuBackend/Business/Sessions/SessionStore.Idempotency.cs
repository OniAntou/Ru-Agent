using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuBackend.Business.Sessions;

public sealed partial class SessionStore
{
    public async Task<SessionAppendOutcome> AppendIfAbsentAsync(
        string sessionId,
        SessionMessage message,
        CancellationToken cancellationToken)
    {
        if (!IsValidSessionId(sessionId))
        {
            return SessionAppendOutcome.SessionNotFound;
        }

        if (!IsValidMessage(message)
            || message.SourceMessageId is null
            || message.SourceRunId is null)
        {
            return SessionAppendOutcome.InvalidMessage;
        }

        try
        {
            await using var connection = await OpenStoreAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            var current = await LoadUnlockedAsync(sessionId, cancellationToken, connection, transaction)
                .ConfigureAwait(false);
            if (current is null)
            {
                return SessionAppendOutcome.SessionNotFound;
            }

            var existingFingerprint = await ReadReceiptAsync(sessionId, message.SourceMessageId,
                connection, transaction, cancellationToken).ConfigureAwait(false);
            if (existingFingerprint is not null)
            {
                return string.Equals(
                        existingFingerprint,
                        SourceFingerprint(message),
                        StringComparison.Ordinal)
                    ? SessionAppendOutcome.AlreadyPresent
                    : SessionAppendOutcome.InvalidMessage;
            }

            await AppendUnlockedAsync(current, [message], connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            transaction.Commit();
            return SessionAppendOutcome.Appended;
        }
        catch (SqliteException) { return SessionAppendOutcome.TransientFailure; }
        catch (IOException)
        {
            return SessionAppendOutcome.TransientFailure;
        }
        catch (UnauthorizedAccessException)
        {
            return SessionAppendOutcome.TransientFailure;
        }
        catch (InvalidDataException)
        {
            return SessionAppendOutcome.InvalidMessage;
        }
    }

    private async Task AppendUnlockedAsync(
        SessionDocument current,
        IReadOnlyList<SessionMessage> messages,
        SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var count = current.TotalMessageCount ?? 0;
        foreach (var message in messages.Where(message => message.SourceMessageId is not null))
        {
            var fingerprint = SourceFingerprint(message);
            var existing = await ReadReceiptAsync(current.Id, message.SourceMessageId!, connection,
                transaction, cancellationToken).ConfigureAwait(false);
            if (existing is not null && !string.Equals(existing, fingerprint, StringComparison.Ordinal))
            {
                throw new InvalidDataException("permanentInvalidMessage");
            }
        }

        var title = string.IsNullOrWhiteSpace(current.Title) || current.Title == DefaultTitle
            ? TitleFromMessages(messages)
            : current.Title;
        var now = _clock.GetUtcNow().ToUniversalTime();
        await InsertMessagesAsync(messages, current.Id, count, connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        foreach (var message in messages.Where(message => message.SourceMessageId is not null))
        {
            await InsertReceiptAsync(current.Id, message, connection, transaction, cancellationToken)
                .ConfigureAwait(false);
        }
        var updated = current with
        {
            Title = title,
            UpdatedAt = now,
            LastAgentActivityAt = messages.Any(message => message.Role == "ai")
                ? now
                : current.LastAgentActivityAt,
            TotalMessageCount = checked(count + messages.Count),
        };
        await UpdateMetadataUnlockedAsync(updated, connection, transaction, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ReadReceiptAsync(
        string sessionId, string sourceMessageId, SqliteConnection connection,
        SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT fingerprint FROM business_session_source_receipts WHERE session_id = $id AND source_message_id = $source;";
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$source", sourceMessageId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private static async Task InsertReceiptAsync(
        string sessionId, SessionMessage message, SqliteConnection connection,
        SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO business_session_source_receipts(session_id, source_message_id, fingerprint) VALUES ($id, $source, $fingerprint);";
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$source", message.SourceMessageId!);
        command.Parameters.AddWithValue("$fingerprint", SourceFingerprint(message));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool IsValidSourceId(string? value) =>
        value is { Length: >= 1 and <= 128 }
        && value.All(character =>
            character is (>= 'a' and <= 'z')
                or (>= 'A' and <= 'Z')
                or (>= '0' and <= '9')
                or '_'
                or '-'
                or '.'
                or ':');

    private static bool TryReadSourceLedger(
        JsonObject root,
        IReadOnlyList<SessionMessage> messages,
        out Dictionary<string, string> ledger)
    {
        ledger = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root["source_message_ledger"] is { } ledgerNode)
        {
            if (ledgerNode is not JsonObject ledgerObject)
            {
                return false;
            }

            foreach (var item in ledgerObject)
            {
                if (!IsValidSourceId(item.Key)
                    || item.Value is not JsonValue value
                    || !value.TryGetValue<string>(out var fingerprint)
                    || !IsSha256(fingerprint))
                {
                    return false;
                }

                ledger[item.Key] = fingerprint;
            }
        }

        foreach (var message in messages.Where(message => message.SourceMessageId is not null))
        {
            var fingerprint = SourceFingerprint(message);
            if (ledger.TryGetValue(message.SourceMessageId!, out var stored)
                && !string.Equals(stored, fingerprint, StringComparison.Ordinal))
            {
                return false;
            }

            ledger[message.SourceMessageId!] = fingerprint;
        }

        return true;
    }

    private static string SourceFingerprint(SessionMessage message)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            message.Role,
            message.Content,
            message.SourceRunId!,
        });
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
