using System;
using System.Net.Http.Json;
using System.Threading.Tasks;
using RuApp.Models;

namespace RuApp.Services
{
    public partial class ApiService
    {
        public async Task<ObservationStatusDto?> GetObservationStatusAsync(CancellationToken cancellationToken = default)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                return await _httpClient.GetFromJsonAsync<ObservationStatusDto>($"{_baseUrl}/observation/status", timeoutCts.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
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
    }
}
