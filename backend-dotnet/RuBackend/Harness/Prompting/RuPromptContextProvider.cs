using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Persistence;
using RuAgent.Protocol;
using RuBackend.Business.Settings;
using RuBackend.Providers;
using RuBackend.Harness.Workspace;
using RuBackend.Harness.Subagents;

using RuAgent.Protocol.Harness;
namespace RuBackend.Harness.Prompting;

/// <summary>
/// Rebuilds the typed system-prompt context for every model step. The provider is
/// constructed for one canonical run root; nested scopes come from successful durable file observations.
/// </summary>
public sealed partial class RuPromptContextProvider : ISystemPromptContextProvider
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Maximum retained tool results before accounting for the omission notice
    /// and any available workspace-activity reference.
    /// </summary>
    private const int ToolResultReferenceBudget = PromptRuntimeContext.MaxRuntimeReferences;
    private const int ToolCallAssociationEntryLimit = 512;

    private readonly string _canonicalRoot;
    private readonly RuInstructionLoader _instructionLoader;
    private readonly ISettingsStore _settings;
    private readonly ITimeProvider _time;
    private readonly AgentToolRuntimeAvailability _availability;
    private readonly WorkspaceCoordinatorStore? _workspaceCoordinatorStore;
    private readonly IAgentRunModelMetadataResolver? _runModelMetadataResolver;

    /// <summary>
    /// Optional probe returning one bounded note about this session's background
    /// terminal tasks, or null when nothing changed. The runtime loop injects it
    /// because only it holds the Broker gateway.
    /// </summary>
    public Func<CancellationToken, Task<string?>>? BackgroundTaskNotes { get; init; }

    /// <summary>
    /// Optional read-only repository facts for the prompt. Null omits the workspace
    /// snapshot; the runtime loop injects it because only it reaches the Broker.
    /// </summary>
    public Func<CancellationToken, Task<RuAgent.Broker.Tools.Git.WorkspaceGitSnapshot?>>? WorkspaceGitSnapshot { get; init; }

    /// <summary>
    /// Optional execution-free machine description. Null omits the machine snapshot;
    /// the runtime loop injects it so tests can keep the prompt deterministic.
    /// </summary>
    public Func<string?>? MachineSnapshot { get; init; }

    /// <summary>Whether the prompt carries the run counters and session notes.</summary>
    public bool IncludeRunState { get; init; }

    /// <summary>Optional bounded note naming this run's subagents.</summary>
    public Func<CancellationToken, Task<string?>>? ActiveSubagents { get; init; }

    /// <summary>Optional bounded note naming this session's open work items.</summary>
    public Func<CancellationToken, Task<string?>>? OpenTasks { get; init; }

    private const string WorkspaceSnapshotLabel = "bounded workspace and repository snapshot";
    private static readonly TimeSpan WorkspaceSnapshotRefresh = TimeSpan.FromSeconds(10);
    private string? _workspaceSnapshotText;
    private DateTimeOffset _workspaceSnapshotBuiltAt;
    private string? _machineSnapshotText;
    private bool _machineSnapshotRead;

    public RuPromptContextProvider(
        string workspaceRoot,
        RuInstructionLoader instructionLoader,
        ISettingsStore settings,
        ITimeProvider time,
        bool webToolsEnabled = false,
        WorkspaceCoordinatorStore? workspaceCoordinatorStore = null,
        IAgentRunModelMetadataResolver? runModelMetadataResolver = null)
        : this(workspaceRoot, instructionLoader, settings, time,
            new AgentToolRuntimeAvailability(webToolsEnabled, webToolsEnabled),
            workspaceCoordinatorStore,
            runModelMetadataResolver)
    {
    }

    public RuPromptContextProvider(
        string workspaceRoot,
        RuInstructionLoader instructionLoader,
        ISettingsStore settings,
        ITimeProvider time,
        AgentToolRuntimeAvailability availability,
        WorkspaceCoordinatorStore? workspaceCoordinatorStore = null,
        IAgentRunModelMetadataResolver? runModelMetadataResolver = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _canonicalRoot = WindowsWorkspaceIdentityResolver.GetCanonicalRoot(workspaceRoot);
        _instructionLoader = instructionLoader ?? throw new ArgumentNullException(nameof(instructionLoader));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _availability = availability;
        _workspaceCoordinatorStore = workspaceCoordinatorStore;
        _runModelMetadataResolver = runModelMetadataResolver;
    }

    public async Task<SystemPromptContext> BuildAsync(
        AgentRun run,
        ModelContextSnapshot modelContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(modelContext);
        cancellationToken.ThrowIfCancellationRequested();

        var metadata = _runModelMetadataResolver is null ? null
            : await _runModelMetadataResolver.ResolveAsync(run.RunId, cancellationToken).ConfigureAwait(false);
        var model = metadata?.ModelLabel ?? SelectModel(await _settings.ReadAsync(cancellationToken).ConfigureAwait(false));
        cancellationToken.ThrowIfCancellationRequested();
        var observedPaths = ObservedFilePaths(modelContext);
        var instructions = _instructionLoader.Load(_canonicalRoot, observedPaths);
        var now = TimeZoneInfo.ConvertTime(_time.UtcNow, TimeZoneInfo.Local);
        var environment = new PromptEnvironment(
            model,
            Environment.OSVersion.Platform.ToString(),
            CultureInfoName(),
            TimeZoneInfo.Local.Id,
            now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            WorkspaceLabel(_canonicalRoot),
            ToolInventoryProbe.Probe(), metadata?.ImageInputAccepted);
        var runtime = await BuildRuntimeContextAsync(run, modelContext, cancellationToken).ConfigureAwait(false);
        var projectMemory = await ProjectMemoryReferenceAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (projectMemory is not null)
            runtime = PromptContextBudget.Append(runtime, projectMemory);
        if (observedPaths.Count > 0)
        {
            runtime = PromptContextBudget.Append(runtime, NestedInstructionReference(instructions, observedPaths));
        }
        var skipped = instructions.Skips.Where(skip => skip.Reason is not ("missing" or "empty")).ToArray();
        if (skipped.Length > 0)
        {
            runtime = PromptContextBudget.Append(runtime, new PromptReference("instruction_skipped", "workspace instruction diagnostics",
                "Some instructions were not loaded: " + string.Join("; ", skipped.Take(16)
                    .Select(skip => $"{skip.SourceId}: {skip.Reason}"))
                + ". Do not assume those instructions were applied. Inspect or repair them within current permissions."));
        }
        var profiles = SubagentProfileCatalog.Load(_canonicalRoot);
        if (profiles.Items.Count > 0)
        {
            runtime = PromptContextBudget.Append(runtime,
                new PromptReference("subagent_profiles", "available subagent profiles",
                    SubagentProfileCatalog.Describe(profiles.Items)));
        }
        var capabilities = BuildCapabilities();

        return PromptContextBudget.Create(
            SystemPromptContext.PromptContextSchemaVersion,
            SystemPromptContext.RequiredAgentProfile,
            AgentPermissionPresetMapper.ToWireValue(run.Preset),
            environment,
            instructions.Blocks.Where(block => !block.SourceId.StartsWith("nested:", StringComparison.Ordinal)).ToArray(),
            capabilities,
            runtime) with { ProviderWebSearch = _availability.ProviderWebSearch };
    }

    private static string SelectModel(SettingsSnapshot settings)
    {
        var candidate = settings.Model;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = settings.Backend switch
            {
                "Custom OpenAI" => settings.CustomModel,
                "OpenAI" => settings.OpenAiModel,
                "Anthropic" => settings.AnthropicModel,
                _ => "configured-model",
            };
        }

        return string.IsNullOrWhiteSpace(candidate) ? "configured-model" : candidate.Trim();
    }

    private IReadOnlyList<PromptCapability> BuildCapabilities()
    {
        var definitions = AgentToolRuntimeProjection.Select(_availability);
        var capabilities = new List<PromptCapability>(definitions.Count);
        foreach (var definition in definitions)
        {
            capabilities.Add(new PromptCapability(
                definition.ToolId,
                definition.ToolVersion,
                definition.Description,
                definition.Scope,
                AgentToolDefinitionCatalog.CanonicalSchemaJson(definition.InputSchema),
                AgentToolDefinitionCatalog.ComputeSchemaDigest(definition.InputSchema),
                definition.ExampleArguments?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = false }),
                "enabled"));
        }

        return capabilities;
    }

    /// <summary>
    /// Shortens tool output that does not fit in one prompt reference.
    /// </summary>
    /// <remarks>
    /// Measured in UTF-8 bytes, because that is the unit the bound is expressed in and a
    /// character count would let multi-byte text through. The cut is made on a character
    /// boundary so the result stays valid text.
    ///
    /// The marker is not decorative: without it a truncated result is indistinguishable from
    /// a complete one, and the model would draw conclusions from half a listing without
    /// knowing it was half.
    ///
    /// The cut point is found by bisection. Scanning one character at a time re-encoded the
    /// whole prefix on every step, which on a 4 MiB tool result -- the documented output cap,
    /// so an ordinary oversized read -- measured at about eight minutes of a synchronous call
    /// on the host's own thread. The byte count of a prefix never decreases as the prefix
    /// grows, so bisection finds the same boundary in a few dozen probes.
    /// </remarks>
    internal static string FitReferenceContent(string content)
    {
        const string Note = "\n…(truncated: this tool result was longer than one prompt reference allows)";
        int budget = PromptReference.ContentMaxBytes - System.Text.Encoding.UTF8.GetByteCount(Note);
        if (budget <= 0 || System.Text.Encoding.UTF8.GetByteCount(content) <= PromptReference.ContentMaxBytes)
        {
            return content;
        }

        // Largest length whose prefix fits the budget. Low is always known to fit (an empty
        // prefix is zero bytes) and high is known not to, so this converges on the exact same
        // character the scan used to stop at.
        int low = 0;
        int high = content.Length;
        while (high - low > 1)
        {
            int middle = low + ((high - low) / 2);
            if (System.Text.Encoding.UTF8.GetByteCount(content.AsSpan(0, middle)) > budget)
            {
                high = middle;
            }
            else
            {
                low = middle;
            }
        }

        return content[..low] + Note;
    }

    private async Task<PromptRuntimeContext> BuildRuntimeContextAsync(
        AgentRun run,
        ModelContextSnapshot modelContext,
        CancellationToken cancellationToken)
    {
        // Durable tool receipts preserve full output. Runtime references are bounded
        // projections, and the conversation carries each retained call/result pairing.
        //
        // Each reference is fitted to the bound a reference accepts. A reference over it is
        // rejected, and a rejected reference fails the prompt, which fails the run -- so a
        // listing or a file read larger than the bound used to end the turn outright instead
        // of costing the model the tail of one result. The marker says the output was cut,
        // because a silently shortened result reads as a complete one.
        //
        // Keep only the newest bounded tool references. Older durable receipts remain in
        // the run store, but the final model history may omit their conversation exchanges.
        var toolCallByReceiptId = BuildToolCallAssociations(modelContext);
        var toolResults = modelContext.Entries
            .Where(entry => entry.Kind == ModelContextEntryKind.ToolResult
                && !string.IsNullOrWhiteSpace(entry.Content))
            .OrderBy(entry => entry.Sequence)
            .ToList();
        var workspaceActivity = await BuildWorkspaceActivityReferenceAsync(run, cancellationToken).ConfigureAwait(false);
        var backgroundTasks = await BuildBackgroundTaskReferenceAsync(cancellationToken).ConfigureAwait(false);
        var workspaceSnapshot = await BuildWorkspaceSnapshotReferenceAsync(cancellationToken).ConfigureAwait(false);
        var machineSnapshot = MachineSnapshotReference();
        var runState = await BuildRunStateReferenceAsync(run, modelContext, cancellationToken).ConfigureAwait(false);
        var verification = modelContext.VerificationEvidence.DescribePending();
        var reservedReferences = (workspaceActivity is null ? 0 : 1) + (backgroundTasks is null ? 0 : 1)
            + (verification is null ? 0 : 1) + (workspaceSnapshot is null ? 0 : 1)
            + (machineSnapshot is null ? 0 : 1) + (runState is null ? 0 : 1);
        var maximumResultReferences = ToolResultReferenceBudget - reservedReferences;
        var hasOmittedResults = toolResults.Count > maximumResultReferences;
        var resultReferenceBudget = maximumResultReferences - (hasOmittedResults ? 1 : 0);
        var newestResults = toolResults.Count <= resultReferenceBudget
            ? toolResults
            : toolResults.GetRange(toolResults.Count - resultReferenceBudget, resultReferenceBudget);
        var omittedResults = toolResults.Count - newestResults.Count;

        var references = new List<PromptReference>();
        if (omittedResults > 0)
        {
            references.Add(new PromptReference(
                "tool_result_omitted",
                "untrusted tool results omitted from the prompt",
                $"{omittedResults} older tool result(s) are omitted from this bounded reference list. "
                + "Their durable receipts remain stored for this run, but older exchanges may not be in model history. "
                + "The newest results below may include projection cursors."));
        }

        references.AddRange(newestResults.Select(entry =>
        {
            var receipt = entry.ToolReceipt!;
            toolCallByReceiptId.TryGetValue(receipt.ToolCallId.Value, out var call);
            return new PromptReference(
                "tool_result",
                "untrusted tool result" + (receipt.ProviderCallId is { } providerId ? ":" + providerId : string.Empty),
                StructuredToolOutputProjector.Project(
                    call?.ToolId ?? string.Empty,
                    call?.Arguments,
                    entry.Content!,
                    PromptReference.ContentMaxBytes,
                    ToolOutputProjectionUnit.Utf8Bytes,
                    (value, _) => FitReferenceContent(value),
                    receipt.ToolCallId.Value,
                    receipt.ProviderCallId));
        }));

        if (workspaceActivity is not null)
            references.Add(workspaceActivity);
        // The snapshot references orient the model before any exploratory call. They
        // are stable-ish data, so they sit ahead of the most actionable notes below.
        if (workspaceSnapshot is not null)
            references.Add(workspaceSnapshot);
        if (machineSnapshot is not null)
            references.Add(machineSnapshot);
        if (runState is not null)
            references.Add(runState);

        // The background-task note is the most actionable reference, so it stays
        // last: the overflow guard below keeps the newest entries.
        if (backgroundTasks is not null)
            references.Add(backgroundTasks);
        if (verification is not null)
            references.Add(new PromptReference("verification_state", "receipt-backed outstanding checks", verification));

        // The reservation above is arithmetic, not a guarantee: a third source of
        // references added later could overflow the Core bound. Degrade instead of
        // failing the turn: keep the newest references inside the bound and fold the
        // dropped count into one note, so an assembly slip costs the oldest references
        // rather than ending the run with a contract code.
        if (references.Count > PromptRuntimeContext.MaxRuntimeReferences)
        {
            int keptCount = PromptRuntimeContext.MaxRuntimeReferences - 1;
            int droppedCount = references.Count - keptCount;
            var kept = references.GetRange(references.Count - keptCount, keptCount);
            references = new List<PromptReference>(PromptRuntimeContext.MaxRuntimeReferences)
            {
                new(
                    "references_omitted",
                    "untrusted references omitted from the prompt",
                    $"{droppedCount} earlier reference(s) are omitted from this bounded reference list."),
            };
            references.AddRange(kept);
        }

        return new PromptRuntimeContext(references);
    }

    private async Task<PromptReference?> BuildBackgroundTaskReferenceAsync(
        CancellationToken cancellationToken)
    {
        if (BackgroundTaskNotes is null)
        {
            return null;
        }

        string? note;
        try
        {
            note = await BackgroundTaskNotes(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // The note is advisory: a probe failure must not fail the prompt.
            return null;
        }

        return string.IsNullOrWhiteSpace(note)
            ? null
            : new PromptReference(
                "background_tasks",
                "harness background task state",
                note);
    }

    /// <summary>
    /// Builds the workspace snapshot reference, refreshing its repository facts on a
    /// short interval so an edit the run just made is visible without paying a Git
    /// read on every step. Failures and cancellations follow the note rules above.
    /// </summary>
    private async Task<PromptReference?> BuildWorkspaceSnapshotReferenceAsync(
        CancellationToken cancellationToken)
    {
        if (WorkspaceGitSnapshot is null)
        {
            return null;
        }

        var now = _time.UtcNow;
        if (_workspaceSnapshotText is not null && now - _workspaceSnapshotBuiltAt < WorkspaceSnapshotRefresh)
        {
            return TryCreateReference("workspace_snapshot", WorkspaceSnapshotLabel, _workspaceSnapshotText);
        }

        RuAgent.Broker.Tools.Git.WorkspaceGitSnapshot? git = null;
        try
        {
            git = await WorkspaceGitSnapshot(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Context only: an unavailable repository probe costs the snapshot section.
            git = null;
        }

        string? text;
        try
        {
            text = WorkspaceSnapshotComposer.Render(git, _canonicalRoot);
        }
        catch (Exception)
        {
            // Rendering is presentation only: an unexpected failure costs the section.
            return null;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        _workspaceSnapshotText = text;
        _workspaceSnapshotBuiltAt = now;
        return TryCreateReference("workspace_snapshot", WorkspaceSnapshotLabel, text);
    }

    private PromptReference? MachineSnapshotReference()
    {
        if (MachineSnapshot is null)
        {
            return null;
        }

        if (!_machineSnapshotRead)
        {
            _machineSnapshotRead = true;
            try
            {
                _machineSnapshotText = MachineSnapshot();
            }
            catch (Exception)
            {
                _machineSnapshotText = null;
            }
        }

        return string.IsNullOrWhiteSpace(_machineSnapshotText)
            ? null
            : TryCreateReference("machine_snapshot", "bounded machine and toolchain facts", _machineSnapshotText);
    }

    /// <summary>
    /// Run counters and session notes. They change every step, so they travel as
    /// runtime references after history instead of in the stable system prefix.
    /// </summary>
    private async Task<PromptReference?> BuildRunStateReferenceAsync(
        AgentRun run,
        ModelContextSnapshot modelContext,
        CancellationToken cancellationToken)
    {
        if (!IncludeRunState)
        {
            return null;
        }

        var steps = modelContext.Entries.Count(entry => entry.Kind == ModelContextEntryKind.AssistantStep);
        var results = modelContext.Entries.Count(entry => entry.Kind == ModelContextEntryKind.ToolResult);
        // BudgetWindow is the window ordinal (a run opens window 2, 3, ...), not its
        // size; ModelStepLimit bounds how many steps fit in any one window.
        var stepInWindow = Math.Min(run.ModelStepsInWindow + 1, AgentBudgetPolicy.ModelStepLimit);
        var builder = new StringBuilder();
        builder.AppendLine("run_state:");
        builder.AppendLine(
            $"  model step: {stepInWindow} of {AgentBudgetPolicy.ModelStepLimit} in budget window {run.BudgetWindow}");
        builder.AppendLine($"  tool calls this window: {run.ToolCallsInWindow} of {AgentBudgetPolicy.ToolCallLimit}");
        builder.AppendLine($"  visible transcript: {steps} assistant step(s), {results} tool result(s)");

        var subagents = await ReadNoteAsync(ActiveSubagents, cancellationToken).ConfigureAwait(false);
        if (subagents is not null)
        {
            builder.AppendLine($"  subagents: {subagents}");
        }

        var tasks = await ReadNoteAsync(OpenTasks, cancellationToken).ConfigureAwait(false);
        if (tasks is not null)
        {
            builder.AppendLine($"  work items: {tasks}");
        }

        return TryCreateReference("run_state", "host run counters and session state", builder.ToString().TrimEnd());
    }

    /// <summary>
    /// Builds one reference from untrusted or host-assembled text. NUL and invalid
    /// UTF-16 are removed first (a todo or filename can carry either), and a text the
    /// Core still rejects omits only this reference instead of failing the prompt.
    /// </summary>
    private static PromptReference? TryCreateReference(string kind, string label, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var safe = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text.Replace("\0", string.Empty, StringComparison.Ordinal)));
        if (string.IsNullOrWhiteSpace(safe))
        {
            return null;
        }

        try
        {
            return new PromptReference(kind, label, safe);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static async Task<string?> ReadNoteAsync(
        Func<CancellationToken, Task<string?>>? note,
        CancellationToken cancellationToken)
    {
        if (note is null)
        {
            return null;
        }

        try
        {
            var value = await note(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<PromptReference?> BuildWorkspaceActivityReferenceAsync(
        AgentRun run,
        CancellationToken cancellationToken)
    {
        if (_workspaceCoordinatorStore is null)
            return null;

        try
        {
            var changes = await _workspaceCoordinatorStore
                .ListChangesAsync(run.WorkspaceId, 0, 16, cancellationToken)
                .ConfigureAwait(false);
            var external = changes
                .Where(change => change.RunId is null)
                .TakeLast(8)
                .Select(change =>
                    $"sequence={change.CommitSequence}; path={change.NormalizedPath}; before={change.BeforeSha256 ?? "missing"}; observed={change.AfterSha256 ?? "missing"}")
                .ToArray();
            return external.Length == 0
                ? null
                : new PromptReference(
                    "workspace_activity",
                    "untrusted workspace activity",
                    string.Join("\n", external));
        }
        catch (HarnessStoreException)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, ModelToolCall> BuildToolCallAssociations(ModelContextSnapshot modelContext)
    {
        var pendingByProviderId = new Dictionary<string, ModelToolCall>(StringComparer.Ordinal);
        var byReceiptId = new Dictionary<string, ModelToolCall>(StringComparer.Ordinal);
        foreach (var entry in modelContext.Entries.TakeLast(ToolCallAssociationEntryLimit))
        {
            if (entry.Kind == ModelContextEntryKind.AssistantStep && entry.AssistantStep is { } step)
            {
                foreach (var call in step.ToolCalls)
                    pendingByProviderId[call.ProviderCallId] = call;
                continue;
            }

            if (entry.Kind != ModelContextEntryKind.ToolResult || entry.ToolReceipt is not { } receipt)
                continue;

            if (pendingByProviderId.Remove(receipt.ProviderCallId!, out var matched))
                byReceiptId[receipt.ToolCallId.Value] = matched;
        }

        return byReceiptId;
    }

    private static string WorkspaceLabel(string canonicalRoot)
    {
        var trimmed = canonicalRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var label = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(label) ? "workspace" : label;
    }

    private static string CultureInfoName() =>
        System.Globalization.CultureInfo.CurrentCulture.Name is { Length: > 0 } name
            ? name
            : "en-US";
}
