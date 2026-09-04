using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuBackend.Harness.Broker;

/// <summary>
/// Builds one <see cref="BrokerToolGateway"/> per run lease: loads the Broker
/// session descriptor published by the managed bootstrap, projects the Host
/// policy snapshot onto the wire contract, and tracks open gateways so run
/// teardown closes the Broker lease with them.
/// </summary>
public class BrokerToolGatewayFactory : IAsyncDisposable
{
    private static readonly TimeSpan LeaseLifetime = TimeSpan.FromMinutes(60);

    private readonly string? _descriptorPath;
    private readonly BrokerApprovalState _approvals = new();
    private readonly ConcurrentDictionary<string, BrokerToolGateway> _gateways =
        new(StringComparer.Ordinal);

    public BrokerToolGatewayFactory(string? descriptorPath)
    {
        _descriptorPath = string.IsNullOrWhiteSpace(descriptorPath) ? null : descriptorPath;
    }

    public bool IsConfigured =>
        _descriptorPath is not null && File.Exists(_descriptorPath);

    public IToolGateway Create(string workspaceRoot, PolicySnapshot policySnapshot, RunId runId, string? agentSessionId = null)
    {
        ArgumentNullException.ThrowIfNull(policySnapshot);
        if (policySnapshot.Sha256.Length != 64)
        {
            throw new InvalidOperationException(
                "broker_gateway_requires_canonical_snapshot_hash");
        }

        // The Broker session id is the Host Agent session id, stable across
        // every drive and run of one chat. Session-scoped directory grants,
        // approval tokens, and revoke all key on it. Random per-drive ids
        // broke every approval resume with session_conflict and made revoke
        // miss. Callers without an Agent session (history ack/fail paths)
        // fall back to a run-stable id.
        var sessionId = !string.IsNullOrWhiteSpace(agentSessionId)
            ? agentSessionId.Trim()
            : "session_" + runId.Value;
        if (sessionId.Length > 128 || sessionId.Contains('\0'))
        {
            throw new InvalidOperationException("broker_gateway_requires_bounded_session_id");
        }

        var descriptor = LoadDescriptor()
            ?? throw new InvalidOperationException("broker_descriptor_unavailable");
        var canonicalRoot = Path.GetFullPath(workspaceRoot);
        var gateway = new BrokerToolGateway(
            new Uri(descriptor.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"),
            descriptor.AuthToken,
            sessionId,
            runId.Value,
            () => BuildRunOpenPayload(sessionId, runId.Value, canonicalRoot, policySnapshot),
            _approvals);
        if (_gateways.TryRemove(runId.Value, out var previous))
        {
            previous.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _gateways[runId.Value] = gateway;
        return gateway;
    }

    /// <summary>
    /// Bridges an approval resolution from the Host approval endpoint to the
    /// Broker pending approval that produced it. Returns false when the run
    /// has no tracked Broker gateway or the Broker rejects the resolution;
    /// the authorization token, when issued, stays cached run-scoped until
    /// the resumed proposal consumes it.
    /// </summary>
    public async Task<(bool Authorized, string? AuthorizationToken)> RespondApprovalAsync(
        string runId,
        string hostApprovalId,
        string command,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostApprovalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        if (!_approvals.TryGetAsk(runId, hostApprovalId, out var brokerApprovalId, out var argsKey)
            || !_gateways.TryGetValue(runId, out var gateway))
        {
            return (false, null);
        }

        return await gateway
            .RespondApprovalAsync(brokerApprovalId, command, argsKey, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Revokes one Agent session's directory grants before durable session
    /// deletion. True when revoked or when no Broker is reachable (grants
    /// cannot execute without their Broker); false only on live-Broker
    /// rejection, which must fail the delete closed.
    /// </summary>
    public virtual async Task<bool> RevokeSessionGrantsAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!IsConfigured)
        {
            return true;
        }

        var gateways = _gateways.Values.ToArray();
        if (gateways.Length == 0)
        {
            return true;
        }

        foreach (var gateway in gateways)
        {
            try
            {
                if (await gateway.RevokeSessionGrantsAsync(sessionId, cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }

                return false;
            }
            catch (ToolTransportException)
            {
                continue;
            }
        }

        return true;
    }

    public void CloseRun(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        _approvals.ClearRun(runId);
        if (_gateways.TryRemove(runId, out var gateway))
        {
            gateway.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        var gateways = _gateways.ToArray();
        _gateways.Clear();
        _approvals.Clear();
        foreach (var pair in gateways)
        {
            await pair.Value.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static JsonObject BuildRunOpenPayload(
        string sessionId,
        string runId,
        string canonicalRoot,
        PolicySnapshot policySnapshot)
    {
        var wire = PolicySnapshotMapper.ToWire(policySnapshot);
        return new JsonObject
        {
            ["run_id"] = runId,
            ["session_id"] = sessionId,
            ["workspace_id"] = wire.Workspace.WorkspaceId,
            ["canonical_root"] = canonicalRoot,
            ["volume_serial"] = wire.Workspace.VolumeSerialNumber,
            ["root_file_id"] = wire.Workspace.RootFileId,
            ["display_hash"] = wire.Workspace.DisplayIdentityHash,
            ["preset"] = wire.Preset,
            ["snapshot_version"] = wire.Version,
            ["snapshot_hash"] = policySnapshot.Sha256,
            ["expires_at"] = DateTimeOffset
                .UtcNow
                .Add(LeaseLifetime)
                .ToString("O", CultureInfo.InvariantCulture),
            ["snapshot"] = BrokerPolicyContracts.WriteSnapshot(wire),
        };
    }

    private BrokerSessionDescriptor? LoadDescriptor()
    {
        if (_descriptorPath is null || !File.Exists(_descriptorPath))
        {
            return null;
        }

        return JsonSerializer.Deserialize<BrokerSessionDescriptor>(
            File.ReadAllText(_descriptorPath),
            BrokerSessionDescriptor.JsonOptions);
    }
}
