using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence;
using RuAgent.Protocol.Subagents;
using RuBackend.Harness.Subagents;

namespace RuBackend.Harness;

public static partial class AgentV1Endpoints
{
    private static void MapSubagents(WebApplication app)
    {
        app.MapGet("/agent/v1/runs/{runId}/subagents", ListSubagentsAsync);
        app.MapGet("/agent/v1/runs/{runId}/subagents/{agentId}", GetSubagentAsync);
    }

    private static Task<IResult> ListSubagentsAsync(string runId, SubagentPublicService service, CancellationToken ct) =>
        SubagentResponseAsync(async () => await service.ListAsync(new(runId), ct), 200);

    private static Task<IResult> GetSubagentAsync(string runId, string agentId, HttpContext context,
        SubagentPublicService service, CancellationToken ct) => SubagentResponseAsync(async () =>
        {
            var query = context.Request.Query;
            long? version = query.ContainsKey("result_version") ? long.Parse(query["result_version"]!, System.Globalization.CultureInfo.InvariantCulture) : null;
            var offset = query.ContainsKey("offset") ? int.Parse(query["offset"]!, System.Globalization.CultureInfo.InvariantCulture) : 0;
            var limit = query.ContainsKey("limit") ? int.Parse(query["limit"]!, System.Globalization.CultureInfo.InvariantCulture) : 32768;
            if (version is null && (offset != 0 || query.ContainsKey("limit"))) throw new FormatException();
            return await service.DetailAsync(new(runId), agentId, version, offset, limit, ct);
        }, 200);

    private static async Task<IResult> SubagentResponseAsync(Func<Task<object>> action, int success)
    {
        try { return Results.Json(await action(), statusCode: success); }
        catch (HarnessStoreException e)
        {
            var status = e.Code is "subagent_root_not_found" or "subagent_not_found" or "subagent_result_not_found" ? 404
                : e.Code is "invalid_subagent_arguments" or "subagent_page_invalid" ? 400 : 409;
            return Results.Json(new { error_code = e.Code }, statusCode: status);
        }
        catch (RequestBodyTooLargeException) { return Results.Json(new { error_code = "request_too_large" }, statusCode: 413); }
        catch (Exception e) when (e is JsonException or FormatException or OverflowException or ArgumentException)
        { return Results.Json(new { error_code = "invalid_request" }, statusCode: 400); }
    }
}
