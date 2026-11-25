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
                // Name why the Host refused, so a rejected autosave is diagnosable from the log.
                else GuiDiagnosticWriter.WriteGuiError("settings_save", await RejectionCodeAsync(response));
                return ok;
            }
            catch (Exception ex)
            {
                LogApiError("SaveProviderSettingsAsync", ex);
                return false;
            }
        }

        /// <summary>Saves only the browser persistent-profile opt-in; it applies to new runs (ADR 0055).</summary>
        public async Task<bool> SaveBrowserProfileAsync(bool enabled)
        {
            try
            {
                var payload = new System.Text.Json.Nodes.JsonObject { ["browser_persistent_profile"] = enabled };
                using var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/settings", payload);
                return await HasOkEnvelopeAsync(response);
            }
            catch (Exception ex)
            {
                LogApiError("SaveBrowserProfileAsync", ex);
                return false;
            }
        }

        /// <summary>Saves only the computer-use opt-in; it applies to new runs (ADR 0031).</summary>
        public async Task<bool> SaveComputerUseAsync(bool enabled)
        {
            try
            {
                var payload = new System.Text.Json.Nodes.JsonObject { ["computer_use_enabled"] = enabled };
                using var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/settings", payload);
                return await HasOkEnvelopeAsync(response);
            }
            catch (Exception ex)
            {
                LogApiError("SaveComputerUseAsync", ex);
                return false;
            }
        }

        /// <summary>
        /// Saves a session's advisor (ADR 0047): both fields together, two nulls turn it off.
        /// It applies to the session's next run.
        /// </summary>
        public async Task<bool> SaveSessionAdvisorAsync(string sessionId, string? backend, string? model)
        {
            try
            {
                var payload = new System.Text.Json.Nodes.JsonObject { ["advisor_backend"] = backend, ["advisor_model"] = model };
                using var response = await _httpClient.PutAsJsonAsync(
                    $"{_baseUrl}/agent/v1/sessions/{Uri.EscapeDataString(sessionId)}/advisor", payload);
                if (!response.IsSuccessStatusCode)
                {
                    GuiDiagnosticWriter.WriteGuiError("advisor_save", await RejectionCodeAsync(response));
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                LogApiError("SaveSessionAdvisorAsync", ex);
                return false;
            }
        }

        /// <summary>
        /// The advisor of the session being opened, or null when it has none. A read
        /// failure also reads as none; the picker then shows no advisor rather than a stale one.
        /// </summary>
        public async Task<(string Backend, string Model)?> GetSessionAdvisorAsync(string sessionId, CancellationToken cancellationToken)
        {
            try
            {
                using var response = await _httpClient.GetAsync(
                    $"{_baseUrl}/agent/v1/sessions/{Uri.EscapeDataString(sessionId)}/advisor", cancellationToken);
                if (!response.IsSuccessStatusCode) return null;
                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                var root = document.RootElement;
                return root.TryGetProperty("advisor_backend", out var backend) && backend.ValueKind == System.Text.Json.JsonValueKind.String
                    && root.TryGetProperty("advisor_model", out var model) && model.ValueKind == System.Text.Json.JsonValueKind.String
                    && backend.GetString() is { Length: > 0 } backendText && model.GetString() is { Length: > 0 } modelText
                        ? (backendText, modelText)
                        : null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogApiError("GetSessionAdvisorAsync", ex);
                return null;
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
