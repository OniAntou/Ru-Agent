using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RuApp.Models
{
    /// <summary>A named custom provider as Settings stores it (ADR 0034); never carries a key.</summary>
    public sealed class CustomProviderDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = string.Empty;

        [JsonPropertyName("base_url")]
        public string BaseUrl { get; set; } = string.Empty;

        [JsonPropertyName("protocol")]
        public string Protocol { get; set; } = "chat_completions";

        [JsonPropertyName("models")]
        public List<CustomProviderModelDto> Models { get; set; } = new();
    }

    public sealed class CustomProviderModelDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("context_window")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? ContextWindow { get; set; }
    }

    /// <summary>The Host's verdict on a custom provider save.</summary>
    public sealed record CustomProviderSaveResult(bool Success, string? Code, string Message);
}
