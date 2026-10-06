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
using RuBackend.Business.Sessions;
using RuBackend.Business.Settings;
using RuBackend.Conversation;
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
    private readonly Broker.BrokerToolGatewayFactory? _brokerGateways;
    private readonly WorkspaceCoordinatorStore? _workspaceCoordinatorStore;
    private readonly WorkspaceJournalProtector? _workspaceJournalProtector;
    private readonly ISettingsStore? _settingsStore;
    private readonly Func<string, bool> _isVisionModel;
    private readonly SqliteAgentRunProviderSelectionStore? _providerSelectionStore;
    private readonly SessionWorkStateStore? _workStateStore;
    private readonly KnownSecretRedactor _workStateRedactor;

    /// <summary>
    /// What the provider's own catalog declares about the selected model's image input,
    /// or null when the catalog publishes no such declaration. Null is not a denial: it
    /// means the name-based check decides. Optional because the service is constructed
    /// directly by tests.
    /// </summary>
    /// <remarks>
    /// Set by the Host through property injection rather than a constructor parameter,
    /// because this service has a dozen optional collaborators and re-listing them in a
    /// hand-written DI factory silently dropped the ones the factory forgot.
    /// </remarks>
    public Func<string, CancellationToken, Task<bool?>>? DeclaredVision { get; set; }

    /// <summary>
    /// Optional because the service is constructed directly by tests. When present it records
    /// the reason behind a bounded public error, which otherwise leaves nothing to diagnose
    /// with: the caller sees only the code.
    /// </summary>
    private readonly ILogger<AgentV1Service>? _logger;

    /// <summary>
    /// Optional because the service is constructed directly by tests. When present it
    /// strips private markers from replayed narration so a reopened session reads the
    /// same sentence the live feed showed; without it the raw step text is replayed.
    /// </summary>
    private readonly IMetadataPolicy? _metadataPolicy;

    private const int ApprovalToolIdMaxLength = 128;
    private const int ApprovalToolVersionMaxLength = 32;
    private const int ApprovalPathMaxLength = 4096;
    private const int ApprovalExecutableMaxLength = 256;
    private const int ApprovalCwdMaxLength = 4096;
    private const int ApprovalArgumentMaxLength = 4096;
    private const int ApprovalScriptMaxLength = 16_000;

    /// <summary>
    /// The reconciliation decision a user's Continue implies on a run parked by an
    /// ambiguous tool outcome: resume, but keep mutations behind one read-only
    /// inspection until the workspace is proven clean.
    /// </summary>
    private static readonly ReconciliationCommand UnknownContinueCommand =
        new(ReconciliationCommandKind.UnknownContinue);

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
        HarnessPolicySnapshotService? policySnapshotService = null,
        Broker.BrokerToolGatewayFactory? brokerGateways = null,
        WorkspaceCoordinatorStore? workspaceCoordinatorStore = null,
        WorkspaceJournalProtector? workspaceJournalProtector = null,
        ISettingsStore? settingsStore = null,
        Func<string, bool>? isVisionModel = null,
        ILogger<AgentV1Service>? logger = null,
        IMetadataPolicy? metadataPolicy = null,
        SqliteAgentRunProviderSelectionStore? providerSelectionStore = null,
        SessionWorkStateStore? workStateStore = null,
        KnownSecretRedactor? workStateRedactor = null)
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
        _brokerGateways = brokerGateways;
        _workspaceCoordinatorStore = workspaceCoordinatorStore;
        _workspaceJournalProtector = workspaceJournalProtector;
        _settingsStore = settingsStore;
        _isVisionModel = isVisionModel ?? ((string model) => ModelImage.IsVisionModel(model));
        _logger = logger;
        _metadataPolicy = metadataPolicy;
        _providerSelectionStore = providerSelectionStore;
        _workStateStore = workStateStore;
        _workStateRedactor = workStateRedactor ?? KnownSecretRedactor.Empty;
    }

    private static AgentServiceResult BoundedError(int status, string code, string message) =>
        AgentV1Shared.BoundedError(status, code, message);

    private static AgentServiceResult BusyConflict(string code, string message, AgentRun blocking) =>
        AgentV1Shared.BusyConflict(code, message, blocking);

    public static string MapHistoryWaitKind(HistoryWaitKind? kind) => AgentV1Shared.MapHistoryWaitKind(kind);

    public static string MapRunState(HarnessRunStatus status) => AgentV1Shared.MapRunState(status);

    private static string ComputeSha256(string value) => AgentV1Shared.ComputeSha256(value);

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
