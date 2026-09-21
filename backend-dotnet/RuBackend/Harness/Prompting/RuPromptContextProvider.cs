using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Persistence;
using RuAgent.Protocol;
using RuBackend.Business.Settings;
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
    /// How many tool results may appear as references. One slot of
    /// <see cref="PromptRuntimeContext.MaxRuntimeReferences"/> is reserved for the
    /// workspace-activity reference.
    /// </summary>
    private const int ToolResultReferenceBudget = PromptRuntimeContext.MaxRuntimeReferences - 1;

    private readonly string _canonicalRoot;
    private readonly RuInstructionLoader _instructionLoader;
    private readonly ISettingsStore _settings;
    private readonly ITimeProvider _time;
    private readonly AgentToolRuntimeAvailability _availability;
    private readonly WorkspaceCoordinatorStore? _workspaceCoordinatorStore;

    public RuPromptContextProvider(
        string workspaceRoot,
        RuInstructionLoader instructionLoader,
        ISettingsStore settings,
        ITimeProvider time,
        bool webToolsEnabled = false,
        WorkspaceCoordinatorStore? workspaceCoordinatorStore = null)
        : this(workspaceRoot, instructionLoader, settings, time,
            new AgentToolRuntimeAvailability(webToolsEnabled, webToolsEnabled),
            workspaceCoordinatorStore)
    {
    }

    public RuPromptContextProvider(
        string workspaceRoot,
        RuInstructionLoader instructionLoader,
        ISettingsStore settings,
        ITimeProvider time,
        AgentToolRuntimeAvailability availability,
        WorkspaceCoordinatorStore? workspaceCoordinatorStore = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _canonicalRoot = WindowsWorkspaceIdentityResolver.GetCanonicalRoot(workspaceRoot);
        _instructionLoader = instructionLoader ?? throw new ArgumentNullException(nameof(instructionLoader));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _availability = availability;
        _workspaceCoordinatorStore = workspaceCoordinatorStore;
    }

    public async Task<SystemPromptContext> BuildAsync(
        AgentRun run,
        ModelContextSnapshot modelContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(modelContext);
        cancellationToken.ThrowIfCancellationRequested();

        var settings = await _settings.ReadAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var instructions = _instructionLoader.Load(_canonicalRoot);
        var now = _time.UtcNow;
        var model = SelectModel(settings);
        var environment = new PromptEnvironment(
            model,
            Environment.OSVersion.Platform.ToString(),
            CultureInfoName(),
            TimeZoneInfo.Local.Id,
            now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            WorkspaceLabel(_canonicalRoot));
        var runtime = await BuildRuntimeContextAsync(run, modelContext, cancellationToken).ConfigureAwait(false);
        var capabilities = BuildCapabilities();

        return new SystemPromptContext(
            SystemPromptContext.PromptContextSchemaVersion,
            SystemPromptContext.RequiredAgentProfile,
            run.Preset switch
            {
                AgentPermissionPreset.FullAccess => "full_access",
                AgentPermissionPreset.Trusted => "trusted",
                _ => "standard",
            },
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
                "OpenCode Zen" => settings.OpenCodeModel,
                "Kilo Gateway" => settings.KiloModel,
                "Custom OpenAI" => settings.CustomModel,
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
                definition.InputSchema.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = false }),
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
    /// </remarks>
    internal static string FitReferenceContent(string content)
    {
        const string Note = "\n…(truncated: this tool result was longer than one prompt reference allows)";
        int budget = PromptReference.ContentMaxBytes - System.Text.Encoding.UTF8.GetByteCount(Note);
        if (budget <= 0 || System.Text.Encoding.UTF8.GetByteCount(content) <= PromptReference.ContentMaxBytes)
        {
            return content;
        }

        int length = content.Length;
        while (length > 0 && System.Text.Encoding.UTF8.GetByteCount(content.AsSpan(0, length)) > budget)
        {
            length--;
        }

        return content[..length] + Note;
    }

    private async Task<PromptRuntimeContext> BuildRuntimeContextAsync(
        AgentRun run,
        ModelContextSnapshot modelContext,
        CancellationToken cancellationToken)
    {
        // Tool output keeps its full-fidelity channel here as untrusted data;
        // the model conversation additionally carries the exchange history
        // (see LoopbackModelStepRunner.BuildConversation) so the model knows
        // which calls already ran.
        //
        // Each reference is fitted to the bound a reference accepts. A reference over it is
        // rejected, and a rejected reference fails the prompt, which fails the run -- so a
        // listing or a file read larger than the bound used to end the turn outright instead
        // of costing the model the tail of one result. The marker says the output was cut,
        // because a silently shortened result reads as a complete one.
        //
        // The count is bound the same way the size is. The runtime context accepts at most
        // PromptRuntimeContext.MaxRuntimeReferences in total, and a context over that bound
        // is rejected just as hard -- so a run that had accumulated one reference per tool
        // result eventually ended with prompt_contract_invalid rather than losing anything.
        // Only the newest results are kept: the oldest ones describe work the model has
        // already acted on, and the conversation turns still carry the exchange (truncated)
        // so the model is not told a call it made never happened.
        var toolResults = modelContext.Entries
            .Where(entry => entry.Kind == ModelContextEntryKind.ToolResult
                && !string.IsNullOrWhiteSpace(entry.Content))
            .OrderBy(entry => entry.Sequence)
            .ToList();
        // One slot is reserved for the workspace-activity reference appended below, so a
        // run that is also watching external writers cannot overflow the bound either.
        var newestResults = toolResults.Count <= ToolResultReferenceBudget
            ? toolResults
            : toolResults.GetRange(toolResults.Count - ToolResultReferenceBudget, ToolResultReferenceBudget);
        var omittedResults = toolResults.Count - newestResults.Count;

        var references = new List<PromptReference>();
        if (omittedResults > 0)
        {
            references.Add(new PromptReference(
                "tool_result_omitted",
                "untrusted tool results omitted from the prompt",
                $"{omittedResults} older tool result(s) are not carried here. They are still in "
                + "the conversation history, and the newest results below are complete."));
        }

        references.AddRange(newestResults.Select(entry => new PromptReference(
            "tool_result",
            "untrusted tool result",
            FitReferenceContent(entry.Content!))));

        if (_workspaceCoordinatorStore is not null)
        {
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
                if (external.Length > 0 && references.Count < PromptRuntimeContext.MaxRuntimeReferences)
                {
                    references.Add(new PromptReference(
                        "workspace_activity",
                        "untrusted workspace activity",
                        string.Join("\n", external)));
                }
            }
            catch (HarnessStoreException)
            {
                // Prompt construction remains available if the optional activity
                // journal is unavailable; terminal/file guards remain authoritative.
            }
        }

        // The reservation above is arithmetic, not a guarantee: a third source of
        // references added later would overflow the Core bound silently, and the run would
        // fail with a code that names the prompt contract instead of this provider. Fail
        // here, where the names say what actually went wrong.
        if (references.Count > PromptRuntimeContext.MaxRuntimeReferences)
        {
            throw new InvalidOperationException(
                $"prompt_references_over_core_cap:{references.Count}>{PromptRuntimeContext.MaxRuntimeReferences}");
        }

        return new PromptRuntimeContext(null, null, references);
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
