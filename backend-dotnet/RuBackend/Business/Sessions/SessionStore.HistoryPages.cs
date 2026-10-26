using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RuBackend.Business.Sessions;

public sealed partial class SessionStore
{
    private const int MaximumSummaryPageSize = 200;
    private const int MaximumSearchResults = 200;
    private const int MaximumCursorBytes = 2048;
    private const int MaximumSearchQueryCharacters = 512;

    private sealed record MessageCursor(string? SessionId, int? BeforeIndex, int? TotalMessages, long? Epoch);
    private sealed record SummaryCursor(string? QueryKey, string? UpdatedAt, string? SessionId);

    public async Task<SessionMessagePage?> GetMessagePageAsync(
        string sessionId, string? cursor, int limit, CancellationToken cancellationToken)
    {
        if (!IsValidSessionId(sessionId)) return null;
        if (limit is < 1 or > MaximumPageMessages)
            throw new SessionHistoryException("session_history_limit_invalid");

        await using var connection = await OpenStoreAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var metadata = await LoadUnlockedAsync(sessionId, cancellationToken, connection, transaction)
            .ConfigureAwait(false);
        if (metadata is null) return null;

        var currentTotal = metadata.TotalMessageCount ?? 0;
        var total = currentTotal;
        var before = currentTotal;
        if (cursor is not null)
        {
            var decoded = DecodeCursor<MessageCursor>(cursor);
            if (decoded.SessionId is null || decoded.BeforeIndex is null
                || decoded.TotalMessages is null || decoded.Epoch is null)
                throw new SessionHistoryException("session_history_cursor_invalid");
            if (!string.Equals(decoded.SessionId, sessionId, StringComparison.Ordinal))
                throw new SessionHistoryException("session_history_cursor_session_mismatch");
            if (decoded.Epoch != metadata.HistoryEpoch)
                throw new SessionHistoryException("session_history_cursor_stale");
            if (decoded.BeforeIndex < 0 || decoded.TotalMessages < decoded.BeforeIndex
                || decoded.TotalMessages > currentTotal)
                throw new SessionHistoryException("session_history_cursor_invalid");
            before = decoded.BeforeIndex.Value;
            total = decoded.TotalMessages.Value;
        }

        var page = await ReadMessagesBeforeAsync(connection, sessionId, before, limit,
            MaximumPageBytes, cancellationToken, transaction, allowSingleOverLimit: true).ConfigureAwait(false);
        var nextCursor = page.StartIndex > 0
            ? EncodeCursor(new MessageCursor(sessionId, page.StartIndex, total, metadata.HistoryEpoch))
            : null;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(page.Messages, page.StartIndex, total, nextCursor, metadata.HistoryEpoch);
    }

    public async Task<SessionSummaryPage> ListPageAsync(
        string? cursor, int limit, string? query, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > MaximumSummaryPageSize)
            throw new SessionHistoryException("session_history_limit_invalid");
        query = NormalizeQuery(query);
        var after = cursor is null ? null : DecodeCursor<SummaryCursor>(cursor);
        if (after is not null)
        {
            if (after.QueryKey is null || after.UpdatedAt is null || after.SessionId is null
                || !IsValidSessionId(after.SessionId)
                || !DateTimeOffset.TryParse(after.UpdatedAt, out _))
                throw new SessionHistoryException("session_history_cursor_invalid");
            if (!string.Equals(after.QueryKey, QueryKey(query), StringComparison.Ordinal))
                throw new SessionHistoryException("session_history_cursor_query_mismatch");
        }

        await using var connection = await OpenStoreAsync(cancellationToken).ConfigureAwait(false);
        connection.CreateFunction<string?, string?>("invariant_fold", static value => value?.ToLowerInvariant(), isDeterministic: true);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.summary, s.updated_at_utc, s.session_id
            FROM business_sessions AS s
            WHERE ($query IS NULL
                OR invariant_fold(s.title) LIKE $pattern ESCAPE '\'
                OR s.session_id IN (
                    SELECT session_id FROM business_session_message_search
                    WHERE $fts_query IS NOT NULL
                        AND business_session_message_search MATCH $fts_query))
              AND ($after_updated IS NULL OR s.updated_at_utc < $after_updated
                OR (s.updated_at_utc = $after_updated AND s.session_id > $after_id))
            ORDER BY s.updated_at_utc DESC, s.session_id ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$query", DbValue(query));
        command.Parameters.AddWithValue("$pattern", DbValue(query is null
            ? null
            : $"%{EscapeLike(query.ToLowerInvariant())}%"));
        command.Parameters.AddWithValue("$fts_query", DbValue(ToFtsLiteral(query)));
        command.Parameters.AddWithValue("$after_updated", DbValue(after?.UpdatedAt));
        command.Parameters.AddWithValue("$after_id", DbValue(after?.SessionId));
        command.Parameters.AddWithValue("$limit", limit + 1);
        var summaries = new List<(SessionSummary Value, string Updated, string Id)>(limit + 1);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var value = JsonSerializer.Deserialize<SessionSummary>(reader.GetString(0))
                    ?? throw new InvalidDataException("session_summary_invalid");
                summaries.Add((value, reader.GetString(1), reader.GetString(2)));
            }
        }

        var hasMore = summaries.Count > limit;
        var page = summaries.Take(limit).ToArray();
        var nextCursor = hasMore && page.Length > 0
            ? EncodeCursor(new SummaryCursor(QueryKey(query), page[^1].Updated, page[^1].Id))
            : null;
        return new(page.Select(item => item.Value).ToArray(), nextCursor);
    }

    public async Task<IReadOnlyList<SessionMessageSearchHit>> SearchMessagesAsync(
        string query, int limit, CancellationToken cancellationToken)
    {
        query = NormalizeQuery(query) ?? throw new SessionHistoryException("session_history_query_invalid");
        if (limit is < 1 or > MaximumSearchResults)
            throw new SessionHistoryException("session_history_limit_invalid");
        var literal = ToFtsLiteral(query);
        if (literal is null) return [];
        await using var connection = await OpenStoreAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id, CAST(message_index AS INTEGER), role,
                snippet(business_session_message_search, 3, '<b>', '</b>', '…', 18)
            FROM business_session_message_search
            WHERE business_session_message_search MATCH $query
            ORDER BY rank LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$query", literal);
        command.Parameters.AddWithValue("$limit", limit);
        var results = new List<SessionMessageSearchHit>(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(new(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3)));
        return results;
    }

    private static string? NormalizeQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var normalized = query.Trim();
        if (normalized.Length > MaximumSearchQueryCharacters || normalized.Any(char.IsControl))
            throw new SessionHistoryException("session_history_query_invalid");
        return normalized;
    }

    private static string? ToFtsLiteral(string? query)
    {
        if (query is null || !query.Any(char.IsLetterOrDigit)) return null;
        var escapedQuery = query.Replace("\"", "\"\"", StringComparison.Ordinal);
        return "\"" + escapedQuery + "\"";
    }

    private static string QueryKey(string? query) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(query ?? string.Empty))).ToLowerInvariant();

    private static async Task<(IReadOnlyList<SessionMessage> Messages, int StartIndex)> ReadMessagesBeforeAsync(
        SqliteConnection connection, string sessionId, int beforeIndex, int limit, int byteLimit,
        CancellationToken cancellationToken, SqliteTransaction transaction,
        bool allowSingleOverLimit = false)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT message_index, role, CAST(content AS BLOB), source_message_id, source_run_id
            FROM business_session_messages
            WHERE session_id = $id AND message_index < $before
            ORDER BY message_index DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$before", beforeIndex);
        command.Parameters.AddWithValue("$limit", limit);
        var reversed = new List<(int Index, SessionMessage Message)>(limit);
        var bytes = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var message = new SessionMessage(reader.GetString(1), System.Text.Encoding.UTF8.GetString((byte[])reader[2]),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4));
            var messageBytes = JsonSerializer.SerializeToUtf8Bytes(message).Length;
            if (reversed.Count == 0 && messageBytes > byteLimit && !allowSingleOverLimit)
                throw new SessionHistoryException("session_history_message_exceeds_page_limit");
            if (bytes + messageBytes > byteLimit
                && !(reversed.Count == 0 && allowSingleOverLimit)) break;
            bytes += messageBytes;
            reversed.Add((reader.GetInt32(0), message));
        }
        if (reversed.Count == 0)
        {
            if (beforeIndex == 0) return ([], 0);
            throw new SessionHistoryException("session_history_index_gap");
        }
        reversed.Reverse();
        return (reversed.Select(entry => entry.Message).ToArray(), reversed[0].Index);
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static string EncodeCursor<T>(T cursor)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(cursor);
        return "h1." + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static T DecodeCursor<T>(string cursor)
    {
        if (cursor.Length > MaximumCursorBytes || !cursor.StartsWith("h1.", StringComparison.Ordinal))
            throw new SessionHistoryException("session_history_cursor_invalid");
        try
        {
            var encoded = cursor[3..].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
            var value = JsonSerializer.Deserialize<T>(Convert.FromBase64String(encoded));
            return value ?? throw new SessionHistoryException("session_history_cursor_invalid");
        }
        catch (SessionHistoryException) { throw; }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            throw new SessionHistoryException("session_history_cursor_invalid");
        }
    }
}
