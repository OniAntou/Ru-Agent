using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace RuBackend.Business.Sessions;

public sealed record SessionForkResult(SessionDocument? Document, string? ErrorCode);

public sealed partial class SessionStore
{
    public static string HistoryRevision(SessionDocument document) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            document.HistoryEpoch,
            TotalMessageCount = document.TotalMessageCount ?? document.Messages.Count,
            document.MessageOffset,
            document.Messages,
        }))).ToLowerInvariant();

    public async Task<SessionForkResult> ForkAsync(string sourceId, int keepMessages, string revision,
        CancellationToken cancellationToken)
    {
        if (!IsValidSessionId(sourceId) || !IsSha256(revision)) return new(null, "invalid_fork_request");
        await using var connection = await OpenStoreAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var source = await LoadUnlockedAsync(sourceId, cancellationToken, connection, transaction).ConfigureAwait(false);
        if (source is null) return new(null, "session_not_found");
        var count = source.TotalMessageCount ?? 0;
        var tail = await ReadMessagesBeforeAsync(connection, sourceId, count, MaximumGetMessages,
            MaximumPageBytes, cancellationToken, transaction, allowSingleOverLimit: true).ConfigureAwait(false);
        source = source with { Messages = tail.Messages, MessageOffset = tail.StartIndex };
        if (HistoryRevision(source) != revision) return new(null, "history_stale");
        // Only whole-turn boundaries are accepted, including before the first turn.
        if (keepMessages < source.MessageOffset || keepMessages > count
            || (keepMessages < count && !await IsForkBoundaryAsync(connection, transaction,
                sourceId, keepMessages, cancellationToken).ConfigureAwait(false)))
            return new(null, "invalid_fork_boundary");
        var now = _clock.GetUtcNow().ToUniversalTime();
        var child = new SessionDocument(Guid.NewGuid().ToString("N"), source.Title, now, now, [],
            ParentSessionId: source.Id,
            ClientSource: "cli", TotalMessageCount: keepMessages);
        await WriteUnlockedAsync(child, connection, transaction, cancellationToken).ConfigureAwait(false);
        await CopyForkPrefixAsync(connection, transaction, sourceId, child.Id, keepMessages, cancellationToken)
            .ConfigureAwait(false);
        var childTail = await ReadMessagesBeforeAsync(connection, child.Id, keepMessages, MaximumGetMessages,
            MaximumPageBytes, cancellationToken, transaction, allowSingleOverLimit: true).ConfigureAwait(false);
        child = child with
        {
            Messages = childTail.Messages, MessageOffset = childTail.StartIndex,
            ForkedFromMessageId = childTail.Messages.LastOrDefault()?.SourceMessageId,
            SourceMessageLedger = childTail.Messages.Where(message => message.SourceMessageId is not null)
                .GroupBy(message => message.SourceMessageId!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => SourceFingerprint(group.First()), StringComparer.Ordinal),
        };
        await WriteUnlockedAsync(child, connection, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(child, null);
    }

    private static async Task<bool> IsForkBoundaryAsync(SqliteConnection connection, SqliteTransaction transaction,
        string sourceId, int index, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT role FROM business_session_messages WHERE session_id = $id AND message_index = $index;";
        command.Parameters.AddWithValue("$id", sourceId);
        command.Parameters.AddWithValue("$index", index);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string == "user";
    }

    private static async Task CopyForkPrefixAsync(SqliteConnection connection, SqliteTransaction transaction,
        string sourceId, string childId, int count, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Copy retained rows and only their delivery receipts inside the same transaction.
        // Older pages need not be loaded into memory; no execution/workflow authority is copied.
        command.CommandText = """
            INSERT INTO business_session_messages(session_id, message_index, role, content, source_message_id, source_run_id)
            SELECT $child, message_index, role, content, source_message_id, source_run_id
            FROM business_session_messages WHERE session_id = $source AND message_index < $count;
            INSERT INTO business_session_source_receipts(session_id, source_message_id, fingerprint)
            SELECT $child, r.source_message_id, r.fingerprint FROM business_session_source_receipts r
            WHERE r.session_id = $source AND EXISTS (
                SELECT 1 FROM business_session_messages m WHERE m.session_id = $source
                    AND m.message_index < $count AND m.source_message_id = r.source_message_id);
            """;
        command.Parameters.AddWithValue("$source", sourceId);
        command.Parameters.AddWithValue("$child", childId);
        command.Parameters.AddWithValue("$count", count);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
