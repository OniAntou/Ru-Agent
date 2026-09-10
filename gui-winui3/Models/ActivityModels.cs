using System.Text.Json.Serialization;

namespace RuApp.Models
{
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
}
