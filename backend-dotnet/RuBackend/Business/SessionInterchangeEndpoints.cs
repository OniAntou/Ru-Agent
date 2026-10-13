using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Workspace;
using RuBackend.Harness.Workspace;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RuAgent.SessionInterchange;
using RuBackend.Business.Sessions;
using RuBackend.Security;

namespace RuBackend.Business;

/// <summary>
/// Moves a session between Ru and another coding agent.
/// </summary>
/// <remarks>
/// Both directions are thin on purpose: the format knowledge lives in
/// <c>RuAgent.SessionInterchange</c>, and this file only carries it across the HTTP
/// boundary.
///
/// What travels is the conversation. Ru keeps tool traffic in the durable harness event
/// log rather than in the session's message list, so on import another agent's tool calls
/// and reasoning have nowhere to land — they are counted back to the caller instead of
/// being written in as assistant messages no model produced. Export has the same hole
/// from the other side: Ru's own tool rows are not in the message list either, so a
/// session leaves with its turns and without its tool traffic.
///
/// The request body bounds are layered (Kestrel above the route's declared bound, the
/// route above the endpoint's reader), and <see cref="SessionInterchangeLimits.MaxImportBodyBytes"/>
/// is sized for the worst case of the JSON encoder — non-ASCII and HTML-sensitive
/// characters travel as six-byte <c>\uXXXX</c> escapes. A session longer than the 16 MiB
/// content cap is refused as <c>import_too_large</c>: before parsing when the body is
/// over the transport bound, and after parsing when the decoded content is over the cap.
/// </remarks>
public static class SessionInterchangeEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/sessions/{sessionId}/export", ExportSessionAsync);
        app.MapPost("/sessions/import", ImportSessionAsync);
    }

    private static async Task<IResult> ExportSessionAsync(
        string sessionId,
        string? harness,
        ISessionStore sessions,
        HarnessSqliteOptions sqliteOptions,
        SqliteWorkspaceStore workspaceStore,
        WorkspaceTrustService trustService,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        try
        {
            if (SessionFormatCatalog.Find(harness ?? string.Empty) is not { } format)
            {
                return Results.Ok(new
                {
                    status = "error",
                    message = $"Unknown session format '{harness}'. Known formats: {KnownFormats()}.",
                });
            }

            if (!format.CanWrite)
            {
                // Handing a session to a partner whose store we cannot produce would write
                // a file that merely looks like one of theirs.
                return Results.Ok(new
                {
                    status = "error",
                    message = $"{format.DisplayName} sessions cannot be written: {format.WriteRefusal}",
                });
            }

            var document = await sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (document is null)
            {
                return Results.Ok(new { status = "error", message = "Không tìm thấy session để export." });
            }

            var transcript = ToTranscript(
                document,
                await WorkingDirectoryForAsync(sessionId, sqliteOptions, workspaceStore, trustService, cancellationToken).ConfigureAwait(false));
            var files = format.Write(transcript);

            var envelope = new
            {
                status = "ok",
                harness = format.Id,
                display_name = format.DisplayName,
                session = new
                {
                    id = document.Id,
                    title = document.Title,
                    messages = document.Messages.Count,
                    // Claude Code files a transcript under a directory named after the
                    // project and Codex files by date, so the caller cannot place the files
                    // without these two.
                    working_directory = transcript.WorkingDirectory,
                    created_at = transcript.CreatedAt,
                },
                files = files.Select(file => new { name = file.FileName, content = file.Content }),
            };
            if (!await SessionExportEnvelopeLimit.FitsAsync(envelope, cancellationToken).ConfigureAwait(false))
            {
                return Results.Json(new
                {
                    status = "error",
                    error_code = "export_too_large",
                    message = $"export_too_large: export envelopes are limited to {SessionInterchangeLimits.MaxExportBodyBytes / (1024 * 1024)} MiB.",
                }, statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            // Use the same serializer options as the measurement, independent of Host DI overrides.
            return Results.Json(envelope, JsonSerializerOptions.Web);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            LogFailure(loggers, "session export", error);
            return Results.Ok(new { status = "error", message = "Lỗi khi export session." });
        }
    }

    private static async Task<IResult> ImportSessionAsync(
        HttpContext context,
        ISessionStore sessions,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        string? createdSessionId = null;
        var completed = false;
        try
        {
            // Bounded before parsing: a body above the transport bound never becomes a
            // string, let alone a transcript. The endpoint's own bound sits below the
            // route's declared bound so this answer, not the middleware's, is the 413.
            var (read, body) = await BoundedRequestBodyReader
                .ReadTextAsync(context.Request, SessionInterchangeLimits.MaxImportBodyBytes, cancellationToken)
                .ConfigureAwait(false);
            if (!read)
            {
                return ImportTooLarge();
            }

            ImportSessionRequest? request;
            try
            {
                request = string.IsNullOrWhiteSpace(body)
                    ? null
                    : JsonSerializer.Deserialize<ImportSessionRequest>(body);
            }
            catch (JsonException)
            {
                return Results.Json(
                    new { status = "error", message = "invalid_request" },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (SessionFormatCatalog.Find(request?.Harness ?? string.Empty) is not { } format)
            {
                return Results.Ok(new
                {
                    status = "error",
                    message = $"Unknown session format '{request?.Harness}'. Known formats: {KnownFormats()}.",
                });
            }

            var content = request?.Content;
            if (string.IsNullOrWhiteSpace(content))
            {
                return Results.Ok(new { status = "error", message = "Cần nội dung file session để import." });
            }

            // JSON escaping can inflate the body past the content it carries. The cap the
            // CLI and GUI measured before reading is enforced again on the decoded
            // content, so all three surfaces agree on what "too large" means.
            if (Encoding.UTF8.GetByteCount(content) > SessionInterchangeLimits.MaxImportContentBytes)
            {
                return ImportTooLarge();
            }

            SessionTranscript transcript;
            try
            {
                transcript = format.Read(content);
            }
            catch (InvalidDataException error)
            {
                return Results.Ok(new { status = "error", message = error.Message });
            }

            var messages = MessagesFrom(transcript);
            if (messages.Count == 0)
            {
                return Results.Ok(new { status = "error", message = "File session này không có lượt nào để import." });
            }

            string? clientSource = context.Request.Headers["X-Ru-Client-Source"].ToString();
            if (clientSource.Length == 0) clientSource = null;
            if (clientSource is not (null or "gui" or "cli"))
                return Results.BadRequest(new { status = "error", code = "invalid_client_source" });
            var sessionId = await sessions.CreateForClientAsync(clientSource, cancellationToken).ConfigureAwait(false);
            createdSessionId = sessionId;
            if (sessionId is null)
            {
                return Results.Ok(new { status = "error", message = "Không tạo được session để import." });
            }

            var appended = await sessions
                .AppendAsync(sessionId, messages, cancellationToken)
                .ConfigureAwait(false);
            if (!appended)
            {
                return Results.Ok(new
                {
                    status = "error",
                    message = "Tạo được session nhưng không ghi được nội dung.",
                });
            }

            var document = await sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (document is null)
            {
                return Results.Ok(new
                {
                    status = "error",
                    message = "Không đọc được nội dung session sau khi import.",
                });
            }

            var persistedMessages = document.Messages.Count;
            var droppedEntries = Math.Max(0, transcript.Entries.Count - persistedMessages);
            if (persistedMessages == 0)
            {
                return Results.Ok(new
                {
                    status = "error",
                    message = "Session không có tin nhắn nào có thể lưu.",
                    dropped_entries = droppedEntries,
                    partial_import = true,
                });
            }

            completed = true;
            return Results.Ok(new
            {
                status = "ok",
                harness = format.Id,
                session = new
                {
                    id = sessionId,
                    title = document.Title,
                    messages = persistedMessages,
                },
                dropped_entries = droppedEntries,
                partial_import = droppedEntries > 0,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            LogFailure(loggers, "session import", error);
            return Results.Ok(new { status = "error", message = "Lỗi khi import session." });
        }
        finally
        {
            if (!completed && createdSessionId is not null)
            {
                await CleanupEmptyImportAsync(createdSessionId, sessions, loggers).ConfigureAwait(false);
            }
        }
    }

    private static async Task CleanupEmptyImportAsync(
        string sessionId, ISessionStore sessions, ILoggerFactory loggers)
    {
        // A cancelled request must not cancel its compensation. Preserve an
        // append that already committed, even if its acknowledgement was lost.
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            if (!await sessions.CleanupEmptyAsync(sessionId, cleanup.Token).ConfigureAwait(false))
            {
                throw new IOException("session_import_cleanup_failed");
            }
        }
        catch (Exception error)
        {
            // Cleanup diagnostics must not replace the request's original
            // cancellation or failure, and must not carry transcript content.
            loggers.CreateLogger("RuBackend.Business.SessionInterchange")
                .LogWarning(error, "Session import cleanup failed.");
        }
    }

    /// <summary>
    /// The directory a session ran in, when the store knows one.
    /// </summary>
    /// <remarks>
    /// Without it a Claude Code export lands under "unknown-project": readable, but not
    /// where that agent looks for its sessions.
    /// </remarks>
    private static async Task<string?> WorkingDirectoryForAsync(
        string sessionId,
        HarnessSqliteOptions sqliteOptions,
        SqliteWorkspaceStore workspaceStore,
        WorkspaceTrustService trustService,
        CancellationToken cancellationToken)
    {
        var workspaces = await SessionWorkspaceResolver.ResolveAsync(
            [sessionId], sqliteOptions, workspaceStore, trustService, cancellationToken).ConfigureAwait(false);
        return workspaces.TryGetValue(sessionId, out var workspace) ? workspace.Path : null;
    }

    /// <summary>
    /// A session's message log as a neutral transcript.
    /// </summary>
    /// <remarks>
    /// Every entry carries the session's creation time. Ru stores no per-message
    /// timestamp — its log is ordered by position — and handing a partner a duration that
    /// was never measured would be a worse answer than handing it one stamp for every
    /// line.
    /// </remarks>
    private static SessionTranscript ToTranscript(SessionDocument document, string? workingDirectory) => new(
        document.Id,
        document.Title,
        workingDirectory,
        document.CreatedAt,
        [.. document.Messages.Select(message => new TranscriptEntry(
            message.Role switch
            {
                // The session log spells the assistant "ai"; every partner spells it
                // "assistant", and a transcript that carried Ru's private word out would
                // read as an unknown role on the other side.
                "ai" => TranscriptEntryKind.AssistantMessage,
                "user" => TranscriptEntryKind.UserMessage,
                _ => TranscriptEntryKind.System,
            },
            document.CreatedAt,
            Text: message.Content))]);

    /// <summary>
    /// The turns that survive the trip into Ru's session log.
    /// </summary>
    /// <remarks>
    /// Ru's log holds turns, not steps. Its tool rows belong to runs that really executed
    /// something and live in the harness event log, so another agent's tool calls are
    /// counted as dropped rather than written in as messages.
    /// </remarks>
    private static List<SessionMessage> MessagesFrom(SessionTranscript transcript)
    {
        var messages = new List<SessionMessage>(transcript.Entries.Count);
        foreach (var entry in transcript.Entries)
        {
            var role = entry.Kind switch
            {
                // "ai", not "assistant": the store validates the role against its own
                // vocabulary and drops anything else without saying so.
                TranscriptEntryKind.AssistantMessage => "ai",
                TranscriptEntryKind.UserMessage => "user",
                _ => null,
            };

            if (role is null || string.IsNullOrWhiteSpace(entry.Text))
            {
                continue;
            }

            messages.Add(new SessionMessage(role, entry.Text));
        }

        return messages;
    }

    private static IResult ImportTooLarge() =>
        Results.Json(
            new
            {
                status = "error",
                message = $"import_too_large: session files are limited to {SessionInterchangeLimits.MaxImportContentBytes / (1024 * 1024)} MiB.",
            },
            statusCode: StatusCodes.Status413PayloadTooLarge);

    private static string KnownFormats() =>
        string.Join(", ", SessionFormatCatalog.All.Select(format => format.Id));

    private static void LogFailure(ILoggerFactory loggers, string operation, Exception error) =>
        loggers.CreateLogger("RuBackend.Business.SessionInterchange")
            .LogError(error, "Session interchange failed during {Operation}.", operation);
}

/// <summary>One session file, on its way into Ru.</summary>
public sealed record ImportSessionRequest(
    [property: JsonPropertyName("harness")] string? Harness,
    [property: JsonPropertyName("content")] string? Content);
