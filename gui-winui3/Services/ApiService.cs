using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RuApp.Models;

namespace RuApp.Services
{
    public class ApiService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        private readonly BackendSession _session;

        public ApiService(string authSecret, int port = 8000)
            : this(BackendSession.FromLoopback(port, authSecret))
        {
        }

        public ApiService(BackendSession session)
            : this(session, handler: null)
        {
        }

        internal ApiService(BackendSession session, HttpMessageHandler? handler)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _baseUrl = session.HttpBaseUri.ToString().TrimEnd('/');
            _httpClient = handler == null ? new HttpClient() : new HttpClient(handler);
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AuthSecret);
        }

        public event EventHandler? SettingsSaved;

        private static void LogApiError(string method, Exception ex)
        {
            var status = ex is HttpRequestException httpEx && httpEx.StatusCode.HasValue
                ? $" status={(int)httpEx.StatusCode.Value} "
                : string.Empty;
            Debug.WriteLine(
                $"[ApiService] {method} failed: {ex.GetType().Name}{status}: {ex.Message}");
            if (ex.InnerException != null)
            {
                Debug.WriteLine($"[ApiService] {method} inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            }
        }

        private static bool IsCancellation(Exception ex) =>
            ex is OperationCanceledException || ex is TaskCanceledException;

        public VoiceStreamClient CreateVoiceStreamClient()
        {
            return new VoiceStreamClient(_session);
        }

        public async Task<AppSettingsDto?> GetSettingsAsync()
        {
            try
            {
                return await _httpClient.GetFromJsonAsync<AppSettingsDto>($"{_baseUrl}/settings");
            }
            catch (Exception ex)
            {
                LogApiError("GetSettingsAsync", ex);
                return null;
            }
        }

        public async Task<WorkspaceConcurrencyResponseDto?> GetWorkspaceConcurrencyAsync(string workspaceId)
        {
            try
            {
                return await _httpClient.GetFromJsonAsync<WorkspaceConcurrencyResponseDto>(
                    $"{_baseUrl}/agent/v1/workspaces/{Uri.EscapeDataString(workspaceId)}/concurrency");
            }
            catch (Exception ex)
            {
                LogApiError("GetWorkspaceConcurrencyAsync", ex);
                return null;
            }
        }

        public async Task<WorkspaceActivityResponseDto?> GetWorkspaceActivityAsync(string workspaceId, long afterSequence = 0, int limit = 50)
        {
            try
            {
                return await _httpClient.GetFromJsonAsync<WorkspaceActivityResponseDto>(
                    $"{_baseUrl}/agent/v1/workspaces/{Uri.EscapeDataString(workspaceId)}/activity?after_sequence={afterSequence}&limit={limit}");
            }
            catch (Exception ex)
            {
                LogApiError("GetWorkspaceActivityAsync", ex);
                return null;
            }
        }

        public async Task<WorkspaceChangesResponseDto?> GetWorkspaceChangesAsync(string workspaceId, long afterSequence = 0, int limit = 50)
        {
            try
            {
                return await _httpClient.GetFromJsonAsync<WorkspaceChangesResponseDto>(
                    $"{_baseUrl}/agent/v1/workspaces/{Uri.EscapeDataString(workspaceId)}/changes?after_sequence={afterSequence}&limit={limit}");
            }
            catch (Exception ex)
            {
                LogApiError("GetWorkspaceChangesAsync", ex);
                return null;
            }
        }

        public async Task<bool> SetWorkspaceConcurrencyAsync(string workspaceId, WorkspaceConcurrencyPolicyDto policy)
        {
            try
            {
                using var response = await _httpClient.PutAsJsonAsync(
                    $"{_baseUrl}/agent/v1/workspaces/{Uri.EscapeDataString(workspaceId)}/concurrency",
                    policy);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                LogApiError("SetWorkspaceConcurrencyAsync", ex);
                return false;
            }
        }

        public async Task<WorkspaceChangeRestoreResponseDto?> RestoreWorkspaceChangeAsync(
            string workspaceId,
            string changeId,
            string expectedCurrentSha256)
        {
            try
            {
                using var response = await _httpClient.PostAsJsonAsync(
                    $"{_baseUrl}/agent/v1/workspaces/{Uri.EscapeDataString(workspaceId)}/changes/{Uri.EscapeDataString(changeId)}/restore",
                    new { expected_current_sha256 = expectedCurrentSha256 });
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                return await response.Content.ReadFromJsonAsync<WorkspaceChangeRestoreResponseDto>();
            }
            catch (Exception ex)
            {
                LogApiError("RestoreWorkspaceChangeAsync", ex);
                return null;
            }
        }

        public async Task<bool> SaveSettingsAsync(AppSettingsDto settings)
        {
            try
            {
                using var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/settings", settings);
                bool ok = await HasOkEnvelopeAsync(response);
                if (ok) SettingsSaved?.Invoke(this, EventArgs.Empty);
                return ok;
            }
            catch (Exception ex)
            {
                LogApiError("SaveSettingsAsync", ex);
                return false;
            }
        }

        public async Task<bool> SaveProviderSettingsAsync(ProviderSettingsUpdateDto settings)
        {
            try
            {
                using var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/settings", settings);
                bool ok = await HasOkEnvelopeAsync(response);
                if (ok) SettingsSaved?.Invoke(this, EventArgs.Empty);
                return ok;
            }
            catch (Exception ex)
            {
                LogApiError("SaveProviderSettingsAsync", ex);
                return false;
            }
        }

        public async Task<bool> SaveReasoningSettingsAsync(
            string effort,
            string model,
            string identity)
        {
            try
            {
                var payload = new ReasoningSettingsUpdateDto
                {
                    ReasoningEffort = string.IsNullOrWhiteSpace(effort) ? "default" : effort,
                    ReasoningModel = model,
                    ReasoningIdentity = identity,
                };
                using var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/settings", payload);
                bool ok = await HasOkEnvelopeAsync(response);
                if (ok) SettingsSaved?.Invoke(this, EventArgs.Empty);
                return ok;
            }
            catch (Exception ex)
            {
                LogApiError("SaveReasoningSettingsAsync", ex);
                return false;
            }
        }

        public async Task<bool> SaveProviderModelSelectionAsync(string backend, string model)
        {
            var payload = new ProviderModelSelectionUpdateDto
            {
                BackendChoice = backend,
                SelectedModel = model,
            };

            try
            {
                using var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/settings", payload);
                bool ok = await HasOkEnvelopeAsync(response);
                if (ok) SettingsSaved?.Invoke(this, EventArgs.Empty);
                return ok;
            }
            catch (Exception ex)
            {
                LogApiError("SaveProviderModelSelectionAsync", ex);
                return false;
            }
        }

        private static async Task<bool> HasOkEnvelopeAsync(HttpResponseMessage response)
        {
            if (!response.IsSuccessStatusCode) return false;
            string body = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(body)) return false;

            try
            {
                using var document = JsonDocument.Parse(body);
                return document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("status", out var status) &&
                    status.ValueKind == JsonValueKind.String &&
                    (string.Equals(status.GetString(), "ok", StringComparison.Ordinal) ||
                     string.Equals(status.GetString(), "success", StringComparison.Ordinal));
            }
            catch (JsonException)
            {
                return false;
            }
        }

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

        public async Task<VoiceStatusDto?> GetVoiceStatusAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                return await _httpClient.GetFromJsonAsync<VoiceStatusDto>($"{_baseUrl}/voice/status", cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogApiError("GetVoiceStatusAsync", ex);
                return null;
            }
        }

        public async Task<bool> UpdateVoiceAsync(
            string voiceLabel,
            double voiceSpeed,
            bool autoPace,
            bool autoRead,
            bool showSubtitles,
            string voiceStyleId,
            string subtitleColor = "#FFFFFF",
            string subtitleSize = "medium",
            double subtitleDuration = 2.0,
            string subtitlePosMode = "auto",
            int subtitlePosX = -1,
            int subtitlePosY = -1)
        {
            try
            {
                var payload = new VoiceSettingsUpdateDto
                {
                    VoiceLabel = voiceLabel,
                    VoiceSpeed = voiceSpeed,
                    AutoPaceEnabled = autoPace,
                    AutoReadReplies = autoRead,
                    ShowSubtitles = showSubtitles,
                    VoiceStyleId = voiceStyleId,
                    SubtitleColor = subtitleColor,
                    SubtitleSize = subtitleSize,
                    SubtitleDuration = subtitleDuration,
                    SubtitlePosMode = subtitlePosMode,
                    SubtitlePosX = subtitlePosX,
                    SubtitlePosY = subtitlePosY,
                };
                var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/settings", payload);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                LogApiError("UpdateVoiceAsync", ex);
                return false;
            }
        }

        public async Task<bool> WarmupVoiceAsync()
        {
            try
            {
                var response = await _httpClient.PostAsync($"{_baseUrl}/voice/warmup", null);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                LogApiError("WarmupVoiceAsync", ex);
                return false;
            }
        }

        public async Task<bool> ReportVoiceLatencyAsync(VoiceReplyLatencySnapshot snapshot)
        {
            try
            {
                using var response = await _httpClient.PostAsJsonAsync(
                    $"{_baseUrl}/voice/latency",
                    snapshot.ToWirePayload());
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                LogApiError("ReportVoiceLatencyAsync", ex);
                return false;
            }
        }

        public async Task<bool> ReleaseVoiceAsync()
        {
            try
            {
                var response = await _httpClient.PostAsync($"{_baseUrl}/voice/release", null);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                LogApiError("ReleaseVoiceAsync", ex);
                return false;
            }
        }

        public async Task<bool> TestVoiceAsync(string text)
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/voice/tts", new { text });
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                LogApiError("TestVoiceAsync", ex);
                return false;
            }
        }

        public async Task<byte[]?> SynthesizeTtsAsync(string text, string voiceLabel, double speed)
        {
            try
            {
                var payload = new
                {
                    text,
                    voice_label = voiceLabel,
                    speed
                };
                var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/voice/tts", payload);
                if (response.IsSuccessStatusCode &&
                    response.Content.Headers.ContentType?.MediaType == "audio/wav")
                {
                    return await response.Content.ReadAsByteArrayAsync();
                }
            }
            catch (Exception ex)
            {
                LogApiError("SynthesizeTtsAsync", ex);
            }
            return null;
        }

        public async Task<bool> SetVtsSpeakingAsync(bool speaking)
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/vts/speaking", new { speaking });
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                LogApiError("SetVtsSpeakingAsync", ex);
                return false;
            }
        }

        public async Task<VtsConfigDto?> GetVtsConfigAsync()
        {
            try
            {
                var result = await _httpClient.GetFromJsonAsync<VtsConfigResponse>($"{_baseUrl}/vts/config");
                return result?.Config;
            }
            catch (Exception ex)
            {
                LogApiError("GetVtsConfigAsync", ex);
                return null;
            }
        }

        public async Task<bool> SaveVtsConfigAsync(VtsConfigDto config)
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/vts/config", config);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                LogApiError("SaveVtsConfigAsync", ex);
                return false;
            }
        }

        public async Task PushVtsActivityAsync(bool typing)
        {
            try
            {
                await _httpClient.PostAsJsonAsync($"{_baseUrl}/vts/activity", new { typing });
            }
            catch (Exception ex)
            {
                LogApiError("PushVtsActivityAsync", ex);
            }
        }

        public async Task<MouseLookDto?> GetMouseLookAsync()
        {
            try
            {
                var result = await _httpClient.GetFromJsonAsync<JsonElement>($"{_baseUrl}/vts/mouse-look");
                if (result.TryGetProperty("mouse_look", out var ml))
                    return ml.Deserialize<MouseLookDto>();
                return null;
            }
            catch (Exception ex)
            {
                LogApiError("GetMouseLookAsync", ex);
                return null;
            }
        }

        public async Task<bool> SaveMouseLookAsync(bool enabled, double sensitivity)
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/vts/mouse-look", new { enabled, sensitivity });
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                LogApiError("SaveMouseLookAsync", ex);
                return false;
            }
        }

        public async Task<OverlayDto?> GetOverlayAsync()
        {
            try
            {
                var result = await _httpClient.GetFromJsonAsync<JsonElement>($"{_baseUrl}/vts/overlay");
                if (result.TryGetProperty("overlay", out var o))
                    return o.Deserialize<OverlayDto>();
                return null;
            }
            catch (Exception ex)
            {
                LogApiError("GetOverlayAsync", ex);
                return null;
            }
        }

        public async Task<bool> SaveOverlayAsync(bool enabled)
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/vts/overlay", new { enabled });
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                LogApiError("SaveOverlayAsync", ex);
                return false;
            }
        }

        public async Task<List<ActivityLogEventDto>?> GetActivityLogAsync(int limit = 200)
        {
            try
            {
                var result = await _httpClient.GetFromJsonAsync<JsonElement>($"{_baseUrl}/activity-log?limit={limit}");
                if (result.TryGetProperty("events", out var events))
                    return events.Deserialize<List<ActivityLogEventDto>>();
                return null;
            }
            catch (Exception ex)
            {
                LogApiError("GetActivityLogAsync", ex);
                return null;
            }
        }

        public async Task<bool> ClearActivityLogAsync()
        {
            try
            {
                var response = await _httpClient.DeleteAsync($"{_baseUrl}/activity-log");
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                LogApiError("ClearActivityLogAsync", ex);
                return false;
            }
        }

        public async Task<List<string>?> GetModelsAsync(string backend)
        {
            var catalog = await GetProviderCatalogAsync(backend, refresh: false, CancellationToken.None);
            return catalog?.IsSuccess == true ? new List<string>(catalog.Models) : null;
        }

        public async Task<ProviderCatalogSnapshot?> GetProviderCatalogAsync(
            string backend,
            bool refresh = false,
            CancellationToken cancellationToken = default)
        {
            try
            {
                string query = $"backend={Uri.EscapeDataString(backend)}" + (refresh ? "&refresh=true" : string.Empty);
                using var response = await _httpClient.GetAsync($"{_baseUrl}/models?{query}", cancellationToken);
                if (!response.IsSuccessStatusCode) return null;

                string body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(body)) return null;
                var snapshot = JsonSerializer.Deserialize<ProviderCatalogSnapshot>(body);
                return snapshot;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogApiError("GetProviderCatalogAsync", ex);
                return null;
            }
        }

        public async Task<string?> TranscribeAudioAsync(byte[] wavBytes, CancellationToken cancellationToken = default)
        {
            try
            {
                string audioB64 = Convert.ToBase64String(wavBytes);
                var payload = new
                {
                    audio_b64 = audioB64,
                    sample_rate = 16000
                };
                var response = await _httpClient.PostAsJsonAsync(
                    $"{_baseUrl}/voice/transcribe",
                    payload,
                    cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
                    if (result.TryGetProperty("text", out var textProp))
                    {
                        return textProp.GetString();
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogApiError("TranscribeAudioAsync", ex);
            }
            return null;
        }

        public async Task<(bool Ok, string? Error)> CheckWorkerHealthAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/status", cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return (false, null);
                }
                var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
                if (payload.TryGetProperty("status", out var status) &&
                    status.GetString() == "error")
                {
                    string? message = payload.TryGetProperty("message", out var messageProp)
                        ? messageProp.GetString()
                        : null;
                    return (false, string.IsNullOrWhiteSpace(message) ? null : message);
                }
                return (true, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogApiError("CheckWorkerHealthAsync", ex);
                return (false, null);
            }
        }

        public async Task<VtsStatusDto?> GetVtsStatusAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                return await _httpClient.GetFromJsonAsync<VtsStatusDto>($"{_baseUrl}/vts/status", cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogApiError("GetVtsStatusAsync", ex);
                return null;
            }
        }

        public async Task<bool> ConnectVtsAsync()
        {
            try
            {
                var response = await _httpClient.PostAsync($"{_baseUrl}/vts/connect", null);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                LogApiError("ConnectVtsAsync", ex);
                return false;
            }
        }

        public async Task<List<MemoryItemDto>?> GetMemoryAsync()
        {
            try
            {
                return await _httpClient.GetFromJsonAsync<List<MemoryItemDto>>($"{_baseUrl}/memory");
            }
            catch (Exception ex)
            {
                LogApiError("GetMemoryAsync", ex);
                return null;
            }
        }

        public async Task<bool> ClearMemoryAsync()
        {
            try
            {
                var response = await _httpClient.DeleteAsync($"{_baseUrl}/memory");
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                LogApiError("ClearMemoryAsync", ex);
                return false;
            }
        }

        public async Task<ObservationStatusDto?> GetObservationStatusAsync()
        {
            try
            {
                return await _httpClient.GetFromJsonAsync<ObservationStatusDto>($"{_baseUrl}/observation/status");
            }
            catch (Exception ex)
            {
                LogApiError("GetObservationStatusAsync", ex);
                return null;
            }
        }

        public async Task<bool> StartObservationAsync(double intervalSeconds = 15.0)
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/observation/start", new { interval_seconds = intervalSeconds });
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                LogApiError("StartObservationAsync", ex);
                return false;
            }
        }

        public async Task<bool> StopObservationAsync()
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/observation/stop", new { });
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                LogApiError("StopObservationAsync", ex);
                return false;
            }
        }

        public async Task<bool> AcknowledgeObservationAsync(string observationId, bool played)
        {
            if (string.IsNullOrWhiteSpace(observationId)) return false;
            try
            {
                var response = await _httpClient.PostAsJsonAsync(
                    $"{_baseUrl}/observation/ack",
                    new { observation_id = observationId, played });
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                LogApiError("AcknowledgeObservationAsync", ex);
                return false;
            }
        }

        public async Task<DiagnosticLogResultDto?> GetDiagnosticLogsAsync(
            int? limit = null,
            string? since = null,
            string? level = null,
            string? component = null,
            string? eventCode = null,
            string? correlationId = null,
            string? cursor = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var queryParams = new List<string>();
                if (limit.HasValue) queryParams.Add($"limit={limit.Value}");
                if (!string.IsNullOrWhiteSpace(since)) queryParams.Add($"since={Uri.EscapeDataString(since)}");
                if (!string.IsNullOrWhiteSpace(level)) queryParams.Add($"level={Uri.EscapeDataString(level)}");
                if (!string.IsNullOrWhiteSpace(component)) queryParams.Add($"component={Uri.EscapeDataString(component)}");
                if (!string.IsNullOrWhiteSpace(eventCode)) queryParams.Add($"event_code={Uri.EscapeDataString(eventCode)}");
                if (!string.IsNullOrWhiteSpace(correlationId)) queryParams.Add($"correlation_id={Uri.EscapeDataString(correlationId)}");
                if (!string.IsNullOrWhiteSpace(cursor)) queryParams.Add($"cursor={Uri.EscapeDataString(cursor)}");

                var url = $"{_baseUrl}/diagnostics/logs" + (queryParams.Count > 0 ? "?" + string.Join("&", queryParams) : "");
                return await _httpClient.GetFromJsonAsync<DiagnosticLogResultDto>(url, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogApiError("GetDiagnosticLogsAsync", ex);
                return null;
            }
        }

        public async Task<string?> ExportDiagnosticLogsAsync(
            string? since = null,
            string? level = null,
            string? component = null,
            string? eventCode = null,
            string? correlationId = null)
        {
            try
            {
                var queryParams = new List<string>();
                if (!string.IsNullOrWhiteSpace(since)) queryParams.Add($"since={Uri.EscapeDataString(since)}");
                if (!string.IsNullOrWhiteSpace(level)) queryParams.Add($"level={Uri.EscapeDataString(level)}");
                if (!string.IsNullOrWhiteSpace(component)) queryParams.Add($"component={Uri.EscapeDataString(component)}");
                if (!string.IsNullOrWhiteSpace(eventCode)) queryParams.Add($"event_code={Uri.EscapeDataString(eventCode)}");
                if (!string.IsNullOrWhiteSpace(correlationId)) queryParams.Add($"correlation_id={Uri.EscapeDataString(correlationId)}");

                var url = $"{_baseUrl}/diagnostics/export" + (queryParams.Count > 0 ? "?" + string.Join("&", queryParams) : "");
                return await _httpClient.GetStringAsync(url);
            }
            catch (Exception ex)
            {
                LogApiError("ExportDiagnosticLogsAsync", ex);
                return null;
            }
        }

        public async Task<DiagnosticStatusDto?> GetDiagnosticStatusAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                return await _httpClient.GetFromJsonAsync<DiagnosticStatusDto>($"{_baseUrl}/diagnostics/status", cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogApiError("GetDiagnosticStatusAsync", ex);
                return null;
            }
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }
    }
}
