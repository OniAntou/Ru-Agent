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

        [JsonPropertyName("message_count")]
        public int MessageCount { get; set; }

        [JsonPropertyName("parent_session_id")]
        public string? ParentSessionId { get; set; }

        [JsonPropertyName("forked_from_message_id")]
        public string? ForkedFromMessageId { get; set; }

        [JsonIgnore]
        public bool IsForked => !string.IsNullOrEmpty(ParentSessionId);

        [JsonIgnore]
        public string IconGlyph => IsForked ? "\uE8D8" : "\uE8BD";

        [JsonIgnore]
        public Microsoft.UI.Xaml.Thickness ItemMargin => IsForked ? new Microsoft.UI.Xaml.Thickness(12, 0, 0, 0) : new Microsoft.UI.Xaml.Thickness(0, 0, 0, 0);

        [JsonIgnore]
        public string TooltipText => IsForked ? $"{Title} (Branch)" : Title;
    }

    public class SessionListResponse
    {
        [JsonPropertyName("active_session_id")]
        public string ActiveSessionId { get; set; } = string.Empty;

        [JsonPropertyName("sessions")]
        public List<SessionInfo> Sessions { get; set; } = new();
    }
}
