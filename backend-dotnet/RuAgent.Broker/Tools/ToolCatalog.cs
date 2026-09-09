using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Configuration;
using RuAgent.Protocol;

namespace RuAgent.Broker.Tools;

public sealed class ToolCatalog
{
    private static readonly IReadOnlyList<string> BoundIds =
        AgentToolDefinitionCatalog.Get().Select(definition => definition.ToolId).ToArray();

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
        yield return Manifest("read", ToolScope.WorkspaceRead, RiskClass.Read, "builtin", workspace: false, extraScopes: [ToolScope.SystemRead, ToolScope.WebSearch, ToolScope.WebFetch]);
        yield return Manifest("write", ToolScope.WorkspaceWrite, RiskClass.Reversible, "desktop", workspace: true, requiresApproval: true);
        yield return Manifest("edit", ToolScope.WorkspaceWrite, RiskClass.Reversible, "desktop", workspace: true, requiresApproval: true);
        yield return Manifest("bash", ToolScope.TerminalExecute, RiskClass.Reversible, "terminal", workspace: false, requiresApproval: true);
    }

    private static ToolManifest Manifest(
        string id,
        string scope,
        RiskClass risk,
        string adapter,
        bool workspace = false,
        bool requiresApproval = false,
        int maxOutputBytes = 256 * 1024,
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
