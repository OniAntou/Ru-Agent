using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RuBackend.Business.Sessions;

public sealed partial class SessionStore : ISessionStore
{
    private const string DefaultTitle = "Cuộc trò chuyện mới";
    private const int MaximumTitleCharacters = 50;
    private const int MaximumMessageBytes = 1024 * 1024;
    private const long MaximumSessionFileBytes = 16 * 1024 * 1024;
    private const int MaximumGetMessages = 200;
    private const int MaximumPageMessages = 200;
    // Normal pages stay within 4 MiB of JSON. A single valid 1 MiB UTF-8
    // message can expand to about 6 MiB plus fixed JSON fields.
    private const int MaximumPageBytes = 4 * 1024 * 1024;
    private const int MaximumContextMessages = 256;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _sessionDirectory;
    private readonly TimeProvider _clock;
    private readonly BusinessSqliteDatabase _database;
    private volatile bool _isMigrated;

    public SessionStore(DataPaths paths, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _sessionDirectory = paths.SessionDirectory;
        _database = new BusinessSqliteDatabase(paths);
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<SessionDocument?> GetAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (!IsValidSessionId(sessionId))
        {
            return null;
        }

        await using var connection = await OpenStoreAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var metadata = await LoadUnlockedAsync(sessionId, cancellationToken, connection, transaction).ConfigureAwait(false);
        if (metadata is null) return null;
        var count = metadata.TotalMessageCount ?? 0;
        var tail = await ReadMessagesBeforeAsync(connection, sessionId, count, MaximumGetMessages,
            MaximumPageBytes, cancellationToken, transaction, allowSingleOverLimit: true).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return metadata with { Messages = tail.Messages, MessageOffset = tail.StartIndex };
    }

    public Task<string?> CreateAsync(CancellationToken cancellationToken) =>
        CreateForClientAsync(null, cancellationToken);

    public async Task<string?> CreateForClientAsync(string? clientSource, CancellationToken cancellationToken)
    {
        if (clientSource is not (null or "gui" or "cli"))
            throw new ArgumentException("Unknown session client source.", nameof(clientSource));
        try
        {
            await using var connection = await OpenStoreAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            var sessionId = Guid.NewGuid().ToString("N");
            var now = _clock.GetUtcNow().ToUniversalTime();
            var document = new SessionDocument(
                sessionId,
                DefaultTitle,
                now,
                now,
                [], ClientSource: clientSource);
            await WriteUnlockedAsync(document, connection, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return sessionId;
        }
        catch (SqliteException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task<bool> AppendAsync(
        string sessionId,
        IReadOnlyList<SessionMessage> messages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (!IsValidSessionId(sessionId))
        {
            return false;
        }

        if (messages.Any(message => !IsValidMessage(message)
                || message?.SourceMessageId is not null
                || message?.SourceRunId is not null))
        {
            return false;
        }

        try
        {
            await using var connection = await OpenStoreAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            var current = await LoadUnlockedAsync(sessionId, cancellationToken, connection, transaction)
                .ConfigureAwait(false);
            if (current is null)
            {
                return false;
            }

            await AppendUnlockedAsync(current, messages, connection, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<ConversationTurn>> GetContextTurnsAsync(
        string sessionId,
        int maxTurns,
        CancellationToken cancellationToken)
    {
        if (!IsValidSessionId(sessionId) || maxTurns <= 0)
        {
            return [];
        }

        await using var connection = await OpenStoreAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var metadata = await LoadUnlockedAsync(sessionId, cancellationToken, connection, transaction).ConfigureAwait(false);
        if (metadata is null) return [];
        var count = metadata.TotalMessageCount ?? 0;
        var take = (int)Math.Clamp((long)maxTurns * 2 + 4, 2L, MaximumContextMessages);
        var tail = await ReadMessagesBeforeAsync(connection, sessionId, count, take,
            MaximumPageBytes, cancellationToken, transaction, allowSingleOverLimit: true).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return SessionContextTurns.Select(tail.Messages, maxTurns);
    }

    public async Task<SessionDocument?> RewindAsync(
        string sessionId,
        int toMessageIndex,
        CancellationToken cancellationToken)
    {
        if (!IsValidSessionId(sessionId))
        {
            return null;
        }

        try
        {
            await using var connection = await OpenStoreAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            var original = await LoadUnlockedAsync(sessionId, cancellationToken, connection, transaction).ConfigureAwait(false);
            if (original is null)
            {
                return null;
            }

            var count = Math.Clamp((long)toMessageIndex + 1, 0L, original.TotalMessageCount ?? 0);
            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM business_session_messages WHERE session_id = $id AND message_index >= $index;";
                delete.Parameters.AddWithValue("$id", sessionId);
                delete.Parameters.AddWithValue("$index", count);
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            var now = _clock.GetUtcNow().ToUniversalTime();

            var rewoundDoc = original with
            {
                UpdatedAt = now,
                TotalMessageCount = checked((int)count),
                HistoryEpoch = checked(original.HistoryEpoch + 1),
            };

            await UpdateMetadataUnlockedAsync(rewoundDoc, connection, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return await GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<SessionDocument?> LoadUnlockedAsync(
        string sessionId, CancellationToken cancellationToken,
        SqliteConnection? connection = null, SqliteTransaction? transaction = null)
    {
        if (!IsValidSessionId(sessionId)) return null;
        if (connection is null)
        {
            await using var owned = await OpenStoreAsync(cancellationToken).ConfigureAwait(false);
            return await LoadUnlockedAsync(sessionId, cancellationToken, owned).ConfigureAwait(false);
        }
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT title, created_at_utc, updated_at_utc, parent_session_id,
                forked_from_message_id, last_agent_activity_at_utc, client_source,
                total_message_count, history_epoch
            FROM business_sessions WHERE session_id = $id;
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var count = reader.GetInt32(7);
        return new SessionDocument(
            sessionId,
            reader.GetString(0),
            ParseDatabaseTimestamp(reader.GetString(1)),
            ParseDatabaseTimestamp(reader.GetString(2)),
            [],
            ParentSessionId: reader.IsDBNull(3) ? null : reader.GetString(3),
            ForkedFromMessageId: reader.IsDBNull(4) ? null : reader.GetString(4),
            LastAgentActivityAt: reader.IsDBNull(5) ? null : ParseDatabaseTimestamp(reader.GetString(5)),
            ClientSource: reader.IsDBNull(6) ? null : reader.GetString(6),
            MessageOffset: Math.Max(0, count - MaximumGetMessages),
            TotalMessageCount: count,
            HistoryEpoch: reader.GetInt64(8));
    }

    private static async Task WriteUnlockedAsync(
        SessionDocument document, SqliteConnection connection,
        SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var now = document.UpdatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var created = document.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var metadata = document with { Messages = [], SourceMessageLedger = null,
            TotalMessageCount = document.TotalMessageCount ?? document.Messages.Count };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO business_sessions(session_id, document, summary, updated_at_utc,
                title, created_at_utc, parent_session_id, forked_from_message_id,
                last_agent_activity_at_utc, client_source, total_message_count, history_epoch)
            VALUES ($id, $document, $summary, $updated, $title, $created, $parent,
                $forked, $activity, $client, $count, $epoch)
            ON CONFLICT(session_id) DO UPDATE SET summary = excluded.summary,
                updated_at_utc = excluded.updated_at_utc, title = excluded.title,
                created_at_utc = excluded.created_at_utc, parent_session_id = excluded.parent_session_id,
                forked_from_message_id = excluded.forked_from_message_id,
                last_agent_activity_at_utc = excluded.last_agent_activity_at_utc,
                client_source = excluded.client_source, total_message_count = excluded.total_message_count,
                history_epoch = excluded.history_epoch;
            """;
        command.Parameters.AddWithValue("$id", document.Id);
        command.Parameters.AddWithValue("$document", bytes);
        command.Parameters.AddWithValue("$summary", JsonSerializer.Serialize(ToSummary(metadata)));
        command.Parameters.AddWithValue("$updated", now);
        command.Parameters.AddWithValue("$title", metadata.Title);
        command.Parameters.AddWithValue("$created", created);
        command.Parameters.AddWithValue("$parent", DbValue(metadata.ParentSessionId));
        command.Parameters.AddWithValue("$forked", DbValue(metadata.ForkedFromMessageId));
        command.Parameters.AddWithValue("$activity", DbValue(metadata.LastAgentActivityAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
        command.Parameters.AddWithValue("$client", DbValue(metadata.ClientSource));
        command.Parameters.AddWithValue("$count", metadata.TotalMessageCount!.Value);
        command.Parameters.AddWithValue("$epoch", metadata.HistoryEpoch);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertMessagesAsync(
        IReadOnlyList<SessionMessage> messages, string sessionId, int startIndex,
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO business_session_messages(session_id, message_index, role, content, source_message_id, source_run_id)
            VALUES ($id, $index, $role, CAST($content AS TEXT), $source, $run);
            """;
        var id = command.Parameters.Add("$id", SqliteType.Text);
        var index = command.Parameters.Add("$index", SqliteType.Integer);
        var role = command.Parameters.Add("$role", SqliteType.Text);
        var content = command.Parameters.Add("$content", SqliteType.Blob);
        var source = command.Parameters.Add("$source", SqliteType.Text);
        var run = command.Parameters.Add("$run", SqliteType.Text);
        for (var offset = 0; offset < messages.Count; offset++)
        {
            var message = messages[offset];
            id.Value = sessionId;
            index.Value = checked(startIndex + offset);
            role.Value = message.Role;
            // Bind explicit UTF-8 bytes so embedded NULs are not truncated by native text marshalling.
            content.Value = Encoding.UTF8.GetBytes(message.Content);
            source.Value = DbValue(message.SourceMessageId);
            run.Value = DbValue(message.SourceRunId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task UpdateMetadataUnlockedAsync(
        SessionDocument document, SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var metadata = document with { Messages = [], SourceMessageLedger = null };
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE business_sessions SET title = $title, updated_at_utc = $updated,
                last_agent_activity_at_utc = $activity, total_message_count = $count,
                history_epoch = $epoch, summary = $summary
            WHERE session_id = $id;
            """;
        command.Parameters.AddWithValue("$id", metadata.Id);
        command.Parameters.AddWithValue("$title", metadata.Title);
        command.Parameters.AddWithValue("$updated", metadata.UpdatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$activity", DbValue(metadata.LastAgentActivityAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
        command.Parameters.AddWithValue("$count", metadata.TotalMessageCount ?? 0);
        command.Parameters.AddWithValue("$epoch", metadata.HistoryEpoch);
        command.Parameters.AddWithValue("$summary", JsonSerializer.Serialize(ToSummary(metadata)));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static object DbValue(object? value) => value ?? DBNull.Value;

    private static DateTimeOffset ParseDatabaseTimestamp(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToUniversalTime() : DateTimeOffset.UnixEpoch;

    internal static bool IsValidMessage(SessionMessage? message) =>
        message is not null &&
        (message.Role == "user" || message.Role == "ai") &&
        message.Content is not null &&
        Encoding.UTF8.GetByteCount(message.Content) <= MaximumMessageBytes &&
        ((message.SourceMessageId is null && message.SourceRunId is null)
            || (IsValidSourceId(message.SourceMessageId)
                && IsValidSourceId(message.SourceRunId)));

    private static string TitleFromMessages(IEnumerable<SessionMessage> messages)
    {
        foreach (var message in messages)
        {
            if (message.Role != "user")
            {
                continue;
            }

            var normalized = string.Join(
                " ",
                message.Content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (normalized.Length > 0)
            {
                return normalized[..Math.Min(normalized.Length, MaximumTitleCharacters)];
            }
        }

        return DefaultTitle;
    }

    private static bool IsValidSessionId(string? sessionId) =>
        sessionId is not null && SessionIdPattern().IsMatch(sessionId);

    [GeneratedRegex("^[a-zA-Z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SessionIdPattern();
}
