namespace RuAgent.Harness.Persistence.Maintenance;

public sealed record DatabaseMaintenanceStatus(
    bool DatabaseExists,
    int SchemaVersion,
    long DatabaseBytes,
    long WalBytes,
    long PageCount,
    long FreePageCount,
    long AvailableBytes,
    IReadOnlyDictionary<string, long> TableRowCounts,
    string IntegrityStatus);

public sealed record DatabaseMaintenanceResult(
    string Operation,
    string Status,
    string? Path = null,
    IReadOnlyList<string>? SessionIds = null,
    IReadOnlyList<string>? Warnings = null);

public sealed class DatabaseMaintenanceException(string errorCode, Exception? innerException = null)
    : Exception(errorCode, innerException)
{
    public string ErrorCode { get; } = errorCode;
}
