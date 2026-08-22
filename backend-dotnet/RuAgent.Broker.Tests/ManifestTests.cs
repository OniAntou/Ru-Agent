using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Tools;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class ManifestTests
{
    [Fact]
    public void Registry_rejects_duplicate_ids_unknown_scopes_and_untrusted_adapters()
    {
        var registry = new ToolRegistry();
        registry.Register(Manifest("files.list", [ToolScope.WorkspaceRead]));
        Assert.Throws<InvalidOperationException>(() => registry.Register(Manifest("files.list", [ToolScope.WorkspaceRead])));
        Assert.Throws<ArgumentException>(() => registry.Register(Manifest("files.read", ["unknown.scope"])));
        Assert.Throws<ArgumentException>(() => registry.Register(Manifest("files.write", [ToolScope.WorkspaceWrite], adapter: "arbitrary")));
    }

    [Fact]
    public void Registry_rejects_unbounded_limits_and_invalid_versions()
    {
        var registry = new ToolRegistry();
        Assert.Throws<ArgumentException>(() => registry.Register(Manifest("files.list", [ToolScope.WorkspaceRead], version: "1")));
        Assert.Throws<ArgumentException>(() => registry.Register(Manifest("files.read", [ToolScope.WorkspaceRead], timeout: 0)));
        Assert.Throws<ArgumentException>(() => registry.Register(Manifest("files.write", [ToolScope.WorkspaceWrite], output: 8 * 1024 * 1024)));
    }

    private static ToolManifest Manifest(
        string id,
        string[] scopes,
        string adapter = "builtin",
        string version = "1.0.0",
        int timeout = 10_000,
        int output = 64 * 1024) => new(
        id,
        version,
        adapter,
        RiskClass.Read,
        new HashSet<string>(scopes, StringComparer.Ordinal),
        new JsonObject { ["type"] = "object" },
        new JsonObject { ["type"] = "object" },
        timeout,
        output,
        "builtin");
}
