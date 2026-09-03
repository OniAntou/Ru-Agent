using RuAgent.Broker.Policy;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuAgent.Broker.Runtime;

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
    IReadOnlyList<string> AdvertisedCapabilities,
    BoundPolicySnapshot Snapshot,
    long BindingVersion)
{
    public bool IsExpired(DateTimeOffset now) => ExpiresAt <= now;

    public static BrokerRunLease CreateValidated(
        string runId,
        string sessionId,
        string workspaceId,
        string canonicalRoot,
        string volumeSerial,
        string rootFileId,
        string displayHash,
        string presetWire,
        long snapshotVersion,
        string snapshotHash,
        DateTimeOffset expiresAt,
        BrokerPolicySnapshotWire snapshotWire,
        long bindingVersion = 1,
        IReadOnlyList<string>? advertisedCapabilities = null)
    {
        if (string.IsNullOrWhiteSpace(runId) || runId.Length > 128)
        {
            throw new AgentProtocolException("invalid_run_id");
        }

        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 128)
        {
            throw new AgentProtocolException("invalid_session_id");
        }

        if (string.IsNullOrWhiteSpace(workspaceId) || workspaceId.Length > 128)
        {
            throw new AgentProtocolException("invalid_workspace_id");
        }

        var canonical = Path.GetFullPath(canonicalRoot);
        if (!Path.IsPathRooted(canonical))
        {
            throw new AgentProtocolException("invalid_canonical_root");
        }

        if (string.IsNullOrWhiteSpace(volumeSerial) || volumeSerial.Length > 64)
        {
            throw new AgentProtocolException("invalid_volume_serial");
        }

        if (string.IsNullOrWhiteSpace(rootFileId) || rootFileId.Length > 128)
        {
            throw new AgentProtocolException("invalid_root_file_id");
        }

        if (string.IsNullOrWhiteSpace(displayHash) || displayHash.Length != 64 || !IsHex(displayHash))
        {
            throw new AgentProtocolException("invalid_display_hash");
        }

        if (presetWire is not ("standard" or "full_access"))
        {
            throw new AgentProtocolException("invalid_preset");
        }

        if (snapshotVersion <= 0)
        {
            throw new AgentProtocolException("invalid_snapshot_version");
        }

        if (string.IsNullOrWhiteSpace(snapshotHash) || snapshotHash.Length != 64 || !IsHex(snapshotHash))
        {
            throw new AgentProtocolException("invalid_snapshot_hash");
        }

        if (expiresAt <= DateTimeOffset.UtcNow.AddSeconds(-1))
        {
            throw new AgentProtocolException("lease_expired");
        }

        // v2 authority: the lease must carry the complete immutable ordered-rule snapshot, its
        // canonical hash must match the declared hash, and its workspace identity must equal the
        // lease identity exactly (a moved workspace cannot inherit the binding).
        var snapshot = BoundPolicySnapshot.Parse(snapshotWire);
        if (snapshot.Version != snapshotVersion)
        {
            throw new AgentProtocolException("snapshot_version_mismatch");
        }

        if (!string.Equals(snapshot.Preset, presetWire, StringComparison.Ordinal))
        {
            throw new AgentProtocolException("preset_mismatch");
        }

        if (!string.Equals(snapshot.Sha256, snapshotHash, StringComparison.Ordinal))
        {
            throw new AgentProtocolException("snapshot_hash_mismatch");
        }

        if (!string.Equals(snapshot.WorkspaceId, workspaceId, StringComparison.Ordinal)
            || !string.Equals(snapshot.VolumeSerialNumber, volumeSerial, StringComparison.Ordinal)
            || !string.Equals(snapshot.RootFileId, rootFileId, StringComparison.Ordinal)
            || !string.Equals(snapshot.DisplayIdentityHash, displayHash, StringComparison.Ordinal))
        {
            throw new AgentProtocolException("workspace_identity_mismatch");
        }

        var capabilities = advertisedCapabilities is { Count: > 0 }
            ? advertisedCapabilities
            : BrokerPolicyContracts.AuthoritativeFallbackCapabilities();

        return new BrokerRunLease(
            runId,
            sessionId,
            workspaceId,
            canonical,
            volumeSerial,
            rootFileId,
            displayHash,
            presetWire,
            snapshotVersion,
            snapshotHash,
            expiresAt,
            capabilities,
            snapshot,
            bindingVersion);
    }

    private static bool IsHex(string value) => value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
}
