using System;
using System.Collections.Generic;
using System.Linq;
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

        public async Task<SessionListPageResponse?> GetSessionsPageAsync(
            string? cursor = null,
            int limit = 100,
            string? query = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var parameters = new List<string> { $"limit={limit}" };
                if (!string.IsNullOrEmpty(cursor)) parameters.Add($"cursor={Uri.EscapeDataString(cursor)}");
                if (!string.IsNullOrWhiteSpace(query)) parameters.Add($"query={Uri.EscapeDataString(query.Trim())}");
                var url = $"{_baseUrl}/sessions/page?{string.Join("&", parameters)}";
                var response = await _httpClient.GetFromJsonAsync<SessionListPageResponse>(url, cancellationToken);
                return response?.Status == "ok" ? response : null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogApiError("GetSessionsPageAsync", ex);
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

        public async Task<bool> ActivateSessionAsync(
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            try
            {
                using var response = await _httpClient.PostAsync(
                    $"{_baseUrl}/sessions/{Uri.EscapeDataString(sessionId)}/activate",
                    null,
                    cancellationToken);
                return response.IsSuccessStatusCode;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
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
            var history = await GetSessionHistoryAsync(sessionId, cancellationToken);
            return history?.Select(row => (row.Role, row.Content)).ToList();
        }

        public async Task<List<SessionHistoryEntry>?> GetSessionHistoryAsync(
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            var page = await GetSessionHistoryPageAsync(sessionId, null, cancellationToken: cancellationToken);
            return page?.Messages.ToList();
        }

        public async Task<SessionHistoryPage?> GetSessionHistoryPageAsync(
            string sessionId,
            string? cursor = null,
            int limit = 200,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var url = $"{_baseUrl}/sessions/{Uri.EscapeDataString(sessionId)}/messages?limit={limit}";
                if (!string.IsNullOrEmpty(cursor)) url += $"&cursor={Uri.EscapeDataString(cursor)}";
                var response = await _httpClient.GetFromJsonAsync<SessionMessagesPageResponse>(url, cancellationToken);
                if (response is { Status: "ok" }
                    && string.Equals(response.SessionId, sessionId, StringComparison.Ordinal))
                {
                    var entries = new List<SessionHistoryEntry>(response.Messages.Count);
                    for (int index = 0; index < response.Messages.Count; index++)
                    {
                        var item = response.Messages[index];
                        if (item.Role is not ("user" or "ai" or "assistant")) continue;
                        entries.Add(new SessionHistoryEntry(
                            item.Role == "user" ? "user" : "assistant",
                            item.Content,
                            item.SourceRunId,
                            checked(response.StartIndex + index),
                            item.SourceMessageId));
                    }

                    return new SessionHistoryPage(
                        response.SessionId,
                        entries,
                        response.StartIndex,
                        response.TotalMessages,
                        response.NextCursor,
                        response.HistoryEpoch,
                        response.Messages.Count);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogApiError("GetSessionHistoryPageAsync", ex);
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
