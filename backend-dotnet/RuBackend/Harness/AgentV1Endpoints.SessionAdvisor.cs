using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Persistence;
using RuBackend.Business.Settings;
using RuBackend.Providers;

namespace RuBackend.Harness;

/// <summary>
/// The advisor each session pins on its new root runs (ADR 0047, amended). A session starts
/// with none; <c>PUT</c> selects one (<c>advisor_backend</c> and <c>advisor_model</c>) or
/// clears it (both null). A change applies to the session's next run only.
/// </summary>
public static partial class AgentV1Endpoints
{
    private static async Task<IResult> GetSessionAdvisorAsync(
        string sessionId, SqliteAgentRunProviderSelectionStore store, CancellationToken ct)
    {
        sessionId = sessionId?.Trim() ?? string.Empty;
        if (!AgentV1Shared.IsSessionId(sessionId))
            return Results.Json(new { error_code = "invalid_session_id" }, statusCode: 400);
        var advisor = await store.GetSessionAdvisorAsync(sessionId, ct).ConfigureAwait(false);
        return Results.Json(AdvisorBody(sessionId, advisor));
    }

    private static async Task<IResult> PutSessionAdvisorAsync(
        string sessionId, HttpContext context, SqliteAgentRunProviderSelectionStore store, CancellationToken ct)
    {
        sessionId = sessionId?.Trim() ?? string.Empty;
        if (!AgentV1Shared.IsSessionId(sessionId))
            return Results.Json(new { error_code = "invalid_session_id" }, statusCode: 400);

        RuAgent.Harness.Core.RunProviderIntent? advisor;
        try
        {
            var text = await ReadBoundedBodyTextAsync(context.Request, ct).ConfigureAwait(false);
            if (text is null || JsonNode.Parse(text) is not JsonObject payload)
                return Results.Json(new { error_code = "invalid_request" }, statusCode: 400);
            advisor = AdvisorPolicy.ParseSessionRequest(payload, backend =>
                SettingsTransitionService.TryNormalizeBackend(backend)
                ?? throw new ArgumentException("advisor_backend is not a known provider.", nameof(backend)));
        }
        catch (RequestBodyTooLargeException)
        {
            return Results.Json(new { error_code = "request_too_large" }, statusCode: 413);
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or ArgumentException)
        {
            return Results.Json(new { error_code = "invalid_advisor" }, statusCode: 400);
        }

        await store.SetSessionAdvisorAsync(sessionId, advisor, ct).ConfigureAwait(false);
        return Results.Json(AdvisorBody(sessionId, advisor));
    }

    private static object AdvisorBody(string sessionId, RuAgent.Harness.Core.RunProviderIntent? advisor) => new
    {
        session_id = sessionId,
        advisor_backend = advisor?.Backend,
        advisor_model = advisor?.Model,
    };
}
