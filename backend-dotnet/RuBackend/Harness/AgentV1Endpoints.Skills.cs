using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RuAgent.Broker.Skills;
using RuBackend.Security;

namespace RuBackend.Harness;

public static partial class AgentV1Endpoints
{
    private static void MapSkills(WebApplication app)
    {
        // Available before run creation; these endpoints manage guidance, never execution grants.
        app.MapGet("/agent/v1/skills", GetSkillsAsync);
        app.MapPost("/agent/v1/skills/settings", PostSkillSettingsAsync);
        app.MapPost("/agent/v1/skills/packages", PostSkillPackageAsync);
    }

    private static async Task<IResult> GetSkillsAsync(HttpContext context, CancellationToken ct)
    {
        var workspace = context.Request.Query["workspace_path"].ToString();
        if (!TrySkillWorkspace(workspace, out var canonical))
            return SkillError(400, "invalid_workspace", "An existing absolute workspace path is required.");
        var query = context.Request.Query["query"].ToString();
        if (query.Length > 256) return SkillError(400, "invalid_query", "Search is limited to 256 characters.");
        var refresh = bool.TryParse(context.Request.Query["refresh"], out var requested) && requested;
        return await SkillCatalogResponseAsync(new SkillCatalog(canonical), query, refresh, ct).ConfigureAwait(false);
    }

    private sealed record SkillSettingsRequest(
        [property: JsonPropertyName("workspace_path")] string WorkspacePath,
        [property: JsonPropertyName("action")] string Action,
        [property: JsonPropertyName("root")] string? Root,
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("enabled")] bool? Enabled);

    private sealed record SkillPackageRequest(
        [property: JsonPropertyName("workspace_path")] string WorkspacePath,
        [property: JsonPropertyName("source_url")] string SourceUrl,
        [property: JsonPropertyName("action")] string Action);

    private static async Task<IResult> PostSkillPackageAsync(
        HttpContext context, SkillPackageInstaller installer, CancellationToken ct)
    {
        SkillPackageRequest? request;
        try
        {
            var text = await ReadBoundedBodyTextAsync(context.Request, ct, 8 * 1024).ConfigureAwait(false);
            request = text is null ? null : JsonSerializer.Deserialize<SkillPackageRequest>(text);
        }
        catch (RequestBodyTooLargeException) { return SkillError(413, "request_too_large", "Skill package request is too large."); }
        catch (Exception error) when (error is JsonException or DecoderFallbackException)
        { return SkillError(400, "invalid_request", "Invalid skill package request."); }
        if (request is null || !TrySkillWorkspace(request.WorkspacePath, out var canonical))
            return SkillError(400, "invalid_workspace", "An existing absolute workspace path is required.");
        if (string.IsNullOrWhiteSpace(request.SourceUrl) || request.SourceUrl.Length > 2048)
            return SkillError(400, "invalid_skill_source", "A pinned GitHub tree URL is required.");
        var action = request.Action switch
        {
            "install" => SkillPackageInstallAction.Install,
            "update" => SkillPackageInstallAction.Update,
            _ => (SkillPackageInstallAction?)null,
        };
        if (action is null) return SkillError(400, "invalid_skill_action", "Choose explicit install or update.");
        try
        {
            var receipt = await installer.InstallAsync(request.SourceUrl, action.Value, ct).ConfigureAwait(false);
            var catalog = await SkillCatalogPayloadAsync(new SkillCatalog(canonical), null, true, ct).ConfigureAwait(false);
            return Results.Json(new { receipt, catalog });
        }
        catch (SkillPackageInstallException error)
        {
            var status = error.Code switch
            {
                "skill_target_exists" or "skill_update_target_missing" or "skill_target_modified" or "skill_target_unmanaged" => 409,
                "skill_source_unavailable" or "skill_source_redirect" => 502,
                "skill_source_timeout" => 504,
                "skill_install_busy" => 409,
                "skill_archive_too_large" => 413,
                "skill_install_failed" or "skill_install_root_unsafe" => 500,
                _ => 400,
            };
            return SkillError(status, error.Code, error.Message);
        }
    }

    private static async Task<IResult> PostSkillSettingsAsync(HttpContext context, CancellationToken ct)
    {
        SkillSettingsRequest? request;
        try
        {
            var text = await ReadBoundedBodyTextAsync(context.Request, ct).ConfigureAwait(false);
            request = text is null ? null : JsonSerializer.Deserialize<SkillSettingsRequest>(text);
        }
        catch (RequestBodyTooLargeException) { return SkillError(413, "request_too_large", "Settings request is too large."); }
        catch (Exception error) when (error is JsonException or DecoderFallbackException)
        { return SkillError(400, "invalid_request", "Invalid settings request."); }
        if (request is null || !TrySkillWorkspace(request.WorkspacePath, out var canonical))
            return SkillError(400, "invalid_workspace", "An existing absolute workspace path is required.");
        var catalog = new SkillCatalog(canonical);
        SkillPreferenceResult? result = request.Action switch
        {
            "refresh" => null,
            "add_root" when request.Root is { Length: > 0 and <= 4096 } => catalog.AddRoot(request.Root),
            "remove_root" when request.Root is { Length: > 0 and <= 4096 } => catalog.RemoveRoot(request.Root),
            "set_enabled" when request.Id is not null && request.Enabled is not null => catalog.SetEnabled(request.Id, request.Enabled.Value),
            "set_implicit" when request.Id is not null && request.Enabled is not null => catalog.SetImplicit(request.Id, request.Enabled.Value),
            _ => new SkillPreferenceResult(false, "invalid_request", "Invalid skill settings action or arguments."),
        };
        if (result is { Success: false }) return SkillError(400, result.Code, result.Message);
        return await SkillCatalogResponseAsync(catalog, null, true, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> SkillCatalogResponseAsync(SkillCatalog catalog, string? query, bool refresh, CancellationToken ct)
    {
        return Results.Json(await SkillCatalogPayloadAsync(catalog, query, refresh, ct).ConfigureAwait(false));
    }

    private static async Task<object> SkillCatalogPayloadAsync(SkillCatalog catalog, string? query, bool refresh, CancellationToken ct)
    {
        var result = await catalog.DiscoverAsync(query, refresh, ct).ConfigureAwait(false);
        return new
        {
            items = result.Items.Select(item => new
            {
                id = item.Id, name = item.Name, description = item.Description,
                source_path = item.SourcePath, resource_base = item.ResourceBase, revision = item.Revision,
                enabled = item.Enabled, model_invocable = item.ModelInvocable, user_invocable = item.UserInvocable,
                requirements = item.Requires, compatibility = item.Compatibility,
            }),
            diagnostics = result.Diagnostics.Select(item => new { source_path = item.SourcePath, code = item.Code }),
            truncated = result.Truncated, total = result.Total,
        };
    }

    private static bool TrySkillWorkspace(string? path, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || !Path.IsPathFullyQualified(path)) return false;
        try
        {
            canonical = Path.GetFullPath(path);
            return Directory.Exists(canonical);
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        { return false; }
    }

    private static IResult SkillError(int status, string? code, string? message) =>
        Results.Json(new { error_code = code, message }, statusCode: status);
}
