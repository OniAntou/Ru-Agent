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
        /// Replaces the whole provider list, then stores one provider's key through the
        /// checked key route, which asks that server before storing it. The list is saved
        /// first so the key is checked against the endpoint just saved; a refused key
        /// leaves the list saved and the previous key in place.
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
                if (!success || string.IsNullOrEmpty(keyProviderId) || string.IsNullOrEmpty(apiKey))
                    return new(success, code, message);

                var key = await ValidateAndSaveCustomProviderKeyAsync(keyProviderId, apiKey, CancellationToken.None);
                return key.Code switch
                {
                    "valid" => new(true, null, message),
                    "unchecked" => new(true, "provider_key_unchecked", message),
                    "invalid" => new(false, "provider_key_rejected", string.Empty, DefinitionSaved: true),
                    _ => new(false, key.ErrorCode ?? "provider_key_save_failed", string.Empty, DefinitionSaved: true),
                };
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
