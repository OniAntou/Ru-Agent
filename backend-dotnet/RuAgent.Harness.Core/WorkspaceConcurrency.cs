namespace RuAgent.Harness.Core;

public enum WorkspacePathKind
{
    Text,
    Binary,
    Unknown,
}

public enum WorkspaceClaimDecision
{
    NoOverlap,
    MergeableTextOverlap,
    BinaryConflict,
}

public sealed record WorkspacePathClaim(string NormalizedPath, WorkspacePathKind Kind)
{
    public WorkspacePathClaim Normalize()
    {
        if (string.IsNullOrWhiteSpace(NormalizedPath) || Path.IsPathRooted(NormalizedPath))
        {
            throw new ArgumentException("Workspace claims must be non-empty relative paths.", nameof(NormalizedPath));
        }

        return this with { NormalizedPath = NormalizedPath.Replace('\\', '/') };
    }
}

public static class WorkspaceConcurrencyDecision
{
    public static WorkspaceClaimDecision EvaluateClaim(
        WorkspacePathClaim existing,
        WorkspacePathClaim incoming)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(incoming);
        var left = existing.Normalize();
        var right = incoming.Normalize();
        if (!string.Equals(left.NormalizedPath, right.NormalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            return WorkspaceClaimDecision.NoOverlap;
        }

        return left.Kind == WorkspacePathKind.Text && right.Kind == WorkspacePathKind.Text
            ? WorkspaceClaimDecision.MergeableTextOverlap
            : WorkspaceClaimDecision.BinaryConflict;
    }

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
        var normalized = path.Replace('\\', '/');
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
