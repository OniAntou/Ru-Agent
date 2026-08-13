namespace RuBackend.Runtime;

public sealed record RuntimeWorkerResponse(
    int StatusCode,
    string ContentType,
    byte[] Body,
    IReadOnlyDictionary<string, string>? Headers = null);
