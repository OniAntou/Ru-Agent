using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Reconciliation;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Privacy;
using RuAgent.Harness.Persistence.Context;
using RuBackend.Business.Settings;
using RuBackend.Providers;
using RuBackend.Harness.Policy;
using RuBackend.Harness.Prompting;
using RuBackend.Harness.Reconciliation;
using RuBackend.Harness.Workspace;

namespace RuBackend.Harness;

/// <summary>
/// Creates one coordinator (and therefore one tool gateway authority) per run, rooted at
/// that run's trusted workspace instead of a process-wide directory.
/// </summary>
public interface IAgentRuntimeCoordinatorFactory
{
    /// <summary>
    /// Creates a coordinator with the compaction budget policy resolved from
    /// the current settings snapshot (fail-closed to policy defaults). The
    /// gateway factory's authority open is awaited, so cancellation of the
    /// creating run propagates.
    /// </summary>
    Task<AgentRunCoordinator> CreateAsync(
        string workspaceRoot,
        PolicySnapshot? policySnapshot = null,
        RunId? runId = null,
        string? agentSessionId = null,
        CancellationToken cancellationToken = default);

    void DropRetainedApproval(RunId runId);
}

public sealed class AgentRuntimeCoordinatorFactory : IAgentRuntimeCoordinatorFactory
{
    private readonly IModelStepRunner _model;
    private readonly IHarnessEventStore _store;
    private readonly HarnessSqliteOptions _sqliteOptions;
    private readonly HarnessPayloadStore _payloadStore;
    private readonly SessionWorkStateStore? _workStates;
    private readonly ReconciliationInspectionGate _inspectionGate;
    private readonly ITransientContentRedactor? _transientRedactor;
    private readonly HarnessLiveEventHub? _liveEvents;
    private readonly AgentRunSummaryLivePublisher? _summaryPublisher;
    private readonly IAgentReplyPolicy? _replyPolicy;
    private readonly IAgentReplyEffectSink? _replyEffects;
    private readonly RuInstructionLoader _instructionLoader;
    private readonly ISettingsStore _settings;
    private readonly ITimeProvider _time;
    private readonly IModelContextLimitProvider _contextLimit;
    private readonly Prompting.AgentToolRuntimeAvailability _runtimeAvailability;
    private readonly WorkspaceCoordinatorStore _workspaceCoordinatorStore;
    private readonly Broker.IToolGatewayFactory _brokerGateways;
    private readonly InMemoryRetainedApprovalStash _retainedStash;
    private readonly IAgentRunModelMetadataResolver? _runModelMetadataResolver;
    private readonly Subagents.SubagentRuntimeBinder _subagentBinder;
    private readonly RuAgent.Harness.Core.Subagents.IAgentFamilyBudget _familyBudget;
    private readonly RuAgent.Harness.Core.Subagents.ISubagentInbox _subagentInbox;
    private readonly ILogger<AgentRuntimeCoordinatorFactory>? _budgetLogger;
    private readonly RuAgent.Broker.Tools.Mcp.McpServerHost? _mcpServers;

    public AgentRuntimeCoordinatorFactory(
        IModelStepRunner model,
        IHarnessEventStore store,
        HarnessSqliteOptions sqliteOptions,
        HarnessPayloadStore payloadStore,
        ReconciliationInspectionGate inspectionGate,
        WorkspaceCoordinatorStore workspaceCoordinatorStore,
        Broker.IToolGatewayFactory brokerGateways,
        Subagents.SubagentRuntimeBinder subagentBinder,
        RuAgent.Harness.Core.Subagents.IAgentFamilyBudget familyBudget,
        RuAgent.Harness.Core.Subagents.ISubagentInbox subagentInbox,
        ISettingsStore settings,
        ITimeProvider time,
        ITransientContentRedactor transientRedactor,
        HarnessLiveEventHub? liveEvents = null,
        AgentRunSummaryLivePublisher? summaryPublisher = null,
        IAgentReplyPolicy? replyPolicy = null,
        IAgentReplyEffectSink? replyEffects = null,
        RuInstructionLoader? instructionLoader = null,
        IModelContextLimitProvider? contextLimit = null,
        Prompting.AgentToolRuntimeAvailability? runtimeAvailability = null,
        IAgentRunModelMetadataResolver? runModelMetadataResolver = null,
        SessionWorkStateStore? workStates = null,
        ILogger<AgentRuntimeCoordinatorFactory>? logger = null,
        RuAgent.Broker.Tools.Mcp.McpServerHost? mcpServers = null)
    {
        _mcpServers = mcpServers;
        _budgetLogger = logger;
        _familyBudget = familyBudget ?? throw new ArgumentNullException(nameof(familyBudget));
        _subagentInbox = subagentInbox ?? throw new ArgumentNullException(nameof(subagentInbox));
        _subagentBinder = subagentBinder ?? throw new ArgumentNullException(nameof(subagentBinder));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sqliteOptions = sqliteOptions ?? throw new ArgumentNullException(nameof(sqliteOptions));
        _payloadStore = payloadStore ?? throw new ArgumentNullException(nameof(payloadStore));
        _workStates = workStates;
        _inspectionGate = inspectionGate ?? throw new ArgumentNullException(nameof(inspectionGate));
        _transientRedactor = transientRedactor ?? throw new ArgumentNullException(nameof(transientRedactor));
        _liveEvents = liveEvents;
        _summaryPublisher = summaryPublisher;
        _replyPolicy = replyPolicy;
        _replyEffects = replyEffects;
        _instructionLoader = instructionLoader ?? new RuInstructionLoader();
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        var pinnedModelMetadataResolver = model is IProviderBackedModelStepRunner
            ? runModelMetadataResolver
                ?? throw new InvalidOperationException("provider_model_metadata_unavailable")
            : null;
        _contextLimit = contextLimit
            ?? (pinnedModelMetadataResolver is null
                ? new DynamicModelContextLimitProvider(_settings)
                : new DynamicModelContextLimitProvider(pinnedModelMetadataResolver));
        // The execution authority is in-process now, but the model is still
        // told about web tools from the same configuration the authority
        // parses: managed startup enables them, so the availability stays
        // pinned to that composition.
        _runtimeAvailability = runtimeAvailability ?? new Prompting.AgentToolRuntimeAvailability(
            WebSearchEnabled: true,
            WebFetchEnabled: true);
        _workspaceCoordinatorStore = workspaceCoordinatorStore ?? throw new ArgumentNullException(nameof(workspaceCoordinatorStore));
        _brokerGateways = brokerGateways ?? throw new ArgumentNullException(nameof(brokerGateways));
        _runModelMetadataResolver = pinnedModelMetadataResolver;
        _retainedStash = new InMemoryRetainedApprovalStash(_time);
    }

    public void DropRetainedApproval(RunId runId) => _retainedStash.Drop(runId);

    public async Task<AgentRunCoordinator> CreateAsync(
        string workspaceRoot,
        PolicySnapshot? policySnapshot = null,
        RunId? runId = null,
        string? agentSessionId = null,
        CancellationToken cancellationToken = default)
    {
        var contextBudgetPolicy = await ResolveBudgetPolicyAsync(_settings, cancellationToken, _budgetLogger).ConfigureAwait(false);
        var availability = _runtimeAvailability with
        {
            ChildRole = null,
            McpEnabled = _mcpServers?.Configuration.IsEmpty == false,
            LanguageServersEnabled = RuAgent.Broker.Tools.LanguageServers.LanguageServerConfiguration.CurrentUser.Servers.Count > 0,
        };
        if (_brokerGateways is not { IsConfigured: true }
            || policySnapshot is null
            || runId is not { } brokerRunId)
        {
            throw new InvalidOperationException(
                "broker_gateway_unavailable: every tool call must execute inside the Broker");
        }

        var storedRun = await _store.GetRunAsync(brokerRunId, cancellationToken).ConfigureAwait(false);
        var providerWebSearchPinned = false;
        if (_runModelMetadataResolver is not null)
        {
            var metadata = await _runModelMetadataResolver.ResolveAsync(brokerRunId, cancellationToken).ConfigureAwait(false);
            availability = availability with { ToolProfile = metadata.ToolProfile, ComputerUseEnabled = metadata.ComputerUse };
            providerWebSearchPinned = metadata.ProviderWebSearch;
        }
        var subagentStore = new RuAgent.Harness.Persistence.Subagents.SqliteSubagentStore(_sqliteOptions, _payloadStore);
        var internalRun = storedRun is not null && await subagentStore.IsInternalSessionAsync(storedRun.SessionId, cancellationToken).ConfigureAwait(false);
        if (internalRun && agentSessionId != storedRun!.SessionId.Value)
            throw new HarnessStoreException("subagent_runtime_unavailable");
        if (internalRun)
        {
            var owner = await subagentStore.FindOwnerAsync(storedRun!.SessionId, cancellationToken).ConfigureAwait(false)
                ?? throw new HarnessStoreException("subagent_scope_missing");
            if (!Enum.IsDefined(owner.Role)) throw new HarnessStoreException("subagent_role_invalid");
            availability = availability with { ChildRole = owner.Role };
        }
        IToolGateway gateway = await _brokerGateways
            .CreateAsync(workspaceRoot, policySnapshot, brokerRunId, agentSessionId, cancellationToken)
            .ConfigureAwait(false);
        availability = availability with { ProviderWebSearch = UsesProviderWebSearch(availability, providerWebSearchPinned, gateway) };
        if (internalRun)
        {
            try
            {
                if (!await _subagentBinder.BindAsync(brokerRunId, workspaceRoot, policySnapshot, cancellationToken).ConfigureAwait(false))
                    throw new HarnessStoreException("subagent_scope_missing");
            }
            catch
            {
                await _brokerGateways.CloseRunAsync(brokerRunId.Value).ConfigureAwait(false);
                throw;
            }
        }
        var frames = new TransientFrameBuffer(
            64 * 1024,
            _liveEvents is null ? null : frame => PublishLiveFrame(_liveEvents, frame));
        IAgentActivitySink? activitySink = _summaryPublisher is not null
            ? _summaryPublisher
            : _liveEvents is null
                ? null
                : new AgentActivityLivePublisher(_liveEvents);
        var backgroundTaskNotes = new BackgroundTaskNoteTracker(
            ct => _brokerGateways.ListSessionTasksAsync(brokerRunId.Value, killExecutionId: null, ct));
        var promptContextProvider = new RuPromptContextProvider(
            workspaceRoot,
            _instructionLoader,
            _settings,
            _time,
            availability,
            _workspaceCoordinatorStore,
            _runModelMetadataResolver)
        {
            BackgroundTaskNotes = backgroundTaskNotes.ProbeAsync,
        };
        return new AgentRunCoordinator(
            _model,
            gateway,
            _store,
            new AgentContextAssembler(new SqliteModelContextSource(_sqliteOptions, _payloadStore, _workStates)),
            new CryptographicIdGenerator(),
            _time,
            new ToolCallScheduler(),
            frames,
            _transientRedactor,
            _contextLimit,
            _inspectionGate,
            activitySink,
            internalRun ? null : _replyPolicy,
            internalRun ? null : _replyEffects,
            promptContextProvider,
            contextBudgetPolicy,
            retainedStash: _retainedStash,
            internalRun: internalRun,
            familyBudget: _familyBudget,
            subagentInbox: _subagentInbox);
    }

    /// <summary>
    /// Reads the compaction ratios from settings into a budget policy.
    /// Expected storage/format failures use policy defaults with a safe warning.
    /// Cancellation and unexpected programming failures remain observable.
    /// </summary>
    internal static async Task<ContextBudgetPolicy> ResolveBudgetPolicyAsync(
        ISettingsStore settings,
        CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        try
        {
            var snapshot = await settings.ReadAsync(cancellationToken).ConfigureAwait(false);
            return AgentRunCoordinator.CreateBudgetPolicy(
                snapshot.CompactionTriggerRatio,
                snapshot.CompactionTargetRatio);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            // Exception messages may contain persisted settings or credentials.
            logger?.LogWarning("Context budget settings unavailable; using policy defaults. ErrorType={ErrorType}",
                error.GetType().Name);
            return AgentRunCoordinator.CreateBudgetPolicy();
        }
    }

    internal static void PublishLiveFrame(HarnessLiveEventHub hub, HarnessTransientFrame frame)
    {
        if (frame.Kind == TransientFrameKind.UsageEstimate)
        {
            // The content is already the wire payload: the measured request size for the
            // step about to run, published so a client's context meter moves before the
            // provider's usage report lands at step end.
            hub.TryPublish(frame.RunId, "agent.usage_estimate", frame.Content);
            return;
        }

        var kind = frame.Kind == TransientFrameKind.AssistantDelta
            ? "assistant.delta"
            : "tool.output.delta";
        var data = JsonSerializer.Serialize(new
        {
            text = frame.Content,
            model_step_id = frame.ModelStepId?.Value,
            execution_id = frame.ExecutionId?.Value,
            // Emitted only for a superseded attempt: the desktop client's delta
            // parser rejects unknown fields, so an ordinary frame must keep the
            // exact shape it has always had.
            reset = frame.IsReset ? true : (bool?)null,
        });
        hub.TryPublish(frame.RunId, kind, data);
    }

    /// <summary>
    /// A root run whose selection pinned provider search uses it only when the Broker
    /// would run its own <c>web_search</c> without an approval (ADR 0033): a search the
    /// provider runs cannot stop for one.
    /// </summary>
    private static bool UsesProviderWebSearch(AgentToolRuntimeAvailability availability, bool pinned, IToolGateway gateway) =>
        pinned
        && availability.WebSearchEnabled
        && availability.ChildRole is null
        && RuAgent.Protocol.AgentToolProfiles.Includes(availability.ToolProfile, "web_search")
        && gateway is RuBackend.Harness.Broker.IUnattendedToolPolicy policy
        && policy.AllowsWithoutApproval("web_search", "1.0.0", new System.Text.Json.Nodes.JsonObject { ["query"] = "ru" });
}
