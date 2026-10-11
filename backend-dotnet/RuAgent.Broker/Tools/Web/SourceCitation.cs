namespace RuAgent.Broker.Tools.Web;

public sealed record SourceCitation(string Title, Uri Url, string? Snippet = null);

public sealed record WebFetchResult(SourceCitation Source, string SanitizedText, string ContentType,
    int Offset = 0, int? NextOffset = null);
