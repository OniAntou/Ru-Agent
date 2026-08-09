namespace RuBackend.Memory;

public sealed record MemoryRecord(
    string Id,
    MemoryKind Kind,
    string Content,
    string? Scope,
    string Evidence,
    double Confidence,
    double Importance,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? AccessedAt,
    DateTimeOffset? ExpiresAt,
    string Status,
    bool Pinned);

public sealed record MemoryCandidate(
    MemoryKind Kind,
    string Content,
    string? Scope,
    string Evidence,
    double Confidence,
    double Importance,
    DateTimeOffset? ExpiresAt = null,
    string? SupersedesId = null);
