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

        public async Task<bool> ShutdownHostAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var response = await _httpClient.PostAsync($"{_baseUrl}/host/shutdown", null, cancellationToken);
                return response.IsSuccessStatusCode;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogApiError("ShutdownHostAsync", ex);
                return false;
            }
        }
    }
}
