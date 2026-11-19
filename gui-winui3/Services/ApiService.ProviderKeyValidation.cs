using System.Net.Http.Json;
using System.Text.Json;

namespace RuApp.Services;

public partial class ApiService
{
    internal Task<ProviderKeyProbeResult> ValidateAndSaveModelsDevKeyAsync(
        string providerId, string key, CancellationToken ct) =>
        SaveCheckedKeyAsync(new Dictionary<string, string>
        {
            ["models_dev_provider_id"] = providerId,
            ["models_dev_api_key"] = key,
        }, ct);

    /// <summary>The legacy Custom OpenAI slot's key, checked against its saved URL.</summary>
    internal Task<ProviderKeyProbeResult> ValidateAndSaveCustomKeyAsync(string key, CancellationToken ct) =>
        SaveCheckedKeyAsync(new Dictionary<string, string> { ["custom_api_key"] = key }, ct);

    /// <summary>A named custom provider's key, checked against its saved base URL.</summary>
    internal Task<ProviderKeyProbeResult> ValidateAndSaveCustomProviderKeyAsync(
        string providerId, string key, CancellationToken ct) =>
        SaveCheckedKeyAsync(new Dictionary<string, string>
        {
            ["custom_provider_id"] = providerId,
            ["custom_provider_api_key"] = key,
        }, ct);

    /// <summary>
    /// <c>POST /settings/provider/key</c>: the Host checks the key with the provider or
    /// server before storing it. Never falls back to a plain settings save.
    /// </summary>
    private async Task<ProviderKeyProbeResult> SaveCheckedKeyAsync(
        IReadOnlyDictionary<string, string> payload, CancellationToken ct)
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/settings/provider/key", payload, ct);
            if (!response.IsSuccessStatusCode) return new("save_failed");
            using var body = await response.Content.ReadFromJsonAsync<JsonDocument>(ct);
            var code = body?.RootElement.TryGetProperty("code", out var value) == true && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
            if (body?.RootElement.TryGetProperty("status", out var status) == true && status.GetString() == "success")
            {
                SettingsSaved?.Invoke(this, EventArgs.Empty);
                // Stored, but nothing the provider offers before use told a good key from a bad one.
                return code == "provider_key_unchecked" ? new("unchecked", code) : new("valid");
            }
            return code switch
            {
                "provider_key_rejected" => new("invalid"),
                "provider_key_unverified" => new("unverified", code),
                "provider_key_malformed" => new("malformed", code),
                "provider_key_too_long" => new("too_long", code),
                "provider_insecure_endpoint" => new("insecure", code),
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
