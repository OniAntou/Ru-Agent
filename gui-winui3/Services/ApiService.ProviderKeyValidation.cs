using System.Net.Http.Json;
using System.Text.Json;

namespace RuApp.Services;

public partial class ApiService
{
    internal async Task<ProviderKeyProbeResult> ValidateAndSaveModelsDevKeyAsync(
        string providerId, string key, CancellationToken ct)
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/settings/provider/key", new
            {
                models_dev_provider_id = providerId,
                models_dev_api_key = key,
            }, ct);
            if (!response.IsSuccessStatusCode) return new("save_failed");
            using var body = await response.Content.ReadFromJsonAsync<JsonDocument>(ct);
            if (body?.RootElement.TryGetProperty("status", out var status) == true && status.GetString() == "success")
            {
                SettingsSaved?.Invoke(this, EventArgs.Empty);
                return new("valid");
            }
            var code = body?.RootElement.TryGetProperty("code", out var value) == true ? value.GetString() : null;
            return code switch
            {
                "provider_key_rejected" => new("invalid"),
                "provider_key_unverified" => new("unverified", code),
                _ => new("save_failed"),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new("superseded"); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            // A lost response is not evidence that the server did not commit.
            return new("save_failed");
        }
    }
}
