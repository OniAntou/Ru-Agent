namespace RuBackend.Compatibility;

public sealed record UpstreamResponse(
    int StatusCode,
    string ContentType,
    byte[] Body,
    IReadOnlyDictionary<string, string>? Headers = null);
