using System.Buffers;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Skills;
using RuAgent.Protocol;

namespace RuAgent.Broker.Transport;

public sealed partial class AgentConnection
{
    private AgentEnvelope HandleRunOpen(AgentEnvelope request)
    {
        try
        {
            var lease = ReadRunLease(request);
            if (!string.Equals(request.SessionId, lease.SessionId, StringComparison.Ordinal))
            {
                return ToolError(request, "session_conflict", "run lease session does not match connection session");
            }

            if (!_session.TryOpenRunLease(lease, out var errorCode))
            {
                return ToolError(request, errorCode ?? "lease_failed", "run lease failed");
            }

            return Reply(request, AgentMessageTypes.RunOpened, new JsonObject
            {
                ["status"] = ToolResultStatuses.Succeeded,
                ["run_id"] = lease.RunId,
                ["snapshot_version"] = lease.Snapshot.Version,
                ["binding_version"] = lease.BindingVersion,
                ["advertised_capabilities"] = new JsonArray(lease.AdvertisedCapabilities.Select(c => (JsonNode?)c).ToArray()),
                ["safe_summary"] = "run opened",
            });
        }
        catch (AgentProtocolException error)
        {
            return ToolError(request, error.Code, "invalid run open");
        }
    }

    private AgentEnvelope HandleRunPolicyRebind(AgentEnvelope request)
    {
        try
        {
            var lease = ReadRunLease(request);
            if (!string.Equals(request.SessionId, lease.SessionId, StringComparison.Ordinal))
            {
                return ToolError(request, "session_conflict", "run lease session does not match connection session");
            }

            if (!_session.TryRebindRunPolicy(lease, out var errorCode))
            {
                return ToolError(request, errorCode ?? "rebind_failed", "run policy rebind failed");
            }

            var bound = _session.ActiveRunLease;
            if (bound is null)
            {
                return ToolError(request, "run_not_open", "run lease is not open");
            }
            return Reply(request, AgentMessageTypes.RunRebound, new JsonObject
            {
                ["status"] = ToolResultStatuses.Succeeded,
                ["run_id"] = bound.RunId,
                ["snapshot_version"] = bound.Snapshot.Version,
                ["binding_version"] = bound.BindingVersion,
                ["safe_summary"] = "run policy rebound",
            });
        }
        catch (AgentProtocolException error)
        {
            return ToolError(request, error.Code, "invalid rebind");
        }
    }

    private BrokerRunLease ReadRunLease(AgentEnvelope request)
    {
        var payload = request.Payload;
        var runId = ReadRequiredString(payload, "run_id");
        var sessionId = ReadRequiredString(payload, "session_id");
        var workspaceId = ReadRequiredString(payload, "workspace_id");
        var canonicalRoot = ReadRequiredString(payload, "canonical_root");
        var volumeSerial = ReadRequiredString(payload, "volume_serial");
        var rootFileId = ReadRequiredString(payload, "root_file_id");
        var displayHash = ReadRequiredString(payload, "display_hash");
        var preset = ReadRequiredString(payload, "preset");
        var snapshotVersion = ReadRequiredLong(payload, "snapshot_version");
        var snapshotHash = ReadRequiredString(payload, "snapshot_hash");
        var expiresAtStr = ReadRequiredString(payload, "expires_at");
        if (!DateTimeOffset.TryParse(
                expiresAtStr,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var expiresAt))
        {
            throw new AgentProtocolException("invalid_expires_at");
        }

        if (payload["snapshot"] is not JsonObject snapshotNode)
        {
            throw new AgentProtocolException("invalid_run_open");
        }

        var snapshot = RuAgent.Protocol.Harness.BrokerPolicyContracts.ReadSnapshot(snapshotNode);
        return BrokerRunLease.CreateValidated(
            runId,
            sessionId,
            workspaceId,
            canonicalRoot,
            volumeSerial,
            rootFileId,
            displayHash,
            preset,
            snapshotVersion,
            snapshotHash,
            expiresAt,
            snapshot,
            advertisedCapabilities: _advertisedCapabilities);
    }

    private AgentEnvelope HandleRunClose(AgentEnvelope request)
    {
        if (!_session.Owns(request.SessionId))
        {
            return ToolError(request, "broker_not_ready", "session is not open");
        }

        var runId = request.Payload["run_id"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : request.RequestId;
        if (_session.TryCloseRun(runId))
        {
            return Reply(request, AgentMessageTypes.RunClosed, new JsonObject
            {
                ["status"] = ToolResultStatuses.Succeeded,
                ["run_id"] = runId,
                ["safe_summary"] = "run closed",
            });
        }

        return Reply(request, AgentMessageTypes.RunClosed, new JsonObject
        {
            ["status"] = ToolResultStatuses.Denied,
            ["error_code"] = "run_not_open",
            ["safe_summary"] = "no matching open run",
        });
    }

    private bool TryGetOwnedRunLease(
        AgentEnvelope request,
        string runId,
        out BrokerRunLease? lease,
        out string errorCode)
    {
        if (!_session.Owns(request.SessionId)
            || !_session.TryGetRunLease(runId, out lease)
            || lease is null
            || !string.Equals(lease.SessionId, request.SessionId, StringComparison.Ordinal))
        {
            lease = null;
            errorCode = "run_not_open";
            return false;
        }

        if (lease.IsExpired(DateTimeOffset.UtcNow))
        {
            lease = null;
            errorCode = "lease_expired";
            return false;
        }

        errorCode = string.Empty;
        return true;
    }
}
