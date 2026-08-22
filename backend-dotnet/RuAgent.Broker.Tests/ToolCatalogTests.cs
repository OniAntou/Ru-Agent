using RuAgent.Broker.Policy;
using RuAgent.Broker.Tools;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class ToolCatalogTests
{
    [Fact]
    public void Volume_tool_is_reversible_local_system_write()
    {
        var catalog = new ToolCatalog(new ToolRegistry());
        var manifest = catalog.Registry.Get("system.volume", "1.0.0");

        Assert.Equal(RiskClass.Reversible, manifest.Risk);
        Assert.Contains(ToolScope.SystemWrite, manifest.Scopes);
        Assert.False(manifest.NetworkAllowed);
        Assert.False(manifest.WorkspaceRequired);
        Assert.Equal("desktop", manifest.Adapter);
        Assert.InRange(manifest.TimeoutMs, 1, 30_000);
        Assert.InRange(manifest.MaxOutputBytes, 1, 256 * 1024);
    }

    [Theory]
    [InlineData("system.time", "system.read", RiskClass.Read, false, false)]
    [InlineData("system.status", "system.read", RiskClass.Read, false, false)]
    [InlineData("app.list", "system.read", RiskClass.Read, false, false)]
    [InlineData("files.list", "workspace.read", RiskClass.Read, false, true)]
    [InlineData("files.read", "workspace.read", RiskClass.Read, false, true)]
    [InlineData("system.volume", "system.write", RiskClass.Reversible, false, false)]
    public void First_usable_tool_set_has_explicit_policy(
        string toolId,
        string scope,
        RiskClass risk,
        bool networkAllowed,
        bool workspaceRequired)
    {
        var catalog = new ToolCatalog(new ToolRegistry());
        var manifest = catalog.Registry.Get(toolId, "1.0.0");

        Assert.Equal(risk, manifest.Risk);
        Assert.Contains(scope, manifest.Scopes);
        Assert.Equal(networkAllowed, manifest.NetworkAllowed);
        Assert.Equal(workspaceRequired, manifest.WorkspaceRequired);
        Assert.InRange(manifest.TimeoutMs, 1, 30_000);
        Assert.InRange(manifest.MaxOutputBytes, 1, 256 * 1024);
    }

    [Theory]
    [InlineData("web.search", RiskClass.External)]
    [InlineData("terminal.shell", RiskClass.Destructive)]
    public void External_and_destructive_tools_stay_outside_the_local_reversible_set(
        string toolId,
        RiskClass risk)
    {
        var catalog = new ToolCatalog(new ToolRegistry());
        Assert.Equal(risk, catalog.Registry.Get(toolId, "1.0.0").Risk);
    }
}
