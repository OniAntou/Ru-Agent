using System.Text.RegularExpressions;

namespace RuAgent.Protocol.Harness;

public sealed record BrokerRunLeaseRequest(
    string RunId,
    string SessionId,
    string WorkspaceId,
    string CanonicalRoot,
    string VolumeSerialNumber,
    string RootFileId,
    string DisplayIdentityHash,
    string Preset,
    long SnapshotVersion,
    string SnapshotHash,
    DateTimeOffset ExpiresAt)
{
    private static readonly Regex Sha256Pattern = new("^[0-9a-f]{64}$", RegexOptions.Compiled);
    private static readonly Regex IdPattern = new(@"^[a-z]+_[0-9a-f]{32}$", RegexOptions.Compiled);
    private static readonly Regex SessionPattern = new(@"^[0-9a-f]{32}$", RegexOptions.Compiled);
    private static readonly Regex VolumePattern = new(@"^[^\s]{1,64}$", RegexOptions.Compiled);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RunId) || (!IdPattern.IsMatch(RunId) && !RunId.StartsWith("run_", StringComparison.Ordinal)))
        {
            throw new AgentProtocolException("invalid_run_id");
        }

        if (string.IsNullOrWhiteSpace(SessionId) || SessionId.Length > 128)
        {
            throw new AgentProtocolException("invalid_session_id");
        }

        if (string.IsNullOrWhiteSpace(WorkspaceId) || WorkspaceId.Length > 128)
        {
            throw new AgentProtocolException("invalid_workspace_id");
        }

        if (string.IsNullOrWhiteSpace(CanonicalRoot) || !Path.IsPathRooted(CanonicalRoot))
        {
            throw new AgentProtocolException("invalid_canonical_root");
        }

        if (string.IsNullOrWhiteSpace(VolumeSerialNumber) || VolumeSerialNumber.Length > 64)
        {
            throw new AgentProtocolException("invalid_volume_serial");
        }

        if (string.IsNullOrWhiteSpace(RootFileId) || RootFileId.Length > 128)
        {
            throw new AgentProtocolException("invalid_root_file_id");
        }

        if (string.IsNullOrWhiteSpace(DisplayIdentityHash) || !Sha256Pattern.IsMatch(DisplayIdentityHash))
        {
            throw new AgentProtocolException("invalid_display_hash");
        }

        if (Preset is not ("standard" or "full_access"))
        {
            throw new AgentProtocolException("invalid_preset");
        }

        if (SnapshotVersion <= 0)
        {
            throw new AgentProtocolException("invalid_snapshot_version");
        }

        if (string.IsNullOrWhiteSpace(SnapshotHash) || !Sha256Pattern.IsMatch(SnapshotHash))
        {
            throw new AgentProtocolException("invalid_snapshot_hash");
        }

        if (ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(-5))
        {
            throw new AgentProtocolException("lease_expired");
        }
    }
}

public sealed record BrokerRunLease(
    string RunId,
    string SessionId,
    string WorkspaceId,
    string CanonicalRoot,
    string VolumeSerialNumber,
    string RootFileId,
    string DisplayIdentityHash,
    string Preset,
    long SnapshotVersion,
    string SnapshotHash,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<string> AdvertisedCapabilities)
{
    public bool IsExpired(DateTimeOffset now) => ExpiresAt <= now;
}
