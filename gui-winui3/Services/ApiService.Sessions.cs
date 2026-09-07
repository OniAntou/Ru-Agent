using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RuApp.Models;

namespace RuApp.Services
{
    public partial class ApiService
    {
        public async Task<SessionListResponse?> GetSessionsAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                return await _httpClient.GetFromJsonAsync<SessionListResponse>($"{_baseUrl}/sessions", cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogApiError("GetSessionsAsync", ex);
                return null;
            }
        }

        public async Task<SessionInfo?> CreateSessionAsync()
        {
            try
            {
                var response = await _httpClient.PostAsync($"{_baseUrl}/sessions/new", null);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var json = await response.Content.ReadFromJsonAsync<JsonElement>();
                if (json.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                if (json.TryGetProperty("session", out var sessionObj) && sessionObj.ValueKind == JsonValueKind.Object)
                {
                    var id = sessionObj.GetProperty("id").GetString() ?? string.Empty;
                    var title = sessionObj.TryGetProperty("title", out var titleProp)
                        ? titleProp.GetString() ?? string.Empty
                        : string.Empty;
                    return new SessionInfo { Id = id, Title = title };
                }

                if (json.TryGetProperty("session_id", out var sessionIdProp))
                {
                    var id = sessionIdProp.GetString() ?? string.Empty;
                    return new SessionInfo { Id = id, Title = string.Empty };
                }
            }
            catch (Exception ex)
            {
                LogApiError("CreateSessionAsync", ex);
            }
            return null;
        }

        public async Task<bool> ActivateSessionAsync(string sessionId)
        {
            try
            {
                var response = await _httpClient.PostAsync($"{_baseUrl}/sessions/{sessionId}/activate", null);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                LogApiError("ActivateSessionAsync", ex);
                return false;
            }
        }

        public async Task<List<(string Role, string Content)>?> GetSessionMessagesAsync(string sessionId)
        {
            try
            {
                var response = await _httpClient.PostAsync($"{_baseUrl}/sessions/{sessionId}/activate", null);
                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<JsonElement>();
                    if (result.TryGetProperty("session", out var sessionProp) &&
                        sessionProp.TryGetProperty("messages", out var messagesProp))
                    {
                        var list = new List<(string, string)>();
                        foreach (var item in messagesProp.EnumerateArray())
                        {
                            string role = item.TryGetProperty("role", out var roleProp) ? roleProp.GetString() ?? "" : "";
                            string content = item.TryGetProperty("content", out var contentProp) ? contentProp.GetString() ?? "" : "";
                            if (role == "user" || role == "ai")
                            {
                                list.Add((role == "ai" ? "assistant" : "user", content));
                            }
                        }
                        return list;
                    }
                }
            }
            catch (Exception ex)
            {
                LogApiError("GetSessionMessagesAsync", ex);
            }
            return null;
        }

        public async Task<bool> DeleteSessionAsync(string sessionId)
        {
            try
            {
                using var response = await _httpClient.DeleteAsync($"{_baseUrl}/sessions/{sessionId}");
                if (!response.IsSuccessStatusCode)
                {
                    return false;
                }

                var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
                return payload.ValueKind == JsonValueKind.Object
                    && payload.TryGetProperty("status", out var status)
                    && status.ValueKind == JsonValueKind.String
                    && string.Equals(status.GetString(), "ok", StringComparison.Ordinal);
            }
            catch (Exception ex)
            {
                LogApiError("DeleteSessionAsync", ex);
                return false;
            }
        }
    }
}
