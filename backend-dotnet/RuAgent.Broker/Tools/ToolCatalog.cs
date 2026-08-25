using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Tools.Desktop;
using RuAgent.Broker.Tools.Terminal;
using RuAgent.Protocol;

namespace RuAgent.Broker.Tools;

public sealed class ToolCatalog
{
    private static readonly IReadOnlyList<string> BoundIds =
        AgentModelToolContractCatalog.ToolIds;

    public ToolCatalog(IReadOnlyDictionary<string, ToolExecutor> executors)
    {
        ArgumentNullException.ThrowIfNull(executors);
        var known = BoundIds.ToHashSet(StringComparer.Ordinal);
        if (executors.Keys.Any(id => !known.Contains(id)))
        {
            throw new InvalidOperationException("unknown_tool_executor");
        }

        if (BoundIds.Any(id => !executors.TryGetValue(id, out var executor) || executor is null))
        {
            throw new InvalidOperationException("incomplete_tool_binding");
        }

        Registry = new ToolBindingRegistry();
        foreach (var manifest in Manifests())
        {
            var nativePending = manifest.Id == "system.volume";
            Registry.Register(new ToolBinding(
                manifest,
                executors[manifest.Id],
                ModelEnabled: true,
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

    private static IEnumerable<ToolManifest> Manifests()
    {
        yield return Manifest("system.time", ToolScope.SystemRead, RiskClass.Read, "builtin");
        yield return Manifest("system.status", ToolScope.SystemRead, RiskClass.Read, "builtin");
        yield return VolumeTools.Manifest;
        yield return Manifest("app.list", ToolScope.SystemRead, RiskClass.Read, "desktop");
        yield return Manifest("files.list", ToolScope.WorkspaceRead, RiskClass.Read, "desktop", workspace: true);
        yield return Manifest("files.glob", ToolScope.WorkspaceRead, RiskClass.Read, "desktop", workspace: true);
        yield return Manifest("files.search", ToolScope.WorkspaceRead, RiskClass.Read, "desktop", workspace: true);
        yield return Manifest("files.read", ToolScope.WorkspaceRead, RiskClass.Read, "desktop", workspace: true);
        yield return Manifest("files.edit", ToolScope.WorkspaceWrite, RiskClass.Reversible, "desktop", workspace: true, requiresApproval: true);
        yield return Manifest("files.write", ToolScope.WorkspaceWrite, RiskClass.Reversible, "desktop", workspace: true, requiresApproval: true);
        yield return TerminalManifests.Run;
        yield return TerminalManifests.Shell;
        yield return Manifest("notes.create", ToolScope.WorkspaceWrite, RiskClass.External, "builtin", workspace: true, requiresApproval: true);
    }

    private static ToolManifest Manifest(
        string id,
        string scope,
        RiskClass risk,
        string adapter,
        bool workspace = false,
        bool requiresApproval = false) => new(
        id,
        "1.0.0",
        adapter,
        risk,
        new HashSet<string>([scope], StringComparer.Ordinal),
        AgentModelToolContractCatalog.Get(id, AgentModelToolContractCatalog.CurrentVersion).InputSchema,
        new JsonObject { ["type"] = "object" },
        30_000,
        256 * 1024,
        "builtin",
        WorkspaceRequired: workspace,
        RequiresApproval: requiresApproval);
}
