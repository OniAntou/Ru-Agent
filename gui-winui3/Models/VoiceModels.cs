using System.Text.Json.Serialization;

namespace RuApp.Models
{
    public class VoiceStatusDto
    {
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

        [JsonPropertyName("server_running")]
        public bool ServerRunning { get; set; } = false;

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

    public sealed class VoiceSettingsUpdateDto
    {
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
}
