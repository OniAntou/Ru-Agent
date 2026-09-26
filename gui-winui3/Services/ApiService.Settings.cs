using System;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using RuAgent.Client;
using RuApp.Models;

namespace RuApp.Services
{
    public partial class ApiService
    {
        public async Task<AppSettingsDto?> GetSettingsAsync()
        {
            try
            {
                return await _httpClient.GetFromJsonAsync<AppSettingsDto>($"{_baseUrl}/settings");
            }
            catch (Exception ex)
            {
                LogApiError("GetSettingsAsync", ex);
                return null;
            }
        }

        public async Task<bool> SaveSettingsAsync(AppSettingsDto settings)
        {
            try
            {
                using var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/settings", settings);
                bool ok = await HasOkEnvelopeAsync(response);
                if (ok) SettingsSaved?.Invoke(this, EventArgs.Empty);
                return ok;
            }
            catch (Exception ex)
            {
                LogApiError("SaveSettingsAsync", ex);
                return false;
            }
        }

        public async Task<bool> SaveProviderSettingsAsync(ProviderSettingsUpdateDto settings)
        {
            try
            {
                using var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/settings", settings);
                bool ok = await HasOkEnvelopeAsync(response);
                if (ok) SettingsSaved?.Invoke(this, EventArgs.Empty);
                return ok;
            }
            catch (Exception ex)
            {
                LogApiError("SaveProviderSettingsAsync", ex);
                return false;
            }
        }

        public async Task<bool> SaveReasoningSettingsAsync(
            string effort,
            string model,
            string identity)
        {
            try
            {
                var payload = new ReasoningSettingsUpdateDto
                {
                    ReasoningEffort = string.IsNullOrWhiteSpace(effort) ? "default" : effort,
                    ReasoningModel = model,
                    ReasoningIdentity = identity,
                };
                using var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/settings", payload);
                bool ok = await HasOkEnvelopeAsync(response);
                // Reasoning preferences do not change the provider catalog; notifying
                // SettingsSaved would clear the catalog and make the effort picker
                // briefly fall back to the default-only state.
                return ok;
            }
            catch (Exception ex)
            {
                LogApiError("SaveReasoningSettingsAsync", ex);
                return false;
            }
        }

        /// <summary>
        /// Stores a key for a fixed provider or a Models.dev catalog provider.
        /// </summary>
        /// <remarks>
        /// The payload comes from <see cref="ProviderCredential.KeyPayload"/> so this path
        /// and the terminal's /key cannot disagree about what the backend accepts. They
        /// did: this method used to send a bare Models.dev id, which the backend rejects
        /// as an invalid payload, so every catalog key silently failed to save.
        /// </remarks>
        public async Task<bool> SaveProviderKeyAsync(string backendChoice, string apiKey)
        {
            var target = ProviderCredential.Resolve(backendChoice);
            if (target is null)
            {
                LogApiError("SaveProviderKeyAsync", new ArgumentException(
                    $"'{backendChoice}' is not a provider the app can store a key for."));
                return false;
            }

            try
            {
                using var response = await _httpClient.PostAsJsonAsync(
                    $"{_baseUrl}/settings",
                    ProviderCredential.KeyPayload(target, apiKey));
                bool ok = await HasOkEnvelopeAsync(response);
                if (ok) SettingsSaved?.Invoke(this, EventArgs.Empty);
                return ok;
            }
            catch (Exception ex)
            {
                LogApiError("SaveProviderKeyAsync", ex);
                return false;
            }
        }

        public async Task<bool> SaveModelsDevKeyAsync(string providerId, string apiKey) =>
            await SaveProviderKeyAsync(providerId, apiKey).ConfigureAwait(true);

        public async Task<bool> RemoveProviderAsync(string backendChoice)
        {
            try
            {
                using var response = await _httpClient.PostAsJsonAsync(
                    $"{_baseUrl}/settings/provider/remove",
                    new { backend_choice = backendChoice });
                bool ok = await HasOkEnvelopeAsync(response);
                if (ok) SettingsSaved?.Invoke(this, EventArgs.Empty);
                return ok;
            }
            catch (Exception ex)
            {
                LogApiError("RemoveProviderAsync", ex);
                return false;
            }
        }

        public async Task<bool> SaveProviderModelSelectionAsync(string backend, string model)
        {
            var payload = new ProviderModelSelectionUpdateDto
            {
                BackendChoice = backend,
                SelectedModel = model,
            };

            try
            {
                using var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/settings", payload);
                bool ok = await HasOkEnvelopeAsync(response);
                if (ok) SettingsSaved?.Invoke(this, EventArgs.Empty);
                return ok;
            }
            catch (Exception ex)
            {
                LogApiError("SaveProviderModelSelectionAsync", ex);
                return false;
            }
        }
    }
}
