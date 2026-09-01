using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
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

    public RuPromptContextProvider(
        string workspaceRoot,
        RuInstructionLoader instructionLoader,
        ISettingsStore settings,
        ITimeProvider time,
        bool webToolsEnabled = false)
        : this(workspaceRoot, instructionLoader, settings, time,
            new AgentToolRuntimeAvailability(webToolsEnabled, webToolsEnabled))
    {
    }

    public RuPromptContextProvider(
        string workspaceRoot,
        RuInstructionLoader instructionLoader,
        ISettingsStore settings,
        ITimeProvider time,
        AgentToolRuntimeAvailability availability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _canonicalRoot = WindowsWorkspaceIdentityResolver.GetCanonicalRoot(workspaceRoot);
        _instructionLoader = instructionLoader ?? throw new ArgumentNullException(nameof(instructionLoader));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _availability = availability;
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
        var runtime = BuildRuntimeContext(modelContext);
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
                "Ollama" => settings.OllamaModel,
                "LM Studio" => settings.LmStudioModel,
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

    private static PromptRuntimeContext BuildRuntimeContext(ModelContextSnapshot modelContext)
    {
        var memoryParts = modelContext.Entries
            .Where(entry => entry.Role == ModelContextRole.System
                && entry.Kind == ModelContextEntryKind.Text
                && string.Equals(entry.SourceId, "base:memory", StringComparison.Ordinal))
            .Select(entry => entry.Content)
            .Where(content => !string.IsNullOrWhiteSpace(content))
            .Cast<string>()
            .ToArray();
        var memory = memoryParts.Length == 0 ? null : string.Join("\n\n", memoryParts);

        var references = modelContext.Entries
            .Where(entry => entry.Kind == ModelContextEntryKind.ToolResult
                && !string.IsNullOrWhiteSpace(entry.Content))
            .OrderBy(entry => entry.Sequence)
            .Select(entry => new PromptReference(
                "tool_result",
                "untrusted tool result",
                entry.Content!))
            .ToArray();

        return new PromptRuntimeContext(memory, null, null, null, null, references);
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
