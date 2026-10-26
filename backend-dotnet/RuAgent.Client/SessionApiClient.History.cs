using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace RuAgent.Client;

public sealed partial class SessionApiClient
{
    public async Task<JsonElement> GetMessagePageAsync(
        string sessionId,
        string? cursor = null,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidSessionId(sessionId)
            || limit is < 1 or > 200
            || (cursor is not null && (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 2048 || cursor.Any(char.IsControl))))
            throw new AgentApiException("session_history_arguments_invalid", "Session history arguments are invalid.");

        var parameters = new List<string>(2);
        if (cursor is not null) parameters.Add("cursor=" + Uri.EscapeDataString(cursor));
        parameters.Add("limit=" + limit.ToString(CultureInfo.InvariantCulture));
        var path = $"sessions/{Uri.EscapeDataString(sessionId)}/messages?" + string.Join("&", parameters);
        return await GetHistoryJsonAsync(path, "session_history_page_failed", cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonElement> ListPageAsync(
        string? cursor = null,
        int limit = 100,
        string? query = null,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 200
            || (cursor is not null && (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 2048 || cursor.Any(char.IsControl)))
            || !IsValidHistoryQuery(query))
            throw new AgentApiException("session_history_arguments_invalid", "Session history arguments are invalid.");

        query = NormalizeHistoryQuery(query);
        var parameters = new List<string>(3);
        if (cursor is not null) parameters.Add("cursor=" + Uri.EscapeDataString(cursor));
        parameters.Add("limit=" + limit.ToString(CultureInfo.InvariantCulture));
        if (query is not null) parameters.Add("query=" + Uri.EscapeDataString(query));
        return await GetHistoryJsonAsync(
            "sessions/page?" + string.Join("&", parameters),
            "session_list_page_failed",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonElement> SearchMessagesAsync(
        string query,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidHistoryQuery(query, required: true) || limit is < 1 or > 200)
            throw new AgentApiException("session_search_arguments_invalid", "Session search arguments are invalid.");
        query = NormalizeHistoryQuery(query)!;
        var relativeUri = "sessions/search?query=" + Uri.EscapeDataString(query)
            + "&limit=" + limit.ToString(CultureInfo.InvariantCulture);
        return await GetHistoryJsonAsync(relativeUri, "session_search_failed", cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<JsonElement> GetHistoryJsonAsync(
        string relativeUri,
        string operation,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_session.BaseUri, relativeUri));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.BearerToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new AgentApiException("unauthorized", "Bearer rejected.", (int)response.StatusCode);

        var content = await BoundedHttpResponseReader.ReadAsync(response.Content, cancellationToken).ConfigureAwait(false);
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(content);
            root = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new AgentApiException("invalid_response", "Session history response was not valid JSON.",
                (int)response.StatusCode, inner: exception);
        }

        if (!response.IsSuccessStatusCode)
            throw ReadHistoryApiError(root, operation, (int)response.StatusCode);
        EnsureOkEnvelope(root, operation);
        return root;
    }

    private static AgentApiException ReadHistoryApiError(JsonElement root, string operation, int statusCode)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "code", "error_code" })
            {
                if (root.TryGetProperty(name, out var code) && code.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(code.GetString()))
                    return new AgentApiException(code.GetString()!, code.GetString()!, statusCode);
            }
        }
        return new AgentApiException(operation, $"HTTP {statusCode}", statusCode);
    }

    private static string? NormalizeHistoryQuery(string? query) =>
        string.IsNullOrWhiteSpace(query) ? null : query.Trim();

    private static bool IsValidSessionId(string? sessionId)
    {
        if (sessionId is not { Length: >= 1 and <= 64 }) return false;
        return sessionId.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    }

    private static bool IsValidHistoryQuery(string? query, bool required = false)
    {
        if (string.IsNullOrWhiteSpace(query)) return !required;
        var value = query.Trim();
        return value.Length <= 512 && !value.Any(char.IsControl);
    }
}
