using System.Text;
using System.Text.Json;
using RuAgent.Protocol.Harness;

namespace RuAgent.Client;

public sealed partial class AgentApiClient
{
    /// <summary>
    /// Sends a steer into a working run (ADR 0056). The Host holds it durably and the model
    /// receives it at the next step boundary. Reuse <paramref name="steerId"/> to retry safely.
    /// A run that is not working answers <c>run_not_steerable</c>: send the text as a new turn.
    /// </summary>
    public async Task<AgentSteerResponse> SteerAsync(string runId, string steerId, string text, CancellationToken ct = default)
    {
        ValidateRunId(runId);
        if (!AgentSteerLimits.IsValidSteerId(steerId))
            throw new AgentApiException("invalid_steer_id", "steer_id invalid");
        if (string.IsNullOrWhiteSpace(text) || text.Length > AgentSteerLimits.MaxCharacters)
            throw new AgentApiException("invalid_steer_text", $"text must be 1..{AgentSteerLimits.MaxCharacters} characters");
        var url = new Uri(_session.BaseUri, $"agent/v1/runs/{runId}/steers");
        var payload = JsonSerializer.Serialize(new { steer_id = steerId, text });
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        EnsureAuth(req);
        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var body = await ReadBoundedResponseAsync(resp.Content, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new AgentApiException(ExtractErrorCode(body) ?? "steer_failed", body, (int)resp.StatusCode);
        return JsonSerializer.Deserialize<AgentSteerResponse>(body)
            ?? throw new AgentApiException("steer_response_invalid", body, (int)resp.StatusCode);
    }

    /// <summary>The run's steers, with the Host's state: pending, delivered or returned.</summary>
    public async Task<AgentSteerListResponse> ListSteersAsync(string runId, CancellationToken ct = default)
    {
        ValidateRunId(runId);
        var url = new Uri(_session.BaseUri, $"agent/v1/runs/{runId}/steers");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        EnsureAuth(req);
        using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var body = await ReadBoundedResponseAsync(resp.Content, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new AgentApiException(ExtractErrorCode(body) ?? "steer_list_failed", body, (int)resp.StatusCode);
        return JsonSerializer.Deserialize<AgentSteerListResponse>(body)
            ?? throw new AgentApiException("steer_response_invalid", body, (int)resp.StatusCode);
    }
}
