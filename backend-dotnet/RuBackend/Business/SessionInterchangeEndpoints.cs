using System.Text.Json.Serialization;
using RuAgent.SessionInterchange;
using RuBackend.Business.Sessions;

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
/// The request body limit is the host's (<c>BackendOptions.MaxRequestBodyBytes</c>,
/// 8 MiB by default). A session longer than that cannot be posted back in one piece;
/// that is a real bound, not a theoretical one, for a long coding session.
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
                await WorkingDirectoryForAsync(sessions, sessionId, cancellationToken).ConfigureAwait(false));
            var files = format.Write(transcript);

            return Results.Ok(new
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
            });
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
        ImportSessionRequest? request,
        ISessionStore sessions,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        try
        {
            if (SessionFormatCatalog.Find(request?.Harness ?? string.Empty) is not { } format)
            {
                return Results.Ok(new
                {
                    status = "error",
                    message = $"Unknown session format '{request?.Harness}'. Known formats: {KnownFormats()}.",
                });
            }

            if (string.IsNullOrWhiteSpace(request?.Content))
            {
                return Results.Ok(new { status = "error", message = "Cần nội dung file session để import." });
            }

            SessionTranscript transcript;
            try
            {
                transcript = format.Read(request.Content);
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

            var sessionId = await sessions.CreateAsync(cancellationToken).ConfigureAwait(false);
            if (sessionId is null)
            {
                return Results.Ok(new { status = "error", message = "Không tạo được session để import." });
            }

            var appended = await sessions
                .AppendAsync(sessionId, messages, cancellationToken)
                .ConfigureAwait(false);
            if (!appended)
            {
                // The session exists and is empty. Saying so beats reporting success and
                // leaving the caller with a session that looks imported but is not.
                return Results.Ok(new
                {
                    status = "error",
                    message = "Tạo được session nhưng không ghi được nội dung.",
                    session = new { id = sessionId },
                });
            }

            var document = await sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);

            return Results.Ok(new
            {
                status = "ok",
                harness = format.Id,
                session = new
                {
                    id = sessionId,
                    title = document?.Title,
                    messages = messages.Count,
                },
                dropped_entries = transcript.Entries.Count - messages.Count,
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
    }

    /// <summary>
    /// The directory a session ran in, when the store knows one.
    /// </summary>
    /// <remarks>
    /// Without it a Claude Code export lands under "unknown-project": readable, but not
    /// where that agent looks for its sessions.
    /// </remarks>
    private static async Task<string?> WorkingDirectoryForAsync(
        ISessionStore sessions,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var summaries = await sessions.ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var summary in summaries)
        {
            if (string.Equals(summary.Id, sessionId, StringComparison.Ordinal))
            {
                return summary.WorkspacePath;
            }
        }

        return null;
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
