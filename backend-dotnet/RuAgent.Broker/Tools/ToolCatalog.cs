using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Tools.Desktop;
using RuAgent.Broker.Tools.Terminal;

namespace RuAgent.Broker.Tools;

public sealed class ToolCatalog
{
    private static readonly string[] BoundIds =
    [
        "system.time",
        "system.status",
        "system.volume",
        "app.list",
        "files.list",
        "files.read",
        "files.write",
        "terminal.run",
        "terminal.shell",
        "notes.create",
    ];

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
            var modelEnabled = manifest.Id == "system.volume";
            Registry.Register(new ToolBinding(
                manifest,
                executors[manifest.Id],
                modelEnabled,
                modelEnabled ? "native_pending" : "executable",
                VerificationRequired: modelEnabled));
        }
    }

    public ToolBindingRegistry Registry { get; }

    private static IEnumerable<ToolManifest> Manifests()
    {
        yield return Manifest("system.time", ToolScope.SystemRead, RiskClass.Read, "builtin");
        yield return Manifest("system.status", ToolScope.SystemRead, RiskClass.Read, "builtin");
        yield return VolumeTools.Manifest;
        yield return Manifest("app.list", ToolScope.SystemRead, RiskClass.Read, "desktop");
        yield return Manifest("files.list", ToolScope.WorkspaceRead, RiskClass.Read, "desktop", workspace: true);
        yield return Manifest("files.read", ToolScope.WorkspaceRead, RiskClass.Read, "desktop", workspace: true);
        yield return Manifest("files.write", ToolScope.WorkspaceWrite, RiskClass.Reversible, "desktop", workspace: true);
        yield return TerminalManifests.Run;
        yield return TerminalManifests.Shell;
        yield return Manifest("notes.create", ToolScope.WorkspaceWrite, RiskClass.External, "builtin", workspace: true);
    }

    private static ToolManifest Manifest(
        string id,
        string scope,
        RiskClass risk,
        string adapter,
        bool workspace = false) => new(
        id,
        "1.0.0",
        adapter,
        risk,
        new HashSet<string>([scope], StringComparer.Ordinal),
        new JsonObject { ["type"] = "object" },
        new JsonObject { ["type"] = "object" },
        30_000,
        256 * 1024,
        "builtin",
        WorkspaceRequired: workspace);
}
