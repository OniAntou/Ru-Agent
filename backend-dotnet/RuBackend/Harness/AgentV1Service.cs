using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Approval;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Reconciliation;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Policy;
using RuAgent.Harness.Persistence.Privacy;
using RuAgent.Harness.Persistence.Workspace;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using RuBackend.Agent;
using RuBackend.Business.Sessions;
using RuBackend.Business.Settings;
using RuBackend.Harness.Policy;
using RuBackend.Harness.Workspace;
using RuBackend.Providers;
using RuAgent.Runtime.Adapters.Models;

namespace RuBackend.Harness;

public sealed record AgentServiceResult(int StatusCode, object? Body);

public sealed partial class AgentV1Service
{
    private readonly IHarnessEventStore _store;
    private readonly HarnessSqliteOptions _options;
    private readonly WindowsWorkspaceIdentityResolver _resolver;
    private readonly SqliteWorkspaceStore _workspaceStore;
    private readonly WorkspaceTrustRequestStore _trustStore;
    private readonly WorkspaceTrustService _trustService;
    private readonly IIdGenerator _ids;
    private readonly ITimeProvider _time;
    private readonly HarnessPayloadStore _payloadStore;
    private readonly ISessionStore _sessions;
    private readonly AgentSessionPermissionStore _permissionStore;
    private readonly IHarnessKeyStore _keyStore;
    private readonly IAgentRuntimeRootRegistry? _runtimeRoots;
    private readonly HarnessPolicySnapshotService? _policySnapshotService;
    private readonly RuBackend.Harness.Reconciliation.WorkspaceStateSnapshotService? _snapshots;
    private readonly ReconciliationInspectionGate? _inspectionGate;
    private readonly RuBackend.Harness.Reconciliation.IAgentApprovalGrants? _approvalGrants;
    private readonly IAgentBrokerClient? _agentBrokerClient;
    private readonly Broker.BrokerToolGatewayFactory? _brokerGateways;
    private readonly WorkspaceCoordinatorStore? _workspaceCoordinatorStore;
    private readonly WorkspaceJournalProtector? _workspaceJournalProtector;
    private readonly ISettingsStore? _settingsStore;
    private readonly Func<string, bool> _isVisionModel;

    private const int ApprovalToolIdMaxLength = 128;
    private const int ApprovalToolVersionMaxLength = 32;
    private const int ApprovalPathMaxLength = 4096;
    private const int ApprovalExecutableMaxLength = 256;
    private const int ApprovalCwdMaxLength = 4096;
    private const int ApprovalArgumentMaxLength = 4096;
    private const int ApprovalScriptMaxLength = 16_000;

    public static bool IsVisionModel(string? model) => ModelImage.IsVisionModel(model);

    public AgentV1Service(
        IHarnessEventStore store,
        HarnessSqliteOptions options,
        WindowsWorkspaceIdentityResolver resolver,
        SqliteWorkspaceStore workspaceStore,
        WorkspaceTrustRequestStore trustStore,
        WorkspaceTrustService trustService,
        IIdGenerator ids,
        ITimeProvider time,
        HarnessPayloadStore payloadStore,
        ISessionStore sessions,
        AgentSessionPermissionStore permissionStore,
        IHarnessKeyStore keyStore,
        IAgentRuntimeRootRegistry? runtimeRoots = null,
        RuBackend.Harness.Reconciliation.WorkspaceStateSnapshotService? snapshots = null,
        ReconciliationInspectionGate? inspectionGate = null,
        RuBackend.Harness.Reconciliation.IAgentApprovalGrants? approvalGrants = null,
        HarnessPolicySnapshotService? policySnapshotService = null,
        IAgentBrokerClient? agentBrokerClient = null,
        Broker.BrokerToolGatewayFactory? brokerGateways = null,
        WorkspaceCoordinatorStore? workspaceCoordinatorStore = null,
        WorkspaceJournalProtector? workspaceJournalProtector = null,
        ISettingsStore? settingsStore = null,
        Func<string, bool>? isVisionModel = null)
    {
        _store = store;
        _options = options;
        _resolver = resolver;
        _workspaceStore = workspaceStore;
        _trustStore = trustStore;
        _trustService = trustService;
        _ids = ids;
        _time = time;
        _payloadStore = payloadStore;
        _sessions = sessions;
        _permissionStore = permissionStore;
        _keyStore = keyStore;
        _runtimeRoots = runtimeRoots;
        _policySnapshotService = policySnapshotService;
        _snapshots = snapshots;
        _inspectionGate = inspectionGate;
        _approvalGrants = approvalGrants;
        _agentBrokerClient = agentBrokerClient;
        _brokerGateways = brokerGateways;
        _workspaceCoordinatorStore = workspaceCoordinatorStore;
        _workspaceJournalProtector = workspaceJournalProtector;
        _settingsStore = settingsStore;
        _isVisionModel = isVisionModel ?? ModelImage.IsVisionModel;
    }

    // Compatibility overload for tests that construct with old signature (fallback to ephemeral)
    public AgentV1Service(
        IHarnessEventStore store,
        HarnessSqliteOptions options,
        WindowsWorkspaceIdentityResolver resolver,
        SqliteWorkspaceStore workspaceStore,
        WorkspaceTrustRequestStore trustStore,
        WorkspaceTrustService trustService,
        IIdGenerator ids,
        ITimeProvider time,
        HarnessPayloadStore payloadStore,
        ISessionStore sessions,
        AgentSessionPermissionStore permissionStore)
        : this(store, options, resolver, workspaceStore, trustStore, trustService, ids, time, payloadStore, sessions, permissionStore, new EphemeralKeyStore())
    {
    }

    private sealed class EphemeralKeyStore : IHarnessKeyStore
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        public ValueTask<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken)
        {
            var copy = new byte[32];
            Buffer.BlockCopy(_key, 0, copy, 0, 32);
            return ValueTask.FromResult(copy);
        }
    }

    public AgentServiceResult PhaseNotAvailable() => BoundedError(409, "phase_not_available", "Not available in this phase.");

    private static AgentServiceResult BoundedError(int status, string code, string message) =>
        AgentV1Shared.BoundedError(status, code, message);

    public static string MapHistoryWaitKind(HistoryWaitKind? kind) => AgentV1Shared.MapHistoryWaitKind(kind);

    public static string MapRunState(HarnessRunStatus status) => AgentV1Shared.MapRunState(status);

    private static string ComputeSha256(string value) => AgentV1Shared.ComputeSha256(value);

    private static string ComputePolicySnapshotSha(AgentPermissionPreset preset, WorkspaceIdentity identity)
    {
        var input = $"{preset}|{identity.WorkspaceId.Value}|empty";
        return ComputeSha256(input);
    }

    private static bool IsSessionId(string value) => AgentV1Shared.IsSessionId(value);

    private static bool IsWorkspaceId(string value) => AgentV1Shared.IsWorkspaceId(value);

    private static bool IsRunId(string value) => AgentV1Shared.IsRunId(value);

    private static bool IsPayloadId(string value) => AgentV1Shared.IsPayloadId(value);

    private static bool IsTrustRequestId(string value) => AgentV1Shared.IsTrustRequestId(value);

    private static bool IsPolicyRuleId(string value) => AgentV1Shared.IsPolicyRuleId(value);

    private static bool IsLowerHex(string value) => AgentV1Shared.IsLowerHex(value);

    private static bool IsLowerHex(ReadOnlySpan<char> value) => AgentV1Shared.IsLowerHex(value);

    private static bool IsValidIsoTimestamp(string value) => AgentV1Shared.IsValidIsoTimestamp(value);
}
