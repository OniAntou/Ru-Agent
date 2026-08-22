using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Tools.Desktop;
using RuAgent.Broker.Tools.Terminal;

namespace RuAgent.Broker.Tools;

public sealed class ToolCatalog
{
    public ToolCatalog(ToolRegistry registry)
    {
        Registry = registry ?? throw new ArgumentNullException(nameof(registry));
        RegisterBuiltIns(registry);
    }

    public ToolRegistry Registry { get; }

    private static void RegisterBuiltIns(ToolRegistry registry)
    {
        registry.Register(Manifest("system.time", ToolScope.SystemRead, RiskClass.Read, "builtin"));
        registry.Register(Manifest("system.status", ToolScope.SystemRead, RiskClass.Read, "builtin"));
        registry.Register(VolumeTools.Manifest);
        registry.Register(Manifest("app.list", ToolScope.SystemRead, RiskClass.Read, "desktop"));
        registry.Register(Manifest("app.open", ToolScope.AppOpen, RiskClass.Reversible, "desktop"));
        registry.Register(Manifest("files.list", ToolScope.WorkspaceRead, RiskClass.Read, "desktop", workspace: true));
        registry.Register(Manifest("files.read", ToolScope.WorkspaceRead, RiskClass.Read, "desktop", workspace: true));
        registry.Register(Manifest("files.write", ToolScope.WorkspaceWrite, RiskClass.Reversible, "desktop", workspace: true));
        registry.Register(Manifest("workspace.search", ToolScope.WorkspaceRead, RiskClass.Read, "desktop", workspace: true));
        registry.Register(TerminalManifests.Run);
        registry.Register(TerminalManifests.Shell);
        registry.Register(Manifest("web.search", ToolScope.WebSearch, RiskClass.External, "web"));
        registry.Register(Manifest("web.fetch", ToolScope.WebFetch, RiskClass.External, "web"));
        registry.Register(Manifest("notes.create", ToolScope.WorkspaceWrite, RiskClass.External, "builtin", workspace: true));
        registry.Register(Manifest("calendar.preview_create", ToolScope.CalendarWrite, RiskClass.Read, "connector"));
        registry.Register(Manifest("calendar.create", ToolScope.CalendarWrite, RiskClass.External, "connector"));
        registry.Register(Manifest("tasks.create", ToolScope.TasksWrite, RiskClass.External, "connector"));
        registry.Register(Manifest("tasks.complete", ToolScope.TasksWrite, RiskClass.External, "connector"));
        registry.Register(Manifest("mail.send", ToolScope.MailSend, RiskClass.External, "connector"));
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
