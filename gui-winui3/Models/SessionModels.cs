using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RuApp.Models
{
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

        [JsonPropertyName("last_agent_activity_at")]
        public string? LastAgentActivityAt { get; set; }

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

        [JsonPropertyName("workspace_path")]
        public string? WorkspacePath { get; set; }

        [JsonIgnore]
        public bool IsForked => !string.IsNullOrEmpty(ParentSessionId);

        [JsonIgnore]
        public string IconGlyph => IsForked ? "\uE8D8" : "\uE8BD";

        [JsonIgnore]
        public Microsoft.UI.Xaml.Thickness ItemMargin => IsForked ? new Microsoft.UI.Xaml.Thickness(12, 0, 0, 0) : new Microsoft.UI.Xaml.Thickness(0, 0, 0, 0);

        [JsonIgnore]
        public string TooltipText => IsForked ? $"{Title} (Branch)" : Title;

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
}
