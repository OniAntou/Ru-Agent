using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace RuApp.Models
{
    public readonly record struct RectPx(int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;
        public int Bottom => Y + Height;
    }

    public class ChatMessageItem : INotifyPropertyChanged
    {
        private string _id = Guid.NewGuid().ToString();
        private string _role = "user";
        private string _content = string.Empty;
        private DateTime _timestamp = DateTime.Now;
        private bool _isStreaming = false;
        private bool _isError = false;
        private string? _turnId;
        private string? _sessionId;

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
                    OnPropertyChanged(nameof(ShowTypingDots));
                    OnPropertyChanged(nameof(ShowContent));
                }
            }
        }

        public string Content
        {
            get => _content;
            set
            {
                if (SetProperty(ref _content, value))
                {
                    OnPropertyChanged(nameof(ShowTypingDots));
                    OnPropertyChanged(nameof(ShowContent));
                }
            }
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
            set
            {
                if (SetProperty(ref _isStreaming, value))
                {
                    OnPropertyChanged(nameof(ShowTypingDots));
                    OnPropertyChanged(nameof(ShowContent));
                }
            }
        }

        public bool IsError
        {
            get => _isError;
            set => SetProperty(ref _isError, value);
        }

        public string? TurnId
        {
            get => _turnId;
            set => SetProperty(ref _turnId, value);
        }

        public string? SessionId
        {
            get => _sessionId;
            set => SetProperty(ref _sessionId, value);
        }

        private System.Collections.ObjectModel.ObservableCollection<ImageAttachmentItem> _attachments = new();

        public System.Collections.ObjectModel.ObservableCollection<ImageAttachmentItem> Attachments
        {
            get => _attachments;
            set
            {
                if (SetProperty(ref _attachments, value))
                {
                    OnPropertyChanged(nameof(HasAttachments));
                }
            }
        }

        public bool HasAttachments => Attachments != null && Attachments.Count > 0;

        public bool ShowTypingDots => Role == "assistant" && IsStreaming && string.IsNullOrEmpty(Content);

        public bool ShowContent => !ShowTypingDots;

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

    public class ImageAttachmentItem : INotifyPropertyChanged
    {
        private string _id = Guid.NewGuid().ToString("N");
        private string _fileName = string.Empty;
        private string? _filePath;
        private string? _base64Data;
        private string? _textContent;
        private bool _isImage = true;
        private string _mimeType = "image/png";
        private long _byteSize;
        private Microsoft.UI.Xaml.Media.Imaging.BitmapImage? _previewImage;

        public string Id
        {
            get => _id;
            set => SetProperty(ref _id, value);
        }

        public string FileName
        {
            get => _fileName;
            set => SetProperty(ref _fileName, value);
        }

        public string? FilePath
        {
            get => _filePath;
            set => SetProperty(ref _filePath, value);
        }

        public string? Base64Data
        {
            get => _base64Data;
            set
            {
                if (SetProperty(ref _base64Data, value))
                {
                    EnsurePreviewFromBase64();
                }
            }
        }

        public void EnsurePreviewFromBase64()
        {
            if (_previewImage != null || string.IsNullOrWhiteSpace(_base64Data) || !_isImage)
            {
                return;
            }

            try
            {
                var raw = _base64Data;
                var commaIdx = raw.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
                if (commaIdx >= 0)
                {
                    raw = raw.Substring(commaIdx + 8);
                }

                var bytes = Convert.FromBase64String(raw);
                if (_byteSize == 0)
                {
                    ByteSize = bytes.Length;
                }
                var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(bytes);
                    writer.StoreAsync().AsTask().GetAwaiter().GetResult();
                }
                stream.Seek(0);
                var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                bmp.SetSource(stream);
                _previewImage = bmp;
                _thumbnailImage = bmp;
                OnPropertyChanged(nameof(PreviewImage));
                OnPropertyChanged(nameof(ThumbnailImage));
            }
            catch
            {
                // Fallback gracefully if decoding fails
            }
        }

        public string? TextContent
        {
            get => _textContent;
            set => SetProperty(ref _textContent, value);
        }

        public bool IsImage
        {
            get => _isImage;
            set
            {
                if (SetProperty(ref _isImage, value))
                {
                    OnPropertyChanged(nameof(IsDocument));
                    OnPropertyChanged(nameof(FileIconGlyph));
                }
            }
        }

        public bool IsDocument => !IsImage;

        public string FileExtension => System.IO.Path.GetExtension(FileName).ToLowerInvariant();

        public bool IsCodeFile => FileExtension switch
        {
            ".cs" or ".py" or ".js" or ".ts" or ".jsx" or ".tsx" or ".json" or ".xml" or ".yaml" or ".yml"
            or ".html" or ".css" or ".scss" or ".sql" or ".sh" or ".bat" or ".ps1" or ".rs" or ".go"
            or ".cpp" or ".c" or ".h" or ".hpp" or ".java" or ".kt" or ".swift" or ".dart" => true,
            _ => false
        };

        public string FileIconGlyph => IsImage ? "\uEB9F" : (IsCodeFile ? "\uE943" : "\uE8A5");

        public string MimeType
        {
            get => _mimeType;
            set => SetProperty(ref _mimeType, value);
        }

        public long ByteSize
        {
            get => _byteSize;
            set
            {
                if (SetProperty(ref _byteSize, value))
                {
                    OnPropertyChanged(nameof(FormattedSize));
                }
            }
        }

        public Microsoft.UI.Xaml.Media.Imaging.BitmapImage? PreviewImage
        {
            get => _previewImage;
            set => SetProperty(ref _previewImage, value);
        }

        private Microsoft.UI.Xaml.Media.Imaging.BitmapImage? _thumbnailImage;

        public Microsoft.UI.Xaml.Media.Imaging.BitmapImage? ThumbnailImage
        {
            get => _thumbnailImage ?? _previewImage;
            set => SetProperty(ref _thumbnailImage, value);
        }

        public string FormattedSize
        {
            get
            {
                if (ByteSize < 1024) return $"{ByteSize} B";
                if (ByteSize < 1024 * 1024) return $"{ByteSize / 1024.0:F1} KB";
                return $"{ByteSize / (1024.0 * 1024.0):F1} MB";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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

        [JsonPropertyName("ollama_model")]
        public string OllamaModel { get; set; } = string.Empty;

        [JsonPropertyName("lmstudio_model")]
        public string LmStudioModel { get; set; } = string.Empty;

        [JsonPropertyName("custom_model")]
        public string CustomModel { get; set; } = string.Empty;

        [JsonPropertyName("ollama_url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? OllamaUrl { get; set; }

        [JsonPropertyName("lmstudio_url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LmStudioUrl { get; set; }

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

        [JsonPropertyName("gemini_vision_api_key")]
        public string GeminiVisionApiKey { get; set; } = string.Empty;

        [JsonPropertyName("has_gemini_vision_key")]
        public bool HasGeminiVisionKey { get; set; } = false;

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

        [JsonPropertyName("self_learning_enabled")]
        public bool SelfLearningEnabled { get; set; } = true;

        [JsonPropertyName("cloud_memory_consent")]
        public bool CloudMemoryConsent { get; set; } = true;
    }

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

    public class ActivityLogItem
    {
        [JsonPropertyName("event_id")]
        public string? EventId { get; set; }

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

    public class ActivityLogEventDto : ActivityLogItem { }

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

    public class ObservationStatusDto
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("active")]
        public bool Active { get; set; }

        [JsonPropertyName("interval_seconds")]
        public double IntervalSeconds { get; set; } = 15.0;

        [JsonPropertyName("last_scene")]
        public string? LastScene { get; set; }

        [JsonPropertyName("last_event")]
        public string? LastEvent { get; set; }

        [JsonPropertyName("last_reaction")]
        public string? LastReaction { get; set; }

        [JsonPropertyName("last_reaction_id")]
        public string? LastReactionId { get; set; }

        [JsonPropertyName("last_reaction_at")]
        public double? LastReactionAt { get; set; }

        [JsonPropertyName("last_error")]
        public string? LastError { get; set; }
    }

    public sealed record DiagnosticEventDto(
        [property: JsonPropertyName("event_id")] string EventId,
        [property: JsonPropertyName("ts")] double Ts,
        [property: JsonPropertyName("iso")] string Iso,
        [property: JsonPropertyName("level")] string Level,
        [property: JsonPropertyName("component")] string Component,
        [property: JsonPropertyName("event_code")] string EventCode,
        [property: JsonPropertyName("correlation_id")] string? CorrelationId,
        [property: JsonPropertyName("duration_ms")] double? DurationMs,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("error_code")] string? ErrorCode,
        [property: JsonPropertyName("exception_kind")] string? ExceptionKind);

    public sealed record DiagnosticLogResultDto(
        [property: JsonPropertyName("events")] IReadOnlyList<DiagnosticEventDto> Events,
        [property: JsonPropertyName("cursor")] string? Cursor);

    public sealed record DiagnosticWriterStatusDto(
        [property: JsonPropertyName("queue_depth")] int QueueDepth,
        [property: JsonPropertyName("enqueued_count")] long EnqueuedCount,
        [property: JsonPropertyName("dropped_count")] long DroppedCount,
        [property: JsonPropertyName("write_failure_count")] long WriteFailureCount,
        [property: JsonPropertyName("total_bytes")] long TotalBytes,
        [property: JsonPropertyName("active_files_count")] int ActiveFilesCount);

    public sealed record DiagnosticStatusDto(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("total_log_files")] int TotalLogFiles,
        [property: JsonPropertyName("total_size_bytes")] long TotalSizeBytes,
        [property: JsonPropertyName("writer")] DiagnosticWriterStatusDto? Writer = null);

    public static class ModelContextSpecs
    {
        public static (int MaxTokens, string DisplayText) GetContextSpec(string? modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName))
                return (131_072, "128K");

            string lower = modelName.ToLowerInvariant();

            if (lower.Contains("gemini-1.5-pro") || lower.Contains("gemini-2.5-pro") || lower.Contains("gemini-2.0-pro"))
                return (2_000_000, "2M");

            if (lower.Contains("gemini") || lower.Contains("flash"))
                return (1_000_000, "1M");

            if (lower.Contains("claude-3-7") || lower.Contains("claude-3-5") || lower.Contains("claude-3") ||
                lower.Contains("o1") || lower.Contains("o3") || lower.Contains("o4"))
                return (200_000, "200K");

            if (lower.Contains("128k") || lower.Contains("llama-3.3") || lower.Contains("llama-3.1") ||
                lower.Contains("qwen2.5") || lower.Contains("qwen-2.5") || lower.Contains("dots") ||
                lower.Contains("gpt-4o") || lower.Contains("gpt-4-turbo") || lower.Contains("codestral"))
                return (131_072, "128K");

            if (lower.Contains("64k") || lower.Contains("deepseek"))
                return (65_536, "64K");

            if (lower.Contains("32k") || lower.Contains("mistral") || lower.Contains("mixtral") ||
                lower.Contains("phi-3") || lower.Contains("phi-4") || lower.Contains("gemma-2"))
                return (32_768, "32K");

            if (lower.Contains("16k") || lower.Contains("gpt-3.5-turbo-16k"))
                return (16_384, "16K");

            if (lower.Contains("8k") || lower.Contains("llama-2") || lower.Contains("llama-3-8b"))
                return (8_192, "8K");

            if (lower.Contains("4k") || lower.Contains("gpt-3.5-turbo"))
                return (4_096, "4K");

            return (131_072, "128K");
        }

        public static int EstimateTokens(string? text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return Math.Max(1, (int)Math.Ceiling(text.Length / 3.5));
        }

        public static string FormatTokens(int tokens)
        {
            if (tokens >= 1_000_000)
                return $"{tokens / 1_000_000.0:0.#}M";
            if (tokens >= 1_000)
                return $"{tokens / 1_000.0:0.#}K";
            return tokens.ToString("N0");
        }
    }

    public sealed record WorkspaceConcurrencyPolicyDto(
        [property: JsonPropertyName("mode")] string Mode,
        [property: JsonPropertyName("max_concurrent_runs")] int MaxConcurrentRuns,
        [property: JsonPropertyName("lease_ttl_seconds")] int LeaseTtlSeconds,
        [property: JsonPropertyName("merge_text_conflicts")] bool MergeTextConflicts,
        [property: JsonPropertyName("external_writer_detection")] bool ExternalWriterDetection);

    public sealed record WorkspaceConcurrencyResponseDto(
        [property: JsonPropertyName("workspace_id")] string WorkspaceId,
        [property: JsonPropertyName("policy")] WorkspaceConcurrencyPolicyDto Policy,
        [property: JsonPropertyName("active_claim_count")] int ActiveClaimCount,
        [property: JsonPropertyName("updated_at_utc")] string UpdatedAtUtc);

    public sealed record WorkspaceActivityItemDto(
        [property: JsonPropertyName("event_id")] string EventId,
        [property: JsonPropertyName("workspace_id")] string WorkspaceId,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("writer_kind")] string? WriterKind,
        [property: JsonPropertyName("sequence")] long Sequence,
        [property: JsonPropertyName("created_at_utc")] string CreatedAtUtc,
        [property: JsonPropertyName("details")] string? Details);

    public sealed record WorkspaceActivityResponseDto(
        [property: JsonPropertyName("items")] IReadOnlyList<WorkspaceActivityItemDto> Items,
        [property: JsonPropertyName("next_cursor")] string? NextCursor);

    public sealed record WorkspaceChangeItemDto(
        [property: JsonPropertyName("change_id")] string ChangeId,
        [property: JsonPropertyName("workspace_id")] string WorkspaceId,
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("sequence")] long Sequence,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("before_sha256")] string? BeforeSha256,
        [property: JsonPropertyName("after_sha256")] string? AfterSha256,
        [property: JsonPropertyName("created_at_utc")] string CreatedAtUtc);

    public sealed record WorkspaceChangesResponseDto(
        [property: JsonPropertyName("items")] IReadOnlyList<WorkspaceChangeItemDto> Items,
        [property: JsonPropertyName("next_cursor")] string? NextCursor);

    public sealed record WorkspaceChangeRestoreResponseDto(
        [property: JsonPropertyName("change_id")] string ChangeId,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("restored_sha256")] string RestoredSha256,
        [property: JsonPropertyName("restore_change_id")] string RestoreChangeId);
}
