using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RuBackend.Business.Sessions;

public sealed partial class SessionStore : ISessionStore
{
    private const string DefaultTitle = "Cuộc trò chuyện mới";
    private const int MaximumMessages = 200;
    private const int MaximumTitleCharacters = 50;
    private const int MaximumMessageBytes = 1024 * 1024;
    private const long MaximumSessionFileBytes = 16 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _sessionDirectory;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

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

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadUnlockedAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<string?> CreateAsync(CancellationToken cancellationToken) =>
        CreateForClientAsync(null, cancellationToken);

    public async Task<string?> CreateForClientAsync(string? clientSource, CancellationToken cancellationToken)
    {
        if (clientSource is not (null or "gui" or "cli"))
            throw new ArgumentException("Unknown session client source.", nameof(clientSource));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
        finally
        {
            _gate.Release();
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

        if (messages.Any(message =>
                message?.SourceMessageId is not null
                || message?.SourceRunId is not null))
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
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

            var validNewMessages = messages.Where(IsValidMessage).ToArray();
            await AppendUnlockedAsync(current, validNewMessages, connection, transaction, cancellationToken)
                .ConfigureAwait(false);
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
        finally
        {
            _gate.Release();
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

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await LoadUnlockedAsync(sessionId, cancellationToken)
                .ConfigureAwait(false);
            if (document is null)
            {
                return [];
            }

            return SessionContextTurns.Select(document.Messages, maxTurns);
        }
        finally
        {
            _gate.Release();
        }
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

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenStoreAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            var original = await LoadUnlockedAsync(sessionId, cancellationToken, connection, transaction).ConfigureAwait(false);
            if (original is null)
            {
                return null;
            }

            var count = Math.Clamp(toMessageIndex + 1, 0, original.Messages.Count);
            var rewoundMessages = original.Messages.Take(count).ToList();
            var now = _clock.GetUtcNow().ToUniversalTime();

            var rewoundDoc = original with
            {
                Messages = rewoundMessages,
                UpdatedAt = now,
            };

            await WriteUnlockedAsync(rewoundDoc, connection, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return rewoundDoc;
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
        finally
        {
            _gate.Release();
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
        command.CommandText = "SELECT document FROM business_sessions WHERE session_id = $id;";
        command.Parameters.AddWithValue("$id", sessionId);
        var bytes = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as byte[];
        return bytes is null ? null : ParseDocument(bytes, sessionId);
    }

    private static SessionDocument? ParseDocument(byte[] bytes, string sessionId)
    {
        try
        {
            var root = JsonNode.Parse(bytes) as JsonObject;
            if (root is null || root["messages"] is not JsonArray messagesNode)
            {
                return null;
            }

            var storedId = ReadString(root, "id", sessionId);
            if (!IsValidSessionId(storedId) || !string.Equals(storedId, sessionId, StringComparison.Ordinal))
            {
                return null;
            }

            var messages = new List<SessionMessage>();
            foreach (var node in messagesNode)
            {
                if (node is not JsonObject messageNode)
                {
                    continue;
                }

                var role = ReadString(messageNode, "role", string.Empty);
                var content = ReadNullableString(messageNode, "content");
                if ((role == "user" || role == "ai") && content is not null)
                {
                    var message = new SessionMessage(
                        role,
                        content,
                        ReadNullableString(messageNode, "source_message_id"),
                        ReadNullableString(messageNode, "source_run_id"));
                    if (IsValidMessage(message))
                    {
                        messages.Add(message);
                    }
                }
            }

            if (!TryReadSourceLedger(root, messages, out var ledger))
            {
                return null;
            }

            var storedTitle = ReadString(root, "title", DefaultTitle);
            if (storedTitle.Length is 0 or > MaximumTitleCharacters
                || storedTitle.Any(char.IsControl))
            {
                storedTitle = DefaultTitle;
            }

            var parentSessionId = ReadNullableString(root, "parent_session_id");
            var forkedFromMessageId = ReadNullableString(root, "forked_from_message_id");
            var clientSource = ReadNullableString(root, "client_source");

            return new SessionDocument(
                storedId,
                storedTitle,
                ReadTimestamp(root, "created_at"),
                ReadTimestamp(root, "updated_at"),
                messages,
                ledger.Count == 0 ? null : ledger,
                parentSessionId,
                forkedFromMessageId,
                TryReadTimestamp(root, "last_agent_activity_at"),
                clientSource is "gui" or "cli" ? clientSource : null);
        }
        catch (JsonException) { return null; }
        catch (InvalidDataException) { return null; }
    }

    private static async Task WriteUnlockedAsync(
        SessionDocument document, SqliteConnection connection,
        SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.Length > MaximumSessionFileBytes) throw new InvalidDataException("session_file_too_large");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO business_sessions(session_id, document, summary, updated_at_utc)
            VALUES ($id, $document, $summary, $updated)
            ON CONFLICT(session_id) DO UPDATE SET document = excluded.document,
                summary = excluded.summary, updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$id", document.Id);
        command.Parameters.AddWithValue("$document", bytes);
        command.Parameters.AddWithValue("$summary", JsonSerializer.Serialize(ToSummary(document)));
        command.Parameters.AddWithValue("$updated", document.UpdatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool IsValidMessage(SessionMessage? message) =>
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

    private static string ReadString(JsonObject node, string key, string fallback) =>
        ReadNullableString(node, key) ?? fallback;

    private static string? ReadNullableString(JsonObject node, string key) =>
        node[key] is JsonValue value && value.TryGetValue<string>(out var result)
            ? result
            : null;

    private static DateTimeOffset ReadTimestamp(JsonObject node, string key)
    {
        var raw = ReadNullableString(node, key);
        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed.ToUniversalTime()
            : DateTimeOffset.UnixEpoch;
    }

    private static DateTimeOffset? TryReadTimestamp(JsonObject node, string key)
    {
        var raw = ReadNullableString(node, key);
        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    private static bool IsValidSessionId(string? sessionId) =>
        sessionId is not null && SessionIdPattern().IsMatch(sessionId);

    [GeneratedRegex("^[a-zA-Z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SessionIdPattern();
}
