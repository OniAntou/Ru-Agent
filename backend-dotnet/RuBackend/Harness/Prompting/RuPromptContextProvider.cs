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

namespace RuBackend.Harness.Prompting;

/// <summary>
/// Rebuilds the typed system-prompt context for every model step. The provider is
/// constructed for one canonical run root and never accepts a model-selected path.
/// </summary>
public sealed class RuPromptContextProvider : ISystemPromptContextProvider
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

        var model = _runModelMetadataResolver is null
            ? SelectModel(await _settings.ReadAsync(cancellationToken).ConfigureAwait(false))
            : (await _runModelMetadataResolver.ResolveAsync(run.RunId, cancellationToken).ConfigureAwait(false)).ModelLabel;
        cancellationToken.ThrowIfCancellationRequested();
        var instructions = _instructionLoader.Load(_canonicalRoot);
        var now = TimeZoneInfo.ConvertTime(_time.UtcNow, TimeZoneInfo.Local);
        var environment = new PromptEnvironment(
            model,
            Environment.OSVersion.Platform.ToString(),
            CultureInfoName(),
            TimeZoneInfo.Local.Id,
            now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            WorkspaceLabel(_canonicalRoot),
            ToolInventoryProbe.Probe());
        var runtime = await BuildRuntimeContextAsync(run, modelContext, cancellationToken).ConfigureAwait(false);
        var skipped = instructions.Skips.Where(skip => skip.Reason is not ("missing" or "empty")).ToArray();
        if (skipped.Length > 0)
        {
            var references = runtime.References.ToList();
            references.Add(new PromptReference("instruction_skipped", "workspace instruction diagnostics",
                "Some instructions were not loaded: " + string.Join("; ", skipped.Take(16)
                    .Select(skip => $"{skip.SourceId}: {skip.Reason}"))
                + ". Do not assume those instructions were applied. Inspect or repair them within current permissions."));
            // The warning stays newest, while preserving the reference-count invariant.
            if (references.Count > PromptRuntimeContext.MaxRuntimeReferences) references.RemoveAt(0);
            runtime = new PromptRuntimeContext(references);
        }
        var capabilities = BuildCapabilities();

        return PromptContextBudget.Create(
            SystemPromptContext.PromptContextSchemaVersion,
            SystemPromptContext.RequiredAgentProfile,
            AgentPermissionPresetMapper.ToWireValue(run.Preset),
            environment,
            instructions.Blocks,
            capabilities,
            runtime);
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
        var reservedReferences = (workspaceActivity is null ? 0 : 1) + (backgroundTasks is null ? 0 : 1);
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

        // The background-task note is the most actionable reference, so it stays
        // last: the overflow guard below keeps the newest entries.
        if (backgroundTasks is not null)
            references.Add(backgroundTasks);

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
