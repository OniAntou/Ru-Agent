using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RuApp.Models
{
    public class VtsStatusDto
    {
        [JsonPropertyName("connected")]
        public bool Connected { get; set; } = false;

        [JsonPropertyName("model_name")]
        public string ModelName { get; set; } = string.Empty;

        [JsonPropertyName("expressions")]
        public List<string> Expressions { get; set; } = new();

        [JsonPropertyName("hotkeys")]
        public List<string> Hotkeys { get; set; } = new();

        [JsonPropertyName("state")]
        public string? State { get; set; }

        [JsonPropertyName("config")]
        public VtsConfigDto? Config { get; set; }

        [JsonPropertyName("mouse_look_enabled")]
        public bool MouseLookEnabled { get; set; }

        [JsonPropertyName("mouse_look_sensitivity")]
        public double MouseLookSensitivity { get; set; } = 1.0;

        [JsonPropertyName("overlay_enabled")]
        public bool OverlayEnabled { get; set; }

        [JsonPropertyName("overlay_running")]
        public bool OverlayRunning { get; set; }

        [JsonPropertyName("connect_error")]
        public string? ConnectError { get; set; }
    }

    public class MouseLookDto
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = false;

        [JsonPropertyName("sensitivity")]
        public double Sensitivity { get; set; } = 1.0;
    }

    public class OverlayDto
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = false;

        [JsonPropertyName("running")]
        public string? Error { get; set; }
    }

    public class VtsConfigDto
    {
        [JsonPropertyName("sway_amplitude_idle")]
        public double SwayAmplitudeIdle { get; set; } = 10.0;
        [JsonPropertyName("sway_amplitude_speaking")]
        public double SwayAmplitudeSpeaking { get; set; } = 16.0;
        [JsonPropertyName("head_scale")]
        public double HeadScale { get; set; } = 100.0;
        [JsonPropertyName("eye_scale")]
        public double EyeScale { get; set; } = 100.0;
        [JsonPropertyName("micro_action_min_delay")]
        public double MicroActionMinDelay { get; set; } = 8.0;
        [JsonPropertyName("micro_action_max_delay")]
        public double MicroActionMaxDelay { get; set; } = 22.0;
        [JsonPropertyName("pose_min_interval")]
        public double PoseMinInterval { get; set; } = 90.0;
        [JsonPropertyName("pose_max_interval")]
        public double PoseMaxInterval { get; set; } = 130.0;
        [JsonPropertyName("breath_amplitude")]
        public double BreathAmplitude { get; set; } = 1.5;
        [JsonPropertyName("breath_rate")]
        public double BreathRate { get; set; } = 0.15;
    }

    public class VtsConfigResponse
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = "";
        [JsonPropertyName("config")]
        public VtsConfigDto? Config { get; set; }
    }
}
