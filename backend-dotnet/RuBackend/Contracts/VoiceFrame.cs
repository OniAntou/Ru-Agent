using System.Text.Json.Serialization;

namespace RuBackend.Contracts;

public sealed record VoiceFrame(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("sequence")] int? Sequence = null,
    [property: JsonPropertyName("sample_rate")] int? SampleRate = null,
    [property: JsonPropertyName("format")] string? Format = null);
