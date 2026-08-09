namespace RuBackend.Compatibility;

public sealed record UpstreamRequest(
    HttpMethod Method,
    string PathAndQuery,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body);
