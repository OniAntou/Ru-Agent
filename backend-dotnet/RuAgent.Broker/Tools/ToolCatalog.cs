using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Configuration;
using RuAgent.Protocol;

namespace RuAgent.Broker.Tools;

public sealed class ToolCatalog
{
    private static readonly IReadOnlyList<string> BoundIds =
        AgentToolDefinitionCatalog.Get()
            // ask_user is Host-intercepted before gateway dispatch, so the Broker
            // must never receive it: it deliberately has no manifest or binding.
            .Where(definition => definition.ToolId is not "ask_user")
            .Select(definition => definition.ToolId)
            .ToArray();

    public ToolCatalog(
        IReadOnlyDictionary<string, ToolExecutor> executors,
        WebOptions? webOptions = null)
    {
        ArgumentNullException.ThrowIfNull(executors);
        webOptions ??= new WebOptions();
        var manifests = Manifests(webOptions).ToArray();
        var known = BoundIds.ToHashSet(StringComparer.Ordinal);
        if (executors.Keys.Any(id => !known.Contains(id)))
        {
            throw new InvalidOperationException("unknown_tool_executor");
        }

        if (manifests.Any(manifest => !executors.TryGetValue(manifest.Id, out var executor) || executor is null))
        {
            throw new InvalidOperationException("incomplete_tool_binding");
        }

        // The binding relation must hold in both directions. Without this check the
        // catalog could grow past the manifests and the model would be offered a tool
        // the execution authority has no binding for.
        var boundIds = manifests.Select(manifest => manifest.Id).ToHashSet(StringComparer.Ordinal);
        if (BoundIds.Any(id => !boundIds.Contains(id)))
        {
            throw new InvalidOperationException("incomplete_tool_catalog");
        }

        foreach (var manifest in manifests)
        {
            var definition = AgentToolDefinitionCatalog.Find(manifest.Id, manifest.Version);
            if (definition is null
                || !string.Equals(
                    AgentToolDefinitionCatalog.ComputeSchemaDigest(definition.InputSchema),
                    AgentToolDefinitionCatalog.ComputeSchemaDigest(manifest.InputSchema),
                    StringComparison.Ordinal))
            {
                throw new AgentProtocolException("tool_contract_mismatch");
            }
        }

        Registry = new ToolBindingRegistry();
        foreach (var manifest in manifests)
        {
            var definition = AgentToolDefinitionCatalog.Find(manifest.Id, manifest.Version)
                ?? throw new AgentProtocolException("tool_contract_mismatch");
            Registry.Register(new ToolBinding(
                manifest,
                executors[manifest.Id],
                ModelEnabled: true,
                Availability: "executable",
                VerificationRequired: false));
        }
    }

    public ToolBindingRegistry Registry { get; }

    public bool RequiresApprovalFor(string id)
    {
        var manifest = Registry.Manifests.List().FirstOrDefault(item => item.Id == id)
            ?? throw new KeyNotFoundException("tool_not_found");
        return manifest.Risk is RiskClass.External or RiskClass.Destructive
            || manifest.RequiresApproval;
    }

    private static IEnumerable<ToolManifest> Manifests(WebOptions webOptions)
    {
        yield return Manifest("read", ToolScope.SystemRead, RiskClass.Read, "builtin", workspace: false,
            maxOutputBytes: Files.FileToolLimits.MaxReadOutputBytes);
        yield return Manifest("list", ToolScope.WorkspaceRead, RiskClass.Read, "builtin", workspace: false, extraScopes: [ToolScope.SystemRead]);
        yield return Manifest("help", ToolScope.SystemRead, RiskClass.Read, "builtin", workspace: false);
        yield return Manifest("todo_write", ToolScope.SystemRead, RiskClass.Read, "builtin", workspace: false);
        yield return Manifest("grep", ToolScope.WorkspaceRead, RiskClass.Read, "builtin", workspace: false, extraScopes: [ToolScope.SystemRead]);
        yield return Manifest("glob", ToolScope.WorkspaceRead, RiskClass.Read, "builtin", workspace: false, extraScopes: [ToolScope.SystemRead]);
        yield return Manifest("write", ToolScope.WorkspaceWrite, RiskClass.Reversible, "desktop", workspace: true, requiresApproval: true);
        yield return Manifest("edit", ToolScope.WorkspaceWrite, RiskClass.Reversible, "desktop", workspace: true, requiresApproval: true);
        yield return Manifest("bash", ToolScope.TerminalExecute, RiskClass.Reversible, "terminal", workspace: false, requiresApproval: true);
        yield return Manifest("web_search", ToolScope.WebSearch, RiskClass.External, "web", workspace: false, requiresApproval: true);
        yield return Manifest("web_fetch", ToolScope.WebFetch, RiskClass.External, "web", workspace: false, requiresApproval: true);
        yield return Manifest("skill", ToolScope.SystemRead, RiskClass.Read, "builtin", workspace: false);
        // An MCP server is a user-chosen program run with the user's rights (ADR 0025).
        yield return Manifest("mcp", ToolScope.TerminalExecute, RiskClass.External, "connector", workspace: false, requiresApproval: true);
        foreach (var definition in RuAgent.Protocol.Subagents.SubagentToolDefinitions.Get())
            yield return Manifest(definition.ToolId,
                definition.ToolId == "agent_apply" ? ToolScope.WorkspaceWrite : ToolScope.SystemRead,
                definition.ToolId == "agent_apply" ? RiskClass.Reversible : RiskClass.Read, "builtin",
                workspace: definition.ToolId == "agent_apply", requiresApproval: definition.ToolId == "agent_apply");
    }

    private static ToolManifest Manifest(
        string id,
        string scope,
        RiskClass risk,
        string adapter,
        bool workspace = false,
        bool requiresApproval = false,
        int maxOutputBytes = 4 * 1024 * 1024,
        string[]? extraScopes = null) => new(
        id,
        "1.0.0",
        adapter,
        risk,
        new HashSet<string>([scope, .. extraScopes ?? []], StringComparer.Ordinal),
        AgentToolDefinitionCatalog.Find(id, AgentToolDefinitionCatalog.CurrentVersion)?.InputSchema
            ?? throw new AgentProtocolException("tool_contract_not_found"),
        new JsonObject { ["type"] = "object" },
        30_000,
        maxOutputBytes,
        "builtin",
        WorkspaceRequired: workspace,
        RequiresApproval: requiresApproval);
}
