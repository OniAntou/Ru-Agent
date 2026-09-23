using System.Text.Json.Serialization;

namespace RuApp.Models
{
    public class AppSettingsDto
    {
        [JsonPropertyName("backend_choice")]
        public string BackendChoice { get; set; } = "openai";

        [JsonPropertyName("custom_model")]
        public string CustomModel { get; set; } = string.Empty;

        [JsonPropertyName("custom_url")]
        public string CustomUrl { get; set; } = "http://localhost:8000/v1";

        [JsonPropertyName("custom_api_key")]
        public string CustomApiKey { get; set; } = string.Empty;

        [JsonPropertyName("has_custom_api_key")]
        public bool HasCustomKey { get; set; } = false;

        [JsonPropertyName("openai_api_key")]
        public string OpenAiApiKey { get; set; } = string.Empty;

        [JsonPropertyName("has_openai_api_key")]
        public bool HasOpenAiKey { get; set; } = false;

        [JsonPropertyName("openai_model")]
        public string OpenAiModel { get; set; } = string.Empty;

        [JsonPropertyName("anthropic_api_key")]
        public string AnthropicApiKey { get; set; } = string.Empty;

        [JsonPropertyName("has_anthropic_api_key")]
        public bool HasAnthropicKey { get; set; } = false;

        [JsonPropertyName("anthropic_model")]
        public string AnthropicModel { get; set; } = string.Empty;

        [JsonPropertyName("cloudflare_account_id")]
        public string CloudflareAccountId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Maps a backend choice to the model id stored on <see cref="AppSettingsDto"/>.
    /// Single home for the backend -&gt; model switch previously duplicated
    /// across ChatView and MainWindow.
    /// </summary>
    internal static class ProviderModelSelection
    {
        internal static string GetSelectedModel(AppSettingsDto settings, string? backend) => backend switch
        {
            "openai" => settings.OpenAiModel ?? string.Empty,
            "anthropic" => settings.AnthropicModel ?? string.Empty,
            "custom" => settings.CustomModel ?? string.Empty,
            _ => string.Empty,
        };
    }
}
