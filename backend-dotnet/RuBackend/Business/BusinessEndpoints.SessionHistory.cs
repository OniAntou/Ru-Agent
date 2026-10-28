using Microsoft.AspNetCore.Http;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Workspace;
using RuBackend.Business.Sessions;
using RuBackend.Harness.Workspace;

namespace RuBackend.Business;

public static partial class BusinessEndpoints
{
    private static void MapSessionHistory(WebApplication app)
    {
        app.MapGet("/sessions/page", ListSessionPageAsync);
        app.MapGet("/sessions/search", SearchSessionMessagesAsync);
        app.MapGet("/sessions/{sessionId}/messages", GetSessionMessagePageAsync);
    }

    private static async Task<IResult> GetSessionMessagePageAsync(string sessionId, HttpRequest request,
        ISessionStore sessions, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        if (!TryHistoryArguments(request, 200, out var cursor, out var limit, out var query) || query is not null)
            return HistoryError("session_history_arguments_invalid", 400);
        try
        {
            var page = await sessions.GetMessagePageAsync(sessionId, cursor, limit, cancellationToken).ConfigureAwait(false);
            return page is null ? HistoryError("session_not_found", 404) : Results.Ok(new
            {
                status = "ok", session_id = sessionId, messages = page.Messages, start_index = page.StartIndex,
                total_messages = page.TotalMessages, next_cursor = page.NextCursor, history_epoch = page.HistoryEpoch,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (SessionHistoryException error) { return HistoryError(error.Code, error.Code.Contains("stale", StringComparison.Ordinal) ? 409 : 400); }
        catch (Exception error)
        {
            LogFailure(loggers, "session history page", error);
            return HistoryError("session_history_unavailable", 503);
        }
    }

    private static async Task<IResult> ListSessionPageAsync(HttpRequest request, ISessionStore sessions,
        HarnessSqliteOptions sqliteOptions, SqliteWorkspaceStore workspaceStore,
        WorkspaceTrustService trustService, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        if (!TryHistoryArguments(request, 100, out var cursor, out var limit, out var query))
            return HistoryError("session_history_arguments_invalid", 400);
        try
        {
            var page = await sessions.ListPageAsync(cursor, limit, query, cancellationToken).ConfigureAwait(false);
            var rows = await ProjectSessionSummariesAsync(page.Sessions, sqliteOptions, workspaceStore,
                trustService, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new { status = "ok", sessions = rows, next_cursor = page.NextCursor,
                sessions_truncated = page.NextCursor is not null });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (SessionHistoryException error) { return HistoryError(error.Code, error.Code.Contains("stale", StringComparison.Ordinal) ? 409 : 400); }
        catch (Exception error)
        {
            LogFailure(loggers, "session listing page", error);
            return HistoryError("session_history_unavailable", 503);
        }
    }

    private static async Task<IResult> SearchSessionMessagesAsync(HttpRequest request, ISessionStore sessions,
        ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        if (!TryHistoryArguments(request, 50, out var cursor, out var limit, out var query)
            || cursor is not null || string.IsNullOrWhiteSpace(query))
            return HistoryError("session_search_arguments_invalid", 400);
        try
        {
            var hits = await sessions.SearchMessagesAsync(query, limit, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new { status = "ok", hits = hits.Select(hit => new
            {
                session_id = hit.SessionId, message_index = hit.MessageIndex, role = hit.Role, snippet = hit.Snippet,
            }).ToArray() });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (SessionHistoryException error) { return HistoryError(error.Code, 400); }
        catch (Exception error)
        {
            LogFailure(loggers, "session message search", error);
            return HistoryError("session_search_unavailable", 503);
        }
    }

    private static bool TryHistoryArguments(HttpRequest request, int defaultLimit,
        out string? cursor, out int limit, out string? query)
    {
        cursor = null; query = null; limit = defaultLimit;
        if (request.Query.Any(pair => pair.Value.Count != 1 || pair.Key is not ("cursor" or "limit" or "query"))) return false;
        if (request.Query.TryGetValue("cursor", out var cursorValue))
        {
            cursor = cursorValue.ToString();
            if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 2048 || cursor.Contains('\0')) return false;
        }
        if (request.Query.TryGetValue("limit", out var limitValue)
            && (!int.TryParse(limitValue, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 200)) return false;
        if (request.Query.TryGetValue("query", out var queryValue))
        {
            query = queryValue.ToString().Trim();
            if (query.Length > 512 || query.Contains('\0')) return false;
            if (query.Length == 0) query = null;
        }
        return true;
    }

    private static IResult HistoryError(string code, int statusCode) =>
        Results.Json(new { status = "error", code }, statusCode: statusCode);

    private static async Task<object[]> ProjectSessionSummariesAsync(IReadOnlyList<SessionSummary> values,
        HarnessSqliteOptions sqliteOptions, SqliteWorkspaceStore workspaceStore,
        WorkspaceTrustService trustService, CancellationToken cancellationToken)
    {
        var ids = values.Select(value => value.Id).ToArray();
        var running = await SessionRunActivityResolver.ResolveAsync(ids, sqliteOptions, cancellationToken).ConfigureAwait(false);
        var workspaces = await SessionWorkspaceResolver.ResolveAsync(ids, sqliteOptions, workspaceStore,
            trustService, cancellationToken).ConfigureAwait(false);
        return values.Select(value =>
        {
            if (workspaces.TryGetValue(value.Id, out var workspace)) value = value with
            {
                WorkspaceId = workspace.WorkspaceId, WorkspaceLabel = workspace.Label, WorkspacePath = workspace.Path,
            };
            return new
            {
                id = value.Id, title = value.Title, created_at = value.CreatedAt.ToUniversalTime().ToString("O"),
                updated_at = value.UpdatedAt.ToUniversalTime().ToString("O"),
                last_agent_activity_at = value.LastAgentActivityAt?.ToUniversalTime().ToString("O"),
                message_count = value.MessageCount, parent_session_id = value.ParentSessionId,
                forked_from_message_id = value.ForkedFromMessageId, workspace_id = value.WorkspaceId,
                workspace_label = value.WorkspaceLabel, workspace_path = value.WorkspacePath,
                client_source = value.ClientSource, is_agent_running = running.Contains(value.Id),
            };
        }).ToArray();
    }
}
