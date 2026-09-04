using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Configuration;
using RuAgent.Broker.Tools.Desktop;
using RuAgent.Broker.Tools.Terminal;
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
            var nativePending = manifest.Id == "system.volume";
            Registry.Register(new ToolBinding(
                manifest,
                executors[manifest.Id],
                ModelEnabled: definition.Exposure.HasFlag(AgentToolExposure.ModelVisible),
                nativePending ? "native_pending" : "executable",
                VerificationRequired: nativePending));
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
        yield return Manifest("system.time", ToolScope.SystemRead, RiskClass.Read, "builtin");
        yield return Manifest("system.status", ToolScope.SystemRead, RiskClass.Read, "builtin");
        yield return Manifest("help.docs", ToolScope.SystemRead, RiskClass.Read, "builtin", maxOutputBytes: 64 * 1024);
        yield return VolumeTools.Manifest;
        yield return Manifest("app.list", ToolScope.SystemRead, RiskClass.Read, "desktop");
        yield return Manifest("files.list", ToolScope.WorkspaceRead, RiskClass.Read, "desktop", workspace: true);
        yield return Manifest("files.glob", ToolScope.WorkspaceRead, RiskClass.Read, "desktop", workspace: true);
        yield return Manifest("files.search", ToolScope.WorkspaceRead, RiskClass.Read, "desktop", workspace: true);
        yield return Manifest("files.read", ToolScope.WorkspaceRead, RiskClass.Read, "desktop", workspace: true);
        yield return Manifest("files.edit", ToolScope.WorkspaceWrite, RiskClass.Reversible, "desktop", workspace: true, requiresApproval: true);
        yield return Manifest("files.write", ToolScope.WorkspaceWrite, RiskClass.Reversible, "desktop", workspace: true, requiresApproval: true);
        yield return Manifest("files.delete", ToolScope.WorkspaceWrite, RiskClass.Destructive, "desktop", workspace: true, requiresApproval: true);
        yield return Manifest("files.restore", ToolScope.WorkspaceWrite, RiskClass.Reversible, "desktop", workspace: true, requiresApproval: true);
        yield return TerminalManifests.Run;
        yield return TerminalManifests.Shell;
        yield return Manifest("notes.create", ToolScope.WorkspaceWrite, RiskClass.External, "builtin", workspace: true, requiresApproval: true);
        if (webOptions.FetchEnabled)
        {
            yield return Manifest("web.fetch", ToolScope.WebFetch, RiskClass.Read, "web", requiresApproval: webOptions.RequireApproval);
        }

        if (webOptions.SearchEnabled)
        {
            yield return Manifest("web.search", ToolScope.WebSearch, RiskClass.Read, "web", requiresApproval: webOptions.RequireApproval);
        }
    }

    private static ToolManifest Manifest(
        string id,
        string scope,
        RiskClass risk,
        string adapter,
        bool workspace = false,
        bool requiresApproval = false,
        int maxOutputBytes = 256 * 1024) => new(
        id,
        "1.0.0",
        adapter,
        risk,
        new HashSet<string>([scope], StringComparer.Ordinal),
        AgentToolDefinitionCatalog.Find(id, AgentToolDefinitionCatalog.CurrentVersion)?.InputSchema
            ?? throw new AgentProtocolException("tool_contract_not_found"),
        new JsonObject { ["type"] = "object" },
        30_000,
        maxOutputBytes,
        "builtin",
        WorkspaceRequired: workspace,
        RequiresApproval: requiresApproval);
}
