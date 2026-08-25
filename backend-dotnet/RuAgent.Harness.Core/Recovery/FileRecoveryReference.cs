using RuAgent.Harness.Core.Persistence;

namespace RuAgent.Harness.Core.Recovery;

public sealed record FileRecoveryReference
{
    public FileRecoveryReference(
        PayloadId recoveryId,
        RunId runId,
        string canonicalPath,
        string contentSha256,
        DateTimeOffset createdAt)
    {
        _ = recoveryId.Value;
        _ = runId.Value;
        if (string.IsNullOrWhiteSpace(canonicalPath))
        {
            throw new ArgumentException("Canonical path must be bounded and non-empty.", nameof(canonicalPath));
        }

        if (canonicalPath.Length > 4096)
        {
            throw new ArgumentException("Canonical path exceeds its bound.", nameof(canonicalPath));
        }

        if (string.IsNullOrWhiteSpace(contentSha256) || !IsValidSha256(contentSha256))
        {
            throw new ArgumentException("Content hash must be 64 lowercase hex.", nameof(contentSha256));
        }

        RecoveryId = recoveryId;
        RunId = runId;
        CanonicalPath = canonicalPath;
        ContentSha256 = contentSha256;
        CreatedAt = createdAt;
    }

    public PayloadId RecoveryId { get; }

    public RunId RunId { get; }

    public string CanonicalPath { get; }

    public string ContentSha256 { get; }

    public DateTimeOffset CreatedAt { get; }

    private static bool IsValidSha256(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (var c in value)
        {
            if ((c < '0' || c > '9') && (c < 'a' || c > 'f'))
            {
                return false;
            }
        }

        return true;
    }
}
