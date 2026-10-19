using System.Text;
using System.Text.Json;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Client;

public sealed partial class AgentApiClient
{
    public Task<SubagentListResponse> ListSubagentsAsync(string rootRunId, CancellationToken ct = default) =>
        SendSubagentRequestAsync<SubagentListResponse>(rootRunId, null, "", HttpMethod.Get, null, ct);

    public Task<SubagentDetailResponse> GetSubagentAsync(string rootRunId, string agentId,
        long? resultVersion = null, int offset = 0, int limit = 32768, CancellationToken ct = default)
    {
        if (resultVersion is < 1 or > 3 || offset < 0 || limit is < 4 or > 32768 || resultVersion is null && offset != 0)
            throw new AgentApiException("invalid_subagent_arguments", "Invalid result page");
        var query = resultVersion is null ? "" : FormattableString.Invariant($"?result_version={resultVersion}&offset={offset}&limit={limit}");
        return SendSubagentRequestAsync<SubagentDetailResponse>(rootRunId, agentId, query, HttpMethod.Get, null, ct);
    }

    private async Task<T> SendSubagentRequestAsync<T>(string rootRunId, string? agentId, string suffix,
        HttpMethod method, object? body, CancellationToken ct)
    {
        ValidateRunId(rootRunId);
        if (agentId is not null && !SubagentContractValidator.IsValidId(agentId))
            throw new AgentApiException("invalid_subagent_arguments", "Invalid agent ID");
        var path = $"agent/v1/runs/{rootRunId}/subagents" + (agentId is null ? "" : "/" + Uri.EscapeDataString(agentId)) + suffix;
        using var request = new HttpRequestMessage(method, new Uri(_session.BaseUri, path));
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        EnsureAuth(request);
        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        var json = await ReadBoundedResponseAsync(response.Content, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new AgentApiException(ExtractErrorCode(json) ?? "subagent_request_failed", json, (int)response.StatusCode);
        try { return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? throw new JsonException("Empty subagent response"); }
        catch (JsonException e) { throw new AgentApiException("invalid_response", e.Message); }
    }
}
