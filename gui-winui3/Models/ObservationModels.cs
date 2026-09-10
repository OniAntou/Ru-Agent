using System.Text.Json.Serialization;

namespace RuApp.Models
{
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
}
