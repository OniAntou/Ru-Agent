namespace RuAgent.Harness.Core;

public static class WorkspaceConcurrencyDecision
{
    public static long NextCommitSequence(long current) => checked(current + 1);

    public static bool IsLeaseActive(DateTimeOffset nowUtc, DateTimeOffset leaseExpiresAtUtc) =>
        nowUtc < leaseExpiresAtUtc;
}

public sealed record WorkspaceExternalChange(
    string NormalizedPath,
    string? ExpectedSha256,
    string ObservedSha256,
    bool IsChanged,
    string WriterKind);

public static class WorkspaceExternalChangeDetector
{
    public static WorkspaceExternalChange Detect(
        string path,
        string? expectedSha256,
        string observedSha256)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            throw new ArgumentException("Workspace path must be relative.", nameof(path));
        }

        ValidateHash(expectedSha256, nameof(expectedSha256), allowNull: true);
        ValidateHash(observedSha256, nameof(observedSha256), allowNull: false);
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("Workspace path must not contain traversal segments.", nameof(path));
        }

        var normalized = string.Join('/', segments);
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Workspace path must be non-empty relative.", nameof(path));
        }

        var changed = !string.Equals(expectedSha256, observedSha256, StringComparison.Ordinal);
        return new WorkspaceExternalChange(
            normalized,
            expectedSha256,
            observedSha256,
            changed,
            changed ? "unknown_external" : "none");
    }

    private static void ValidateHash(string? hash, string parameterName, bool allowNull)
    {
        if (hash is null && allowNull)
        {
            return;
        }

        if (hash is null || hash.Length != 64 || hash.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("SHA-256 must be 64 lowercase hexadecimal characters.", parameterName);
        }
    }
}
