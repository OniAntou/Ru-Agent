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
                using var response = await _httpClient.GetAsync($"{_baseUrl}/activity-log?limit={limit}");
                if (!response.IsSuccessStatusCode) return null;
                var result = await response.Content.ReadFromJsonAsync<JsonElement>();
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
                using var response = await _httpClient.DeleteAsync($"{_baseUrl}/activity-log");
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

        public async Task<(bool BackendAlive, bool WorkerOk, string? WorkerError)> CheckHealthDetailedAsync(CancellationToken cancellationToken = default)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                using var response = await _httpClient.GetAsync($"{_baseUrl}/status", timeoutCts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    string? statusError = await TryReadStatusErrorAsync(response, timeoutCts.Token);
                    return (BackendAlive: true, WorkerOk: false, WorkerError: statusError);
                }
                var payload = await response.Content.ReadFromJsonAsync<JsonElement>(timeoutCts.Token);
                if (payload.TryGetProperty("status", out var status) &&
                    status.GetString() == "error")
                {
                    string? message = payload.TryGetProperty("message", out var messageProp)
                        ? messageProp.GetString()
                        : null;
                    return (BackendAlive: true, WorkerOk: false, WorkerError: string.IsNullOrWhiteSpace(message) ? null : message);
                }
                return (BackendAlive: true, WorkerOk: true, WorkerError: null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogApiError("CheckHealthDetailedAsync", ex);
                return (BackendAlive: false, WorkerOk: false, WorkerError: null);
            }
        }

        public async Task<(bool Ok, string? Error)> CheckWorkerHealthAsync(CancellationToken cancellationToken = default)
        {
            var detailed = await CheckHealthDetailedAsync(cancellationToken);
            return (detailed.WorkerOk, detailed.WorkerError);
        }

        private static async Task<string?> TryReadStatusErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            try
            {
                string body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(body)) return null;
                using var document = JsonSerializer.Deserialize<JsonDocument>(body);
                if (document is not null
                    && document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    string? text = message.GetString();
                    return string.IsNullOrWhiteSpace(text) ? null : text;
                }
            }
            catch (Exception ex) when (ex is JsonException or OperationCanceledException)
            {
                // No usable error payload; caller falls back to a generic message.
            }
            return null;
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
