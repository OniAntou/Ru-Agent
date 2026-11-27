using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Policy;
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
    private readonly ITransientContentRedactor? _transientRedactor;
    private readonly ITransientContentRedactor? _reasoningRedactor;
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
    private readonly RuAgent.Harness.Core.Steering.IRunSteerInbox? _steerInbox;

    public AgentRuntimeCoordinatorFactory(
        IModelStepRunner model,
        IHarnessEventStore store,
        HarnessSqliteOptions sqliteOptions,
        HarnessPayloadStore payloadStore,
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
        RuAgent.Broker.Tools.Mcp.McpServerHost? mcpServers = null,
        RuAgent.Harness.Core.Steering.IRunSteerInbox? steerInbox = null,
        RuAgent.Harness.Persistence.Privacy.KnownSecretRedactor? reasoningRedactor = null)
    {
        _mcpServers = mcpServers;
        _steerInbox = steerInbox;
        _budgetLogger = logger;
        _familyBudget = familyBudget ?? throw new ArgumentNullException(nameof(familyBudget));
        _subagentInbox = subagentInbox ?? throw new ArgumentNullException(nameof(subagentInbox));
        _subagentBinder = subagentBinder ?? throw new ArgumentNullException(nameof(subagentBinder));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sqliteOptions = sqliteOptions ?? throw new ArgumentNullException(nameof(sqliteOptions));
        _payloadStore = payloadStore ?? throw new ArgumentNullException(nameof(payloadStore));
        _workStates = workStates;
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
            WebFetchEnabled: true,
            // A non-provider-backed model (the scripted/dev seam) has no provider pin, so it gets
            // the built-in browser tool directly; real runs are gated by the pinned marker.
            BrowserEnabled: true);
        _workspaceCoordinatorStore = workspaceCoordinatorStore ?? throw new ArgumentNullException(nameof(workspaceCoordinatorStore));
        _brokerGateways = brokerGateways ?? throw new ArgumentNullException(nameof(brokerGateways));
        _runModelMetadataResolver = pinnedModelMetadataResolver;
        _retainedStash = new InMemoryRetainedApprovalStash(_time);
        // Reasoning chunks are redacted with the secrets redactor only: the reply-text
        // sanitizer rewrites cumulative chunk overlaps, which would eat the blank lines
        // that separate reasoning paragraphs.
        _reasoningRedactor = reasoningRedactor;
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
        };
        if (_brokerGateways is not { IsConfigured: true }
            || policySnapshot is null
            || runId is not { } brokerRunId)
        {
            throw new ToolTransportException(
                "broker_gateway_unavailable");
        }

        var storedRun = await _store.GetRunAsync(brokerRunId, cancellationToken).ConfigureAwait(false);
        var providerWebSearchPinned = false;
        var advisorSelected = false;
        if (_runModelMetadataResolver is not null)
        {
            var metadata = await _runModelMetadataResolver.ResolveAsync(brokerRunId, cancellationToken).ConfigureAwait(false);
            availability = availability with
            {
                ToolProfile = metadata.ToolProfile, ComputerUseEnabled = metadata.ComputerUse, AdvisorEnabled = metadata.Advisor,
                BrowserEnabled = metadata.Browser,
            };
            providerWebSearchPinned = metadata.ProviderWebSearch;
            advisorSelected = metadata.AdvisorSelected;
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
            ct => _brokerGateways.ListSessionTasksAsync(
                brokerRunId.Value,
                new RuAgent.Protocol.SessionTaskRequest(),
                ct));
        // Session notes change slowly; one read per ten seconds keeps a per-step
        // prompt build from opening SQLite and decrypting work state every time.
        var noteRefresh = TimeSpan.FromSeconds(10);
        bool subagentsRead = false, tasksRead = false;
        DateTimeOffset subagentsAt = default, tasksAt = default;
        string? subagentsValue = null, tasksValue = null;

        // Awareness references describe a real model's workspace and machine. Scripted and
        // development seams keep their deterministic prompt: no probe runs without a pinned
        // provider, and every hook below stays null there.
        var promptAwareness = _runModelMetadataResolver is not null;
        Func<CancellationToken, Task<RuAgent.Broker.Tools.Git.WorkspaceGitSnapshot?>>? workspaceGitSnapshot = null;
        Func<string?>? machineSnapshot = null;
        Func<CancellationToken, Task<string?>>? subagentNote = null;
        Func<CancellationToken, Task<string?>>? taskNote = null;
        if (promptAwareness)
        {
            workspaceGitSnapshot = ct => _brokerGateways.WorkspaceGitSnapshotAsync(brokerRunId.Value, ct);
            machineSnapshot = MachineSnapshotProbe.Read;
            subagentNote = DescribeSubagentsAsync;
            taskNote = DescribeOpenTasksAsync;
        }

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
            WorkspaceGitSnapshot = workspaceGitSnapshot,
            MachineSnapshot = machineSnapshot,
            IncludeRunState = promptAwareness,
            ActiveSubagents = subagentNote,
            OpenTasks = taskNote,
        };

        async Task<string?> DescribeSubagentsAsync(CancellationToken ct)
        {
            var now = _time.UtcNow;
            if (subagentsRead && now - subagentsAt < noteRefresh)
            {
                return subagentsValue;
            }

            try
            {
                var records = await subagentStore.ListAsync(brokerRunId, ct).ConfigureAwait(false);
                subagentsValue = records.Count == 0
                    ? null
                    : $"{records.Count} known subagent(s): "
                        + string.Join(", ", records.Take(8).Select(record =>
                            $"{record.AgentId} ({record.Role}{(record.Canceled ? ", cancelled" : string.Empty)})"))
                        + (records.Count > 8 ? ", …" : string.Empty);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Session context only: an unreadable store omits the note.
                subagentsValue = null;
            }

            subagentsRead = true;
            subagentsAt = now;
            return subagentsValue;
        }

        async Task<string?> DescribeOpenTasksAsync(CancellationToken ct)
        {
            if (_workStates is null || storedRun is null)
            {
                return null;
            }

            var now = _time.UtcNow;
            if (tasksRead && now - tasksAt < noteRefresh)
            {
                return tasksValue;
            }

            try
            {
                var state = await _workStates
                    .GetAsync(storedRun.SessionId, storedRun.WorkspaceId, ct)
                    .ConfigureAwait(false);
                if (state is null || state.Tasks.Count == 0)
                {
                    tasksValue = null;
                }
                else
                {
                    var open = state.Tasks.Where(item => item.Status == SessionWorkItemStatus.Open).ToArray();
                    var completed = state.Tasks.Count(item => item.Status == SessionWorkItemStatus.Completed);
                    var other = state.Tasks.Count - open.Length - completed;
                    var shown = open.Take(4).Select(item => ClipNote(item.Text.Replace('\n', ' ').Trim(), 200));
                    tasksValue = $"{open.Length} open, {completed} completed"
                        + (other > 0 ? $", {other} other" : string.Empty)
                        + (open.Length > 0 ? $"; open: {string.Join(" | ", shown)}" : string.Empty);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                tasksValue = null;
            }

            tasksRead = true;
            tasksAt = now;
            return tasksValue;
        }

        static string ClipNote(string text, int maxChars)
        {
            if (text.Length <= maxChars)
            {
                return text;
            }

            // Keep the cut on a character boundary so the note stays valid text.
            var cut = char.IsHighSurrogate(text[maxChars - 1]) ? maxChars - 1 : maxChars;
            return text[..cut] + "…";
        }

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
            activitySink,
            internalRun ? null : _replyPolicy,
            internalRun ? null : _replyEffects,
            promptContextProvider,
            contextBudgetPolicy,
            retainedStash: _retainedStash,
            internalRun: internalRun,
            familyBudget: _familyBudget,
            subagentInbox: _subagentInbox,
            recommendAdvisor: advisorSelected,
            steerInbox: _steerInbox,
            reasoningRedactor: _reasoningRedactor);
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

        if (frame.Kind == TransientFrameKind.ReasoningPreview)
        {
            // One absolute line of the running thinking row, or a reset that clears it.
            // The content is the whole line, never a delta, so a dropped frame only
            // leaves the row one paragraph behind until the next line arrives.
            var preview = JsonSerializer.Serialize(new
            {
                activity_id = frame.ActivityId,
                model_step_id = frame.ModelStepId?.Value,
                text = frame.IsReset ? null : frame.Content,
                reset = frame.IsReset ? true : (bool?)null,
            });
            hub.TryPublish(frame.RunId, "agent.reasoning_preview", preview);
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
