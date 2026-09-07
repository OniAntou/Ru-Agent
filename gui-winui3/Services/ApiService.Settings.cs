using System;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
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
                if (ok) SettingsSaved?.Invoke(this, EventArgs.Empty);
                return ok;
            }
            catch (Exception ex)
            {
                LogApiError("SaveReasoningSettingsAsync", ex);
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
