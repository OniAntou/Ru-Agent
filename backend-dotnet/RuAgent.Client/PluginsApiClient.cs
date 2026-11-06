using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RuAgent.Client;

public sealed record PluginItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("directory")] string Directory,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("has_skills")] bool HasSkills,
    [property: JsonPropertyName("has_instructions")] bool HasInstructions,
    [property: JsonPropertyName("has_hooks")] bool HasHooks,
    [property: JsonPropertyName("has_mcp_servers")] bool HasMcpServers,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<string> Diagnostics,
    [property: JsonPropertyName("consent")] string Consent = "not_needed",
    [property: JsonPropertyName("executable")] IReadOnlyList<string>? Executable = null,
    [property: JsonPropertyName("digest")] string? Digest = null)
{
    /// <summary>Hooks or MCP servers that wait on the user's approval, first-time or after a change.</summary>
    public bool NeedsConsent => Consent is "needs_approval" or "changed";
}

public sealed record PluginPackageReceipt(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("commit")] string Commit,
    [property: JsonPropertyName("package_sha256")] string PackageSha256,
    [property: JsonPropertyName("file_count")] int FileCount);

public sealed record PluginListResponse(
    [property: JsonPropertyName("local_root")] string LocalRoot,
    [property: JsonPropertyName("plugins")] IReadOnlyList<PluginItem> Plugins,
    [property: JsonPropertyName("skipped")] IReadOnlyList<string> Skipped,
    [property: JsonPropertyName("receipt")] PluginPackageReceipt? Receipt = null);

public sealed record PluginSettingsRequest(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("enabled")] bool? Enabled = null,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("with_instructions")] bool? WithInstructions = null,
    [property: JsonPropertyName("with_skill")] bool? WithSkill = null,
    [property: JsonPropertyName("digest")] string? Digest = null);

/// <summary>Authenticated user plugin list, enable switch and creation (ADR 0044).</summary>
public sealed class PluginsApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _httpClient;
    private readonly PublicHostSession _session;
    private readonly bool _ownsHttpClient;

    public PluginsApiClient(PublicHostSession session, HttpClient? httpClient = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    public async Task<PluginListResponse> ListAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_session.BaseUri, "agent/v1/plugins"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.BearerToken);
        return await SendAsync(request, "plugins_list_failed", cancellationToken).ConfigureAwait(false);
    }

    public async Task<PluginListResponse> UpdateAsync(PluginSettingsRequest settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Action is not ("set_enabled" or "create" or "approve" or "revoke"))
            throw new ArgumentException("Choose set_enabled, create, approve or revoke.", nameof(settings));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_session.BaseUri, "agent/v1/plugins/settings"))
        {
            Content = new StringContent(JsonSerializer.Serialize(settings), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.BearerToken);
        return await SendAsync(request, "plugins_update_failed", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Installs or updates a plugin from a GitHub tree URL pinned to a full commit.</summary>
    public async Task<PluginListResponse> InstallPackageAsync(string sourceUrl, bool update, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceUrl);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_session.BaseUri, "agent/v1/plugins/packages"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { source_url = sourceUrl, action = update ? "update" : "install" }),
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.BearerToken);
        var result = await SendAsync(request, "plugins_install_failed", cancellationToken).ConfigureAwait(false);
        if (result.Receipt is not { } receipt || !RuAgent.Protocol.UserPlugins.IsValidId(receipt.Id)
            || receipt.Action is not ("installed" or "updated") || receipt.Commit is not { Length: 40 }
            || receipt.PackageSha256 is not { Length: 64 } || receipt.FileCount is < 1 or > 256)
            throw new AgentApiException("invalid_response", "Invalid plugin package receipt.");
        return result;
    }

    /// <summary>Copies a plugin draft folder into the user's plugins; it is added switched off.</summary>
    public async Task<PluginListResponse> ImportAsync(string folder, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_session.BaseUri, "agent/v1/plugins/settings"))
        {
            Content = new StringContent(JsonSerializer.Serialize(new { action = "import", path = folder }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.BearerToken);
        return await SendAsync(request, "plugins_import_failed", cancellationToken).ConfigureAwait(false);
    }

    private async Task<PluginListResponse> SendAsync(HttpRequestMessage request, string failureCode, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new AgentApiException("unauthorized", "Bearer rejected.", (int)response.StatusCode);
        var body = await BoundedHttpResponseReader.ReadAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var code = failureCode;
            var message = $"HTTP {(int)response.StatusCode}";
            try
            {
                using var error = JsonDocument.Parse(body);
                if (error.RootElement.TryGetProperty("error_code", out var suppliedCode)
                    && suppliedCode.GetString() is { Length: > 0 and <= 96 } named)
                    code = named;
                if (error.RootElement.TryGetProperty("message", out var suppliedMessage)
                    && suppliedMessage.GetString() is { Length: > 0 and <= 512 } safeMessage)
                    message = safeMessage;
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException) { }
            throw new AgentApiException(code, message, (int)response.StatusCode);
        }

        try
        {
            var result = JsonSerializer.Deserialize<PluginListResponse>(body, JsonOptions);
            if (result?.Plugins is null || result.Skipped is null || string.IsNullOrWhiteSpace(result.LocalRoot)
                || result.Plugins.Count > RuAgent.Protocol.UserPlugins.MaxPlugins
                || result.Plugins.Any(plugin => plugin is null || !RuAgent.Protocol.UserPlugins.IsValidId(plugin.Id)
                    || string.IsNullOrWhiteSpace(plugin.Name) || string.IsNullOrWhiteSpace(plugin.Directory)
                    || plugin.Source is not ("local" or "managed") || plugin.Diagnostics is null
                    || plugin.Consent is not ("not_needed" or "needs_approval" or "approved" or "changed")
                    || (plugin.Consent != "not_needed" && (plugin.Digest is not { Length: 64 } || plugin.Executable is not { Count: > 0 }))))
                throw new AgentApiException("invalid_response", "Invalid plugin list response.");
            return result;
        }
        catch (JsonException error)
        {
            throw new AgentApiException("invalid_response", error.Message);
        }
    }
}
