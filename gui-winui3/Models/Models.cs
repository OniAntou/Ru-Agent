using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace RuApp.Models
{
    public class ChatMessageItem : INotifyPropertyChanged
    {
        private string _id = Guid.NewGuid().ToString();
        private string _role = "user";
        private string _content = string.Empty;
        private DateTime _timestamp = DateTime.Now;
        private bool _isStreaming = false;
        private bool _isError = false;

        public string Id
        {
            get => _id;
            set => SetProperty(ref _id, value);
        }

        public string Role
        {
            get => _role;
            set
            {
                if (SetProperty(ref _role, value))
                {
                    OnPropertyChanged(nameof(RoleLabel));
                }
            }
        }

        public string Content
        {
            get => _content;
            set => SetProperty(ref _content, value);
        }

        public DateTime Timestamp
        {
            get => _timestamp;
            set
            {
                if (SetProperty(ref _timestamp, value))
                {
                    OnPropertyChanged(nameof(TimeLabel));
                }
            }
        }

        public bool IsStreaming
        {
            get => _isStreaming;
            set => SetProperty(ref _isStreaming, value);
        }

        public bool IsError
        {
            get => _isError;
            set => SetProperty(ref _isError, value);
        }

        public string RoleLabel => Role == "assistant" ? "Ru" : "Bạn";

        public string TimeLabel => Timestamp.ToString("HH:mm");

        public event PropertyChangedEventHandler? PropertyChanged;

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class SessionInfo
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("created_at")]
        public string CreatedAt { get; set; } = string.Empty;

        [JsonPropertyName("updated_at")]
        public string UpdatedAt { get; set; } = string.Empty;

        [JsonPropertyName("message_count")]
        public int MessageCount { get; set; }
    }

    public class SessionListResponse
    {
        [JsonPropertyName("active_session_id")]
        public string ActiveSessionId { get; set; } = string.Empty;

        [JsonPropertyName("sessions")]
        public List<SessionInfo> Sessions { get; set; } = new();
    }

    public class AppSettingsDto
    {
        [JsonPropertyName("backend_choice")]
        public string BackendChoice { get; set; } = "gguf";

        [JsonPropertyName("opencode_api_key")]
        public string OpenCodeApiKey { get; set; } = string.Empty;

        [JsonPropertyName("has_opencode_key")]
        public bool HasOpenCodeKey { get; set; } = false;

        [JsonPropertyName("opencode_model")]
        public string OpenCodeModel { get; set; } = "opencode/free";

        [JsonPropertyName("api_variant")]
        public string ApiVariant { get; set; } = "Default";

        [JsonPropertyName("kilo_api_key")]
        public string KiloApiKey { get; set; } = string.Empty;

        [JsonPropertyName("has_kilo_key")]
        public bool HasKiloKey { get; set; } = false;

        [JsonPropertyName("kilo_model")]
        public string KiloModel { get; set; } = "kilo-auto/free";

        [JsonPropertyName("checkpoint_path")]
        public string CheckpointPath { get; set; } = string.Empty;

        [JsonPropertyName("gemini_vision_api_key")]
        public string GeminiVisionApiKey { get; set; } = string.Empty;

        [JsonPropertyName("has_gemini_vision_key")]
        public bool HasGeminiVisionKey { get; set; } = false;

        [JsonPropertyName("voice_label")]
        public string VoiceLabel { get; set; } = "Ru (OmniVoice)";

        [JsonPropertyName("voice_speed")]
        public double VoiceSpeed { get; set; } = 1.0;

        [JsonPropertyName("auto_pace_enabled")]
        public bool AutoPaceEnabled { get; set; } = true;

        [JsonPropertyName("auto_read_replies")]
        public bool AutoReadReplies { get; set; } = true;

        [JsonPropertyName("temperature")]
        public double Temperature { get; set; } = 0.7;

        [JsonPropertyName("top_k")]
        public int TopK { get; set; } = 40;

        [JsonPropertyName("max_new_tokens")]
        public int MaxNewTokens { get; set; } = 512;

        [JsonPropertyName("allow_online_search")]
        public bool AllowOnlineSearch { get; set; } = true;

        [JsonPropertyName("self_learning_enabled")]
        public bool SelfLearningEnabled { get; set; } = true;

        [JsonPropertyName("cloud_memory_consent")]
        public bool CloudMemoryConsent { get; set; } = false;
    }

    public class VoiceStatusDto
    {
        [JsonPropertyName("voice_label")]
        public string VoiceLabel { get; set; } = "Ru (OmniVoice)";

        [JsonPropertyName("voice_speed")]
        public double VoiceSpeed { get; set; } = 1.0;

        [JsonPropertyName("auto_pace_enabled")]
        public bool AutoPaceEnabled { get; set; } = true;

        [JsonPropertyName("auto_read_replies")]
        public bool AutoReadReplies { get; set; } = true;

        [JsonPropertyName("server_running")]
        public bool ServerRunning { get; set; } = false;
    }

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
        public bool Running { get; set; } = false;

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    public class ActivityLogEventDto
    {
        [JsonPropertyName("ts")]
        public double Ts { get; set; }

        [JsonPropertyName("iso")]
        public string? Iso { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("session_id")]
        public string? SessionId { get; set; }

        [JsonPropertyName("data")]
        public System.Text.Json.JsonElement Data { get; set; }
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

    public class ObservationMetricsDto
    {
        [JsonPropertyName("active")]
        public bool Active { get; set; } = false;

        [JsonPropertyName("frame_count")]
        public long FrameCount { get; set; } = 0;

        [JsonPropertyName("vision_count")]
        public long VisionCount { get; set; } = 0;

        [JsonPropertyName("emitted_count")]
        public long EmittedCount { get; set; } = 0;

        [JsonPropertyName("stale_drops")]
        public long StaleDrops { get; set; } = 0;

        [JsonPropertyName("ms_before")]
        public long MsBefore { get; set; } = 0;

        [JsonPropertyName("ms_cloud")]
        public long MsCloud { get; set; } = 0;

        [JsonPropertyName("ms_after")]
        public long MsAfter { get; set; } = 0;

        [JsonPropertyName("last_motion_delta")]
        public double LastMotionDelta { get; set; } = 0.0;

        [JsonPropertyName("engine")]
        public string Engine { get; set; } = "UltraFast-WinUI3";
    }

    public class MemoryItemDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("category")]
        public string Category { get; set; } = string.Empty;

        [JsonPropertyName("created_at")]
        public string CreatedAt { get; set; } = string.Empty;
    }
}
