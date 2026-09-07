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
    }
}
