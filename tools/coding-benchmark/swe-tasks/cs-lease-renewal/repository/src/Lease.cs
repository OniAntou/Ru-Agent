namespace LeaseRegistry;

public sealed record Lease(string Resource, string Owner, long Version, DateTimeOffset ExpiresAt);

public enum LeaseChangeStatus
{
    Applied,
    NotFound,
    AlreadyOwned,
    OwnerMismatch,
    VersionMismatch,
    Expired,
    InvalidRequest
}

public sealed record LeaseChangeResult(LeaseChangeStatus Status, Lease? Current)
{
    public bool Succeeded => Status == LeaseChangeStatus.Applied;
}
