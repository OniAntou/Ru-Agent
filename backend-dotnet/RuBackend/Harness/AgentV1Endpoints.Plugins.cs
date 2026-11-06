using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RuAgent.Protocol;
using RuBackend.Harness.Plugins;

namespace RuBackend.Harness;

public static partial class AgentV1Endpoints
{
    private static void MapPlugins(WebApplication app)
    {
        // User plugins (ADR 0044). Enabling a plugin adds guidance (skills, instructions) for
        // new runs; its hooks and MCP servers run only after the user approves exactly the
        // commands shown (approve carries the digest of what the user saw). Neither grants
        // execution authority over tool calls, which policy still decides.
        app.MapGet("/agent/v1/plugins", GetPlugins);
        app.MapPost("/agent/v1/plugins/settings", PostPluginSettingsAsync);
    }

    private sealed record PluginSettingsRequest(
        [property: JsonPropertyName("action")] string Action,
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("enabled")] bool? Enabled,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("with_instructions")] bool? WithInstructions,
        [property: JsonPropertyName("with_skill")] bool? WithSkill,
        [property: JsonPropertyName("digest")] string? Digest);

    private static IResult GetPlugins() => PluginListResponse();

    private static async Task<IResult> PostPluginSettingsAsync(HttpContext context, CancellationToken ct)
    {
        PluginSettingsRequest? request;
        try
        {
            var text = await ReadBoundedBodyTextAsync(context.Request, ct).ConfigureAwait(false);
            request = text is null ? null : JsonSerializer.Deserialize<PluginSettingsRequest>(text);
        }
        catch (RequestBodyTooLargeException) { return PluginError(413, "request_too_large", "Plugin request is too large."); }
        catch (Exception error) when (error is JsonException or DecoderFallbackException)
        { return PluginError(400, "invalid_request", "Invalid plugin request."); }

        if (request is null || !UserPlugins.IsValidId(request.Id))
            return PluginError(400, "invalid_plugin_id", "A plugin id is 1 to 24 lowercase letters, digits or dashes, starting with a letter.");

        try
        {
            switch (request.Action)
            {
                case "set_enabled" when request.Enabled is { } enabled:
                    if (enabled && UserPlugins.Discover().All(plugin => plugin.Id != request.Id))
                        return PluginError(404, "plugin_not_found", "No plugin with that id is installed.");
                    UserPlugins.SetEnabled(request.Id!, enabled);
                    break;
                case "approve" when request.Digest is { Length: 64 }:
                    // Consent binds to the exact commands the user was shown: if the plugin
                    // changed since the list was read, nothing is approved.
                    var surface = PluginExecutableSurface.ForAll().FirstOrDefault(item => item.Plugin.Id == request.Id);
                    if (surface is null) return PluginError(404, "plugin_not_found", "No plugin with that id is installed.");
                    if (surface.Digest is null) return PluginError(400, "plugin_runs_nothing", "This plugin declares no hooks or MCP servers.");
                    if (!string.Equals(surface.Digest, request.Digest, StringComparison.Ordinal))
                        return PluginError(409, "plugin_changed", "The plugin's commands changed since you looked; review them again.");
                    UserPlugins.SetApproval(request.Id!, request.Digest);
                    break;
                case "revoke":
                    UserPlugins.SetApproval(request.Id!, null);
                    break;
                case "create" when request.Name is { Length: > 0 }:
                    UserPlugins.Create(request.Id!, request.Name, request.Description,
                        request.WithInstructions ?? true, request.WithSkill ?? true);
                    break;
                default:
                    return PluginError(400, "invalid_request", "Invalid plugin action or arguments.");
            }
        }
        catch (ArgumentException error)
        {
            return PluginError(400, error.Message, "The plugin name or description is not valid.");
        }
        catch (IOException error) when (error.Message == "plugin_exists")
        {
            return PluginError(409, "plugin_exists", "A plugin with that id already exists.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return PluginError(500, "plugin_io_failed", "Ru could not write the plugin files.");
        }

        return PluginListResponse();
    }

    private static IResult PluginListResponse()
    {
        var skipped = new List<string>();
        IReadOnlyList<PluginExecutableSurface> plugins;
        try
        {
            plugins = PluginExecutableSurface.ForAll(skipped: skipped);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return PluginError(500, "plugin_io_failed", "Ru could not read the plugin folders.");
        }

        return Results.Json(new
        {
            local_root = UserPlugins.LocalRoot(),
            plugins = plugins.Select(surface => new
            {
                id = surface.Plugin.Id,
                name = surface.Plugin.Manifest.Name,
                description = surface.Plugin.Manifest.Description,
                version = surface.Plugin.Manifest.Version,
                source = surface.Plugin.Source == UserPluginSource.Managed ? "managed" : "local",
                directory = surface.Plugin.Directory,
                enabled = surface.Plugin.Enabled,
                has_skills = surface.Plugin.HasSkills,
                has_instructions = surface.Plugin.HasInstructions,
                has_hooks = surface.Hooks.Count > 0,
                has_mcp_servers = surface.Servers.Count > 0,
                diagnostics = surface.Plugin.Diagnostics.Concat(surface.Problems),
                consent = surface.Consent switch
                {
                    PluginConsentState.NeedsApproval => "needs_approval",
                    PluginConsentState.Approved => "approved",
                    PluginConsentState.Changed => "changed",
                    _ => "not_needed",
                },
                executable = surface.Lines,
                digest = surface.Digest,
            }),
            skipped,
        });
    }

    private static IResult PluginError(int status, string? code, string? message) =>
        Results.Json(new { error_code = code, message }, statusCode: status);
}
