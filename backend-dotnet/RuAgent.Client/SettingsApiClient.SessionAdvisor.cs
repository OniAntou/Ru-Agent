using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuAgent.Client;

/// <summary>A session's advisor: the provider and model its new runs pin, or none.</summary>
public sealed record SessionAdvisor(string Backend, string Model);

public sealed partial class SettingsApiClient
{
    /// <summary>
    /// The advisor <paramref name="sessionId"/>'s new runs consult (ADR 0047), or null when the
    /// session has none, which is where every new session starts.
    /// </summary>
    public Task<SessionAdvisor?> GetSessionAdvisorAsync(string sessionId, CancellationToken cancellationToken = default) =>
        SendSessionAdvisorAsync(HttpMethod.Get, sessionId, body: null, cancellationToken);

    /// <summary>Selects the session's advisor, or clears it when <paramref name="advisor"/> is null.</summary>
    public Task<SessionAdvisor?> SetSessionAdvisorAsync(
        string sessionId, SessionAdvisor? advisor, CancellationToken cancellationToken = default) =>
        SendSessionAdvisorAsync(HttpMethod.Put, sessionId,
            new JsonObject { ["advisor_backend"] = advisor?.Backend, ["advisor_model"] = advisor?.Model },
            cancellationToken);

    private async Task<SessionAdvisor?> SendSessionAdvisorAsync(
        HttpMethod method, string sessionId, JsonObject? body, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var url = new Uri(_session.BaseUri, $"agent/v1/sessions/{Uri.EscapeDataString(sessionId)}/advisor");
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.BearerToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new AgentApiException("unauthorized", "Bearer rejected.", (int)response.StatusCode);

        var content = await BoundedHttpResponseReader.ReadAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new AgentApiException(
                ExtractErrorCode(content) ?? "session_advisor_failed",
                $"HTTP {(int)response.StatusCode}",
                (int)response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (!root.TryGetProperty("session_id", out var owner) || owner.GetString() != sessionId)
                throw new AgentApiException("invalid_response", "Session advisor response does not match its session.");
            var backend = root.TryGetProperty("advisor_backend", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
            var model = root.TryGetProperty("advisor_model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            return string.IsNullOrWhiteSpace(backend) || string.IsNullOrWhiteSpace(model) ? null : new SessionAdvisor(backend, model);
        }
        catch (JsonException error)
        {
            throw new AgentApiException("invalid_response", error.Message);
        }
    }
}
