using System;
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
        public async Task<VoiceStatusDto?> GetVoiceStatusAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var response = await _httpClient.GetAsync($"{_baseUrl}/voice/status", cancellationToken);
                if (!response.IsSuccessStatusCode) return null;
                return await response.Content.ReadFromJsonAsync<VoiceStatusDto>(cancellationToken);
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
                using var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/settings", payload);
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
                using var response = await _httpClient.PostAsync($"{_baseUrl}/voice/warmup", null);
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
                using var response = await _httpClient.PostAsync($"{_baseUrl}/voice/release", null);
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
                using var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/voice/tts", new { text });
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
                using var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/voice/tts", payload);
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
                using var response = await _httpClient.PostAsJsonAsync(
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
    }
}
