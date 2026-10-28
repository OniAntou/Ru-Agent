using System;
using System.Collections.Generic;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RuApp.Models;

namespace RuApp.Services
{
    /// <summary>Named custom providers (ADR 0034) over the existing settings and models routes.</summary>
    public partial class ApiService
    {
        /// <summary>
        /// Replaces the whole provider list, optionally storing one provider's key in the
        /// same save. The Host validates everything and refuses the save as a whole.
        /// </summary>
        public async Task<CustomProviderSaveResult> SaveCustomProvidersAsync(
            IReadOnlyList<CustomProviderDto> providers,
            string? keyProviderId,
            string? apiKey)
        {
            try
            {
                var payload = new JsonObject
                {
                    ["custom_providers"] = JsonSerializer.SerializeToNode(providers),
                };
                if (!string.IsNullOrEmpty(keyProviderId) && !string.IsNullOrEmpty(apiKey))
                {
                    payload["custom_provider_id"] = keyProviderId;
                    payload["custom_provider_api_key"] = apiKey;
                }

                using var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/settings", payload);
                if (!response.IsSuccessStatusCode) return new(false, null, "The Host refused the request.");
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var root = document.RootElement;
                var success = root.TryGetProperty("status", out var status)
                    && status.ValueKind == JsonValueKind.String
                    && status.GetString() is "success" or "ok";
                var code = root.TryGetProperty("code", out var codeValue) && codeValue.ValueKind == JsonValueKind.String
                    ? codeValue.GetString() : null;
                var message = root.TryGetProperty("message", out var messageValue) && messageValue.ValueKind == JsonValueKind.String
                    ? messageValue.GetString() ?? string.Empty : string.Empty;
                if (success) SettingsSaved?.Invoke(this, EventArgs.Empty);
                return new(success, code, message);
            }
            catch (Exception ex)
            {
                LogApiError("SaveCustomProvidersAsync", ex);
                return new(false, null, "Could not reach the Host.");
            }
        }

        /// <summary>Asks a saved custom provider which models it serves; nothing is stored.</summary>
        public async Task<ProviderCatalogSnapshot?> DiscoverCustomModelsAsync(string backend, CancellationToken cancellationToken = default)
        {
            try
            {
                using var response = await _httpClient.GetAsync(
                    $"{_baseUrl}/models?backend={Uri.EscapeDataString(backend)}&discover=true", cancellationToken);
                if (!response.IsSuccessStatusCode) return null;
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                return string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<ProviderCatalogSnapshot>(body);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogApiError("DiscoverCustomModelsAsync", ex);
                return null;
            }
        }
    }
}
