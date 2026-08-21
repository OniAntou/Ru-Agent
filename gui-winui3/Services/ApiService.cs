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
        private readonly string _authSecret;
        private readonly int _port;

        public ApiService(string authSecret, int port = 8000)
        {
            _authSecret = authSecret;
            _port = port;
            _baseUrl = $"http://127.0.0.1:{port}";
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authSecret);
        }

        public VoiceStreamClient CreateVoiceStreamClient()
        {
            return new VoiceStreamClient(_authSecret, _port);
        }

        public async Task<AppSettingsDto?> GetSettingsAsync()
        {
            try
            {
                return await _httpClient.GetFromJsonAsync<AppSettingsDto>($"{_baseUrl}/settings");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ApiService] GetSettingsAsync failed: {ex}");
                return null;
            }
        }

        public async Task<bool> SaveSettingsAsync(AppSettingsDto settings)
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/settings", settings);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ApiService] SaveSettingsAsync failed: {ex.Message}");
                return false;
            }
        }

        public async Task<SessionListResponse?> GetSessionsAsync()
        {
            try
            {
                return await _httpClient.GetFromJsonAsync<SessionListResponse>($"{_baseUrl}/sessions");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ApiService] GetSessionsAsync failed: {ex.Message}");
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
                Debug.WriteLine($"[ApiService] CreateSessionAsync failed: {ex.Message}");
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
                Debug.WriteLine($"[ApiService] ActivateSessionAsync failed: {ex.Message}");
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
                Debug.WriteLine($"[ApiService] GetSessionMessagesAsync failed: {ex.Message}");
            }
            return null;
        }

        public async Task<bool> DeleteSessionAsync(string sessionId)
        {
            try
            {
                var response = await _httpClient.DeleteAsync($"{_baseUrl}/sessions/{sessionId}");
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ApiService] DeleteSessionAsync failed: {ex.Message}");
                return false;
            }
        }

        public async Task<VoiceStatusDto?> GetVoiceStatusAsync()
        {
            try
            {
                return await _httpClient.GetFromJsonAsync<VoiceStatusDto>($"{_baseUrl}/voice/status");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ApiService] GetVoiceStatusAsync failed: {ex.Message}");
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
                Debug.WriteLine($"[ApiService] UpdateVoiceAsync failed: {ex.Message}");
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
                Debug.WriteLine($"[ApiService] WarmupVoiceAsync failed: {ex.Message}");
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
                Debug.WriteLine(
                    $"[ApiService] ReportVoiceLatencyAsync failed: {ex.GetType().Name}: {ex.Message}");
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
                Debug.WriteLine($"[ApiService] ReleaseVoiceAsync failed: {ex.Message}");
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
                Debug.WriteLine($"[ApiService] TestVoiceAsync failed: {ex.Message}");
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
                Debug.WriteLine($"[ApiService] SynthesizeTtsAsync failed: {ex.Message}");
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
                Debug.WriteLine($"[ApiService] SetVtsSpeakingAsync failed: {ex.Message}");
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
                Debug.WriteLine($"[ApiService] GetVtsConfigAsync failed: {ex.Message}");
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
                Debug.WriteLine($"[ApiService] SaveVtsConfigAsync failed: {ex.Message}");
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
                Debug.WriteLine($"[ApiService] PushVtsActivityAsync failed: {ex.Message}");
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
                Debug.WriteLine($"[ApiService] GetMouseLookAsync failed: {ex.Message}");
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
                Debug.WriteLine($"[ApiService] SaveMouseLookAsync failed: {ex.Message}");
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
                Debug.WriteLine($"[ApiService] GetOverlayAsync failed: {ex.Message}");
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
                Debug.WriteLine($"[ApiService] SaveOverlayAsync failed: {ex.Message}");
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
                Debug.WriteLine($"[ApiService] GetActivityLogAsync failed: {ex.Message}");
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
                Debug.WriteLine($"[ApiService] ClearActivityLogAsync failed: {ex.Message}");
                return false;
            }
        }

        public async Task<List<string>> GetModelsAsync(string backend)
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/models?backend={Uri.EscapeDataString(backend)}");
                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<JsonElement>();
                    if (result.TryGetProperty("status", out var statusProp) &&
                        statusProp.GetString() == "ok" &&
                        result.TryGetProperty("models", out var modelsProp))
                    {
                        var list = new List<string>();
                        foreach (var item in modelsProp.EnumerateArray())
                        {
                            list.Add(item.GetString() ?? "");
                        }
                        return list;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ApiService] GetModelsAsync failed: {ex.Message}");
            }
            return new List<string>();
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
                Debug.WriteLine($"[ApiService] TranscribeAudioAsync failed: {ex.Message}");
            }
            return null;
        }

public async Task<(bool Ok, string? Error)> CheckWorkerHealthAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/status");
                if (!response.IsSuccessStatusCode)
                {
                    return (false, null);
                }
                var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
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
            catch (Exception ex)
            {
                Debug.WriteLine($"[ApiService] CheckWorkerHealthAsync failed: {ex.Message}");
                return (false, null);
            }
        }

        public async Task<VtsStatusDto?> GetVtsStatusAsync()
        {
            try
            {
                return await _httpClient.GetFromJsonAsync<VtsStatusDto>($"{_baseUrl}/vts/status");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ApiService] GetVtsStatusAsync failed: {ex.Message}");
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
                Debug.WriteLine($"[ApiService] ConnectVtsAsync failed: {ex.Message}");
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
                Debug.WriteLine($"[ApiService] GetMemoryAsync failed: {ex.Message}");
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
                Debug.WriteLine($"[ApiService] ClearMemoryAsync failed: {ex.Message}");
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
                Debug.WriteLine($"[ApiService] GetObservationStatusAsync failed: {ex.Message}");
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
                Debug.WriteLine($"[ApiService] StartObservationAsync failed: {ex.Message}");
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
                Debug.WriteLine($"[ApiService] StopObservationAsync failed: {ex.Message}");
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
                Debug.WriteLine($"[ApiService] AcknowledgeObservationAsync failed: {ex.Message}");
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
            string? cursor = null)
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
                return await _httpClient.GetFromJsonAsync<DiagnosticLogResultDto>(url);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ApiService] GetDiagnosticLogsAsync failed: {ex.Message}");
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
                Debug.WriteLine($"[ApiService] ExportDiagnosticLogsAsync failed: {ex.Message}");
                return null;
            }
        }

        public async Task<DiagnosticStatusDto?> GetDiagnosticStatusAsync()
        {
            try
            {
                return await _httpClient.GetFromJsonAsync<DiagnosticStatusDto>($"{_baseUrl}/diagnostics/status");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ApiService] GetDiagnosticStatusAsync failed: {ex.Message}");
                return null;
            }
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }
    }
}
