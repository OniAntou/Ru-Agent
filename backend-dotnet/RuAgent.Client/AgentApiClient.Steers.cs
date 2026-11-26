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
    public Task<AgentSteerResponse> SteerAsync(string runId, string steerId, string text, CancellationToken ct = default) =>
        SteerAsync(runId, steerId, text, null, ct);

    /// <summary>
    /// Sends a steer with up to <see cref="AgentSteerLimits.MaxImages"/> image data URLs; the
    /// Host checks them as it checks a new run's images. Text may be empty when images carry it.
    /// </summary>
    public async Task<AgentSteerResponse> SteerAsync(
        string runId, string steerId, string text, IReadOnlyList<string>? images, CancellationToken ct = default)
    {
        ValidateRunId(runId);
        if (!AgentSteerLimits.IsValidSteerId(steerId))
            throw new AgentApiException("invalid_steer_id", "steer_id invalid");
        text ??= string.Empty;
        var imageCount = images?.Count ?? 0;
        if (string.IsNullOrWhiteSpace(text) && imageCount == 0 || text.Length > AgentSteerLimits.MaxCharacters)
            throw new AgentApiException("invalid_steer_text", $"text must be 1..{AgentSteerLimits.MaxCharacters} characters, or images must be attached");
        if (imageCount > AgentSteerLimits.MaxImages)
            throw new AgentApiException("image_limit_exceeded", $"at most {AgentSteerLimits.MaxImages} images");
        var url = new Uri(_session.BaseUri, $"agent/v1/runs/{runId}/steers");
        var payload = imageCount == 0
            ? JsonSerializer.Serialize(new { steer_id = steerId, text })
            : JsonSerializer.Serialize(new { steer_id = steerId, text, images });

        // A response lost in transit leaves the caller unsure whether the steer was stored.
        // steer_id makes the request idempotent at the Host (ADR 0056), so a transport failure
        // is retried with the same id: a stored steer answers 200 from its row, never a second
        // message. A refusal is an answer, not a lost request, and is never retried.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await SendSteerAsync(url, payload, ct).ConfigureAwait(false);
            }
            catch (Exception error) when (attempt < SteerTransportRetries
                && !ct.IsCancellationRequested
                && error is HttpRequestException or IOException or TaskCanceledException)
            {
                await Task.Delay(SteerRetryDelay(attempt), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Attempts of a steer's POST before a transport failure is reported.</summary>
    internal const int SteerTransportRetries = 2;

    private static TimeSpan SteerRetryDelay(int attempt) =>
        TimeSpan.FromMilliseconds(attempt == 0 ? 200 : 500);

    private async Task<AgentSteerResponse> SendSteerAsync(Uri url, string payload, CancellationToken ct)
    {
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
