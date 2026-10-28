using System.Text.Json.Serialization;
using RuBackend.Business.Sessions;

namespace RuBackend.Business;

public static partial class BusinessEndpoints
{
    public sealed record ForkSessionRequest(
        [property: JsonPropertyName("keep_messages")] int KeepMessages,
        [property: JsonPropertyName("history_revision")] string? HistoryRevision);

    private static async Task<IResult> ForkSessionAsync(string sessionId, ForkSessionRequest request,
        ISessionStore sessions, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        try
        {
            var result = await sessions.ForkAsync(sessionId, request.KeepMessages, request.HistoryRevision ?? "", cancellationToken);
            return result.Document is { } document
                ? Results.Ok(new { status = "ok", session = document, parent_session_id = sessionId })
                : Results.BadRequest(new { status = "error", error_code = result.ErrorCode, message = "History branch was not created. Reopen /history." });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            LogFailure(loggers, "session fork", error);
            return Results.Json(new { status = "error", error_code = "session_fork_failed", message = "History branch was not confirmed." }, statusCode: 500);
        }
    }
}
