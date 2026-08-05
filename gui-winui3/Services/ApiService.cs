using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RuApp.Models;

namespace RuApp.Services
{
    public class ApiService
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;

        public ApiService(string authSecret, int port = 8000)
        {
            _baseUrl = $"http://127.0.0.1:{port}";
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authSecret);
        }

        public async Task<AppSettingsDto?> GetSettingsAsync()
        {
            try
            {
                return await _httpClient.GetFromJsonAsync<AppSettingsDto>($"{_baseUrl}/settings");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ApiService] GetSettingsAsync failed: {ex.Message}");
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

        public async Task<bool> UpdateVoiceAsync(string voiceLabel, double voiceSpeed, bool autoPace, bool autoRead)
        {
            try
            {
                var payload = new
                {
                    voice_label = voiceLabel,
                    voice_speed = voiceSpeed,
                    auto_pace_enabled = autoPace,
                    auto_read_replies = autoRead
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

        public async Task<string?> TranscribeAudioAsync(byte[] wavBytes)
        {
            try
            {
                string audioB64 = Convert.ToBase64String(wavBytes);
                var payload = new
                {
                    audio_b64 = audioB64,
                    sample_rate = 16000
                };
                var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/voice/transcribe", payload);
                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<JsonElement>();
                    if (result.TryGetProperty("text", out var textProp))
                    {
                        return textProp.GetString();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ApiService] TranscribeAudioAsync failed: {ex.Message}");
            }
            return null;
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

        public async Task<bool> TriggerVtsActionAsync(string action)
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/vts/action", new { action });
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ApiService] TriggerVtsActionAsync failed: {ex.Message}");
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

        public async Task<ObservationMetricsDto?> GetObservationMetricsAsync()
        {
            try
            {
                return await _httpClient.GetFromJsonAsync<ObservationMetricsDto>($"{_baseUrl}/observation/status");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ApiService] GetObservationMetricsAsync failed: {ex.Message}");
                return null;
            }
        }

        public async Task<bool> StartObservationAsync()
        {
            try
            {
                var response = await _httpClient.PostAsync($"{_baseUrl}/observation/start", null);
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
                var response = await _httpClient.PostAsync($"{_baseUrl}/observation/stop", null);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ApiService] StopObservationAsync failed: {ex.Message}");
                return false;
            }
        }
    }
}
