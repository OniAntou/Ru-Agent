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
            run.Preset == AgentPermissionPreset.FullAccess ? "full_access" : "standard",
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

    private async Task<PromptRuntimeContext> BuildRuntimeContextAsync(
        AgentRun run,
        ModelContextSnapshot modelContext,
        CancellationToken cancellationToken)
    {
        // Tool output keeps its full-fidelity channel here as untrusted data;
        // the model conversation additionally carries the exchange history
        // (see LoopbackModelStepRunner.BuildConversation) so the model knows
        // which calls already ran.
        var references = modelContext.Entries
            .Where(entry => entry.Kind == ModelContextEntryKind.ToolResult
                && !string.IsNullOrWhiteSpace(entry.Content))
            .OrderBy(entry => entry.Sequence)
            .Select(entry => new PromptReference(
                "tool_result",
                "untrusted tool result",
                entry.Content!))
            .ToList();

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
                if (external.Length > 0 && references.Count < 16)
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

        return new PromptRuntimeContext(null, null, null, null, references);
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
