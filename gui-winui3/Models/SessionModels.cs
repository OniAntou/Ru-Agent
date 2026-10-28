using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace RuApp.Models
{
    public class SessionInfo : INotifyPropertyChanged
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        private string _title = string.Empty;
        [JsonPropertyName("title")]
        public string Title { get => _title; set => SetField(ref _title, value); }

        [JsonPropertyName("created_at")]
        public string CreatedAt { get; set; } = string.Empty;

        private string _updatedAt = string.Empty;
        [JsonPropertyName("updated_at")]
        public string UpdatedAt { get => _updatedAt; set => SetField(ref _updatedAt, value); }

        private string? _lastAgentActivityAt;
        [JsonPropertyName("last_agent_activity_at")]
        public string? LastAgentActivityAt { get => _lastAgentActivityAt; set => SetField(ref _lastAgentActivityAt, value); }

        private bool _isAgentRunning;
        [JsonPropertyName("is_agent_running")]
        public bool IsAgentRunning { get => _isAgentRunning; set => SetField(ref _isAgentRunning, value); }

        [JsonIgnore]
        public string AgentStatusText => IsAgentRunning ? "Running" : string.Empty;

        [JsonPropertyName("client_source")]
        public string? ClientSource { get; set; }

        [JsonPropertyName("message_count")]
        public int MessageCount { get; set; }

        [JsonPropertyName("parent_session_id")]
        public string? ParentSessionId { get; set; }

        [JsonPropertyName("forked_from_message_id")]
        public string? ForkedFromMessageId { get; set; }

        [JsonPropertyName("workspace_id")]
        public string? WorkspaceId { get; set; }

        [JsonPropertyName("workspace_label")]
        public string? WorkspaceLabel { get; set; }

        private string? _workspacePath;
        [JsonPropertyName("workspace_path")]
        public string? WorkspacePath { get => _workspacePath; set => SetField(ref _workspacePath, value); }

        [JsonIgnore]
        public bool IsForked => !string.IsNullOrEmpty(ParentSessionId);

        [JsonIgnore]
        public string IconGlyph => IsForked ? "\uE8D8" : "\uE8BD";

        [JsonIgnore]
        public Microsoft.UI.Xaml.Thickness ItemMargin => IsForked ? new Microsoft.UI.Xaml.Thickness(12, 0, 0, 0) : new Microsoft.UI.Xaml.Thickness(0, 0, 0, 0);

        [JsonIgnore]
        public string TooltipText
        {
            get
            {
                var title = IsForked ? $"{Title} (Branch)" : Title;
                if (IsAgentRunning) title += " · Running";
                return string.IsNullOrEmpty(WorkspacePath) ? title : $"{title}\nWorking directory: {WorkspacePath}";
            }
        }

        [JsonIgnore]
        public string DisplayTime => string.IsNullOrEmpty(LastAgentActivityAt) ? UpdatedAt : LastAgentActivityAt;

        /// <summary>
        /// The sidebar's time label, formatted for display.
        /// </summary>
        /// <remarks>
        /// The formatting lives here rather than in a value converter because the
        /// session row is an <c>x:Bind</c> template inside <c>MainWindow</c>, and a
        /// converter would make the generated bindings call
        /// <c>SetConverterLookupRoot</c>, which requires a <c>FrameworkElement</c> —
        /// <c>Window</c> is not one. The <c>{Binding}</c> form it replaced resolved
        /// at runtime and silently produced nothing, so the clock never rendered.
        /// </remarks>
        [JsonIgnore]
        public string DisplayTimeText => Services.SessionTimeFormatter.Format(DisplayTime, DateTimeOffset.UtcNow);

        public event PropertyChangedEventHandler? PropertyChanged;

        private void SetField<T>(ref T storage, T value, [CallerMemberName] string? property = null)
        {
            if (EqualityComparer<T>.Default.Equals(storage, value)) return;
            storage = value;
            PropertyChanged?.Invoke(this, new(property));
            if (property is nameof(Title) or nameof(WorkspacePath) or nameof(IsAgentRunning))
                PropertyChanged?.Invoke(this, new(nameof(TooltipText)));
            if (property == nameof(IsAgentRunning)) PropertyChanged?.Invoke(this, new(nameof(AgentStatusText)));
            if (property is nameof(UpdatedAt) or nameof(LastAgentActivityAt))
            {
                PropertyChanged?.Invoke(this, new(nameof(DisplayTime)));
                PropertyChanged?.Invoke(this, new(nameof(DisplayTimeText)));
            }
        }

    }

    public class SessionListResponse
    {
        [JsonPropertyName("sessions_truncated")]
        public bool SessionsTruncated { get; set; }

        [JsonPropertyName("active_session_id")]
        public string ActiveSessionId { get; set; } = string.Empty;

        private List<SessionInfo> _sessions = new();

        /// <summary>
        /// The session rows. A wire <c>"sessions": null</c> is an empty list: the
        /// sidebar rebuild iterates this directly, and a null here crashed it.
        /// </summary>
        [JsonPropertyName("sessions")]
        public List<SessionInfo> Sessions
        {
            get => _sessions;
            set => _sessions = value ?? new();
        }
    }

    public sealed class SessionListPageResponse
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        private List<SessionInfo> _sessions = new();

        [JsonPropertyName("sessions")]
        public List<SessionInfo> Sessions
        {
            get => _sessions;
            set => _sessions = value ?? new();
        }

        [JsonPropertyName("next_cursor")]
        public string? NextCursor { get; set; }
    }

    public sealed class SessionMessagesPageResponse
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("session_id")]
        public string SessionId { get; set; } = string.Empty;

        [JsonPropertyName("messages")]
        public List<SessionMessageDto> Messages { get; set; } = new();

        [JsonPropertyName("start_index")]
        public int StartIndex { get; set; }

        [JsonPropertyName("total_messages")]
        public int TotalMessages { get; set; }

        [JsonPropertyName("next_cursor")]
        public string? NextCursor { get; set; }

        [JsonPropertyName("history_epoch")]
        public long HistoryEpoch { get; set; }
    }

    public sealed class SessionMessageDto
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("source_run_id")]
        public string? SourceRunId { get; set; }

        [JsonPropertyName("source_message_id")]
        public string? SourceMessageId { get; set; }
    }
}
