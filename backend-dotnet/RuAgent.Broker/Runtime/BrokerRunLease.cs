using System.Security.Cryptography;
using System.Text;
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
    IReadOnlyList<string> AdvertisedCapabilities)
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
        DateTimeOffset expiresAt)
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

        // Phase-1: snapshot must be version 1 and hash must be valid hex; we don't recompute canonical hash here
        // to avoid Core dependency. The advertised capabilities are always the fallback list/read.
        var capabilities = BrokerPolicyContracts.AuthoritativeFallbackCapabilities();

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
            capabilities);
    }

    private static bool IsHex(string value) => value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
}
