using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace RuAgent.Client;

/// <summary>Authenticated catalog and workspace-local skill preference API.</summary>
public sealed class SkillsApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _httpClient;
    private readonly PublicHostSession _session;
    private readonly bool _ownsHttpClient;

    public SkillsApiClient(PublicHostSession session, HttpClient? httpClient = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    public async Task<SkillCatalogResponse> ListAsync(
        string workspacePath, string? query = null, bool refresh = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        var queryString = $"workspace_path={Uri.EscapeDataString(workspacePath)}";
        if (!string.IsNullOrWhiteSpace(query)) queryString += $"&query={Uri.EscapeDataString(query)}";
        if (refresh) queryString += "&refresh=true";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_session.BaseUri, $"agent/v1/skills?{queryString}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.BearerToken);
        return await SendAsync(request, "skills_list_failed", cancellationToken).ConfigureAwait(false);
    }

    public async Task<SkillCatalogResponse> UpdateAsync(SkillSettingsRequest settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.WorkspacePath);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_session.BaseUri, "agent/v1/skills/settings"))
        {
            Content = new StringContent(JsonSerializer.Serialize(settings), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.BearerToken);
        return await SendAsync(request, "skills_update_failed", cancellationToken).ConfigureAwait(false);
    }

    private async Task<SkillCatalogResponse> SendAsync(HttpRequestMessage request, string failureCode, CancellationToken cancellationToken)
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
                    && suppliedCode.ValueKind == JsonValueKind.String && suppliedCode.GetString() is { Length: > 0 and <= 96 } named)
                    code = named;
                if (error.RootElement.TryGetProperty("message", out var suppliedMessage)
                    && suppliedMessage.ValueKind == JsonValueKind.String && suppliedMessage.GetString() is { Length: > 0 and <= 512 } safeMessage)
                    message = safeMessage;
            }
            catch (JsonException) { }
            throw new AgentApiException(code, message, (int)response.StatusCode);
        }
        try
        {
            var result = JsonSerializer.Deserialize<SkillCatalogResponse>(body, JsonOptions);
            if (result?.Items is null || result.Diagnostics is null || result.Total < 0
                || result.Items.Count > 128 || result.Diagnostics.Count > 128
                || result.Items.Any(item => item is null || !RuAgent.Protocol.SkillIdentity.IsValid(item.Id)
                    || string.IsNullOrWhiteSpace(item.Name) || item.Description is null
                    || string.IsNullOrWhiteSpace(item.SourcePath) || string.IsNullOrWhiteSpace(item.ResourceBase)
                    || item.Revision is null || item.Requirements is null)
                || result.Diagnostics.Any(item => item is null || item.SourcePath is null || string.IsNullOrWhiteSpace(item.Code)))
                throw new AgentApiException("invalid_response", "Invalid skills catalog response.");
            return result;
        }
        catch (JsonException ex)
        {
            throw new AgentApiException("invalid_response", ex.Message);
        }
    }
}
