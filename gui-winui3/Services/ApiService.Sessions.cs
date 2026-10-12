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
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/sessions/new");
                request.Headers.Add("X-Ru-Client-Source", "gui");
                using var response = await _httpClient.SendAsync(request);
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
                    return new SessionInfo { Id = id, Title = title, ClientSource = "gui" };
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
                using var response = await _httpClient.PostAsync($"{_baseUrl}/sessions/{sessionId}/activate", null);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                LogApiError("ActivateSessionAsync", ex);
                return false;
            }
        }

        public async Task<List<(string Role, string Content)>?> GetSessionMessagesAsync(
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            try
            {
                using var response = await _httpClient.PostAsync(
                    $"{_baseUrl}/sessions/{sessionId}/activate",
                    null,
                    cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
                    if (result.TryGetProperty("session", out var sessionProp) &&
                        sessionProp.TryGetProperty("messages", out var messagesProp))
                    {
                        var list = new List<(string, string)>();
                        foreach (var item in messagesProp.EnumerateArray())
                        {
                            string role = item.TryGetProperty("role", out var roleProp) ? roleProp.GetString() ?? "" : "";
                            string content = item.TryGetProperty("content", out var contentProp) ? contentProp.GetString() ?? "" : "";
                            if (role == "user" || role == "ai" || role == "assistant")
                            {
                                list.Add((role == "user" ? "user" : "assistant", content));
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

        /// <summary>
        /// How a delete ended: whether the session file is gone, whether only leftover
        /// artifacts remain, and the host's own message when it reported a problem.
        /// </summary>
        public sealed record SessionDeletionOutcome(bool Deleted, bool Partial, string? Message);

        /// <summary>
        /// The host's word for the one partial state: the session file is already gone,
        /// its artifacts are not. The body carries no machine code for it, so this
        /// sentence is what tells that state apart from an undeleted session.
        /// </summary>
        private const string PartialCleanupMessage =
            "Session đã xoá nhưng artifacts còn sót, hãy thử xoá lại.";

        public async Task<SessionDeletionOutcome> DeleteSessionAsync(string sessionId)
        {
            try
            {
                using var response = await _httpClient.DeleteAsync($"{_baseUrl}/sessions/{sessionId}");
                if (!response.IsSuccessStatusCode)
                {
                    return new SessionDeletionOutcome(false, false, $"HTTP {(int)response.StatusCode}");
                }

                var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
                if (payload.ValueKind != JsonValueKind.Object)
                {
                    return new SessionDeletionOutcome(false, false, null);
                }

                var status = payload.TryGetProperty("status", out var statusProp)
                    && statusProp.ValueKind == JsonValueKind.String
                        ? statusProp.GetString()
                        : null;
                if (string.Equals(status, "ok", StringComparison.Ordinal))
                {
                    return new SessionDeletionOutcome(true, false, null);
                }

                var message = payload.TryGetProperty("message", out var messageProp)
                    && messageProp.ValueKind == JsonValueKind.String
                        ? messageProp.GetString()
                        : null;
                return new SessionDeletionOutcome(
                    Deleted: false,
                    Partial: string.Equals(message, PartialCleanupMessage, StringComparison.Ordinal),
                    Message: message);
            }
            catch (Exception ex)
            {
                LogApiError("DeleteSessionAsync", ex);
                return new SessionDeletionOutcome(false, false, null);
            }
        }

        public async Task<bool> RewindSessionAsync(string sessionId, int toMessageIndex)
        {
            try
            {
                var payload = new { to_message_index = toMessageIndex };
                using var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/sessions/{sessionId}/rewind", payload);
                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<JsonElement>();
                    return result.TryGetProperty("status", out var status) && status.GetString() == "ok";
                }
            }
            catch (Exception ex)
            {
                LogApiError("RewindSessionAsync", ex);
            }
            return false;
        }
    }
}
