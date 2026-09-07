using System;
using System.Collections.Generic;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using RuApp.Models;

namespace RuApp.Services
{
    public partial class ApiService
    {
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
    }
}
