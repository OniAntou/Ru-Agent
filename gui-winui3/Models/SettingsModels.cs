using System.Text.Json.Serialization;

namespace RuApp.Models
{
    public class AppSettingsDto
    {
        [JsonPropertyName("backend_choice")]
        public string BackendChoice { get; set; } = "opencode";

        [JsonPropertyName("opencode_api_key")]
        public string OpenCodeApiKey { get; set; } = string.Empty;

        [JsonPropertyName("has_opencode_key")]
        public bool HasOpenCodeKey { get; set; } = false;

        [JsonPropertyName("opencode_model")]
        public string OpenCodeModel { get; set; } = string.Empty;

        [JsonPropertyName("api_variant")]
        public string ApiVariant { get; set; } = "Default";

        [JsonPropertyName("kilo_api_key")]
        public string KiloApiKey { get; set; } = string.Empty;

        [JsonPropertyName("has_kilo_key")]
        public bool HasKiloKey { get; set; } = false;

        [JsonPropertyName("kilo_model")]
        public string KiloModel { get; set; } = string.Empty;

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

        [JsonPropertyName("kilo_reasoning_enabled")]
        public bool KiloReasoningEnabled { get; set; } = false;



        [JsonPropertyName("voice_label")]
        public string VoiceLabel { get; set; } = "Ru (OmniVoice)";

        [JsonPropertyName("voice_style_id")]
        public string VoiceStyleId { get; set; } = "original";

        [JsonPropertyName("voice_speed")]
        public double VoiceSpeed { get; set; } = 1.0;

        [JsonPropertyName("auto_pace_enabled")]
        public bool AutoPaceEnabled { get; set; } = false;

        [JsonPropertyName("auto_read_replies")]
        public bool AutoReadReplies { get; set; } = true;

        [JsonPropertyName("show_subtitles")]
        public bool ShowSubtitles { get; set; } = false;

        [JsonPropertyName("subtitle_color")]
        public string SubtitleColor { get; set; } = "#FFFFFF";

        [JsonPropertyName("subtitle_size")]
        public string SubtitleSize { get; set; } = "medium";

        [JsonPropertyName("subtitle_duration")]
        public double SubtitleDuration { get; set; } = 2.0;

        [JsonPropertyName("subtitle_pos_mode")]
        public string SubtitlePosMode { get; set; } = "auto";

        [JsonPropertyName("subtitle_pos_x")]
        public int SubtitlePosX { get; set; } = -1;

        [JsonPropertyName("subtitle_pos_y")]
        public int SubtitlePosY { get; set; } = -1;

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
            "opencode" => settings.OpenCodeModel ?? string.Empty,
            "kilo" => settings.KiloModel ?? string.Empty,
            "openai" => settings.OpenAiModel ?? string.Empty,
            "anthropic" => settings.AnthropicModel ?? string.Empty,
            "custom" => settings.CustomModel ?? string.Empty,
            _ => string.Empty,
        };
    }
}
