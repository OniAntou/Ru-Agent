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

    public SessionStore(DataPaths paths, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _sessionDirectory = paths.SessionDirectory;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<SessionSummary>> ListAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(_sessionDirectory))
            {
                return [];
            }

            var sessions = new List<SessionSummary>();
            foreach (var path in Directory.EnumerateFiles(_sessionDirectory, "*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var document = await LoadUnlockedAsync(
                    Path.GetFileNameWithoutExtension(path),
                    cancellationToken).ConfigureAwait(false);
                if (document is null)
                {
                    continue;
                }

                sessions.Add(new SessionSummary(
                    document.Id,
                    document.Title,
                    document.CreatedAt,
                    document.UpdatedAt,
                    document.Messages.Count));
            }

            return sessions
                .OrderByDescending(session => session.UpdatedAt)
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        finally
        {
            _gate.Release();
        }
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

    public async Task<string?> CreateAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sessionId = Guid.NewGuid().ToString("N");
            var now = _clock.GetUtcNow().ToUniversalTime();
            var document = new SessionDocument(
                sessionId,
                DefaultTitle,
                now,
                now,
                []);
            await WriteUnlockedAsync(document, cancellationToken).ConfigureAwait(false);
            return sessionId;
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

    public async Task<bool> DeleteAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (!IsValidSessionId(sessionId))
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = SessionPath(sessionId);
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
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
            var current = await LoadUnlockedAsync(sessionId, cancellationToken)
                .ConfigureAwait(false);
            if (current is null)
            {
                return false;
            }

            var validNewMessages = messages.Where(IsValidMessage).ToArray();
            await AppendUnlockedAsync(current, validNewMessages, cancellationToken)
                .ConfigureAwait(false);
            return true;
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

    private async Task<SessionDocument?> LoadUnlockedAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (!IsValidSessionId(sessionId))
        {
            return null;
        }

        var path = SessionPath(sessionId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            if (new FileInfo(path).Length > MaximumSessionFileBytes)
            {
                return null;
            }

            var bytes = await ReadBoundedBytesAsync(path, MaximumSessionFileBytes, cancellationToken).ConfigureAwait(false);
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

            return new SessionDocument(
                storedId,
                storedTitle,
                ReadTimestamp(root, "created_at"),
                ReadTimestamp(root, "updated_at"),
                messages,
                ledger.Count == 0 ? null : ledger);
        }
        catch (JsonException)
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
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private async Task WriteUnlockedAsync(
        SessionDocument document,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_sessionDirectory);
        var target = SessionPath(document.Id);
        var temporary = Path.Combine(
            _sessionDirectory,
            $".{document.Id}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            if (bytes.Length > MaximumSessionFileBytes)
            {
                throw new InvalidDataException("session_file_too_large");
            }

            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private string SessionPath(string sessionId) =>
        Path.Combine(_sessionDirectory, $"{sessionId}.json");

    private static async Task<byte[]> ReadBoundedBytesAsync(
        string path,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var output = new MemoryStream((int)Math.Min(maximumBytes, 81920));
        var buffer = new byte[(int)Math.Min(maximumBytes, 81920)];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (output.Length > maximumBytes - read)
                throw new InvalidDataException("session_file_too_large");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return output.ToArray();
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

    private static bool IsValidSessionId(string? sessionId) =>
        sessionId is not null && SessionIdPattern().IsMatch(sessionId);

    [GeneratedRegex("^[a-zA-Z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SessionIdPattern();
}
