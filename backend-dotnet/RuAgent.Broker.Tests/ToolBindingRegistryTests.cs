using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class ToolBindingRegistryTests
{
    [Fact]
    public void Chat_profile_advertises_only_workspace_read()
    {
        var ids = CatalogForTests().ModelCapabilities(CapabilityProfile.Chat)
            .Select(binding => binding.Manifest.Id).ToHashSet();

        Assert.Equal(new HashSet<string> { "files.list", "files.glob", "files.search", "files.read" }, ids);
    }

    [Fact]
    public void Agent_profile_adds_model_visible_mutating_and_terminal_bindings()
    {
        var ids = CatalogForTests().ModelCapabilities(CapabilityProfile.Agent)
            .Select(binding => binding.Manifest.Id).ToHashSet();

        Assert.Contains("files.write", ids);
        Assert.Contains("help.docs", ids);
        Assert.Contains("terminal.run", ids);
        Assert.Contains("terminal.shell", ids);
        Assert.DoesNotContain("system.volume", ids);
        Assert.DoesNotContain("app.list", ids);
    }

    [Fact]
    public void Full_profile_keeps_the_same_nonweb_model_bindings_as_agent()
    {
        var agent = CatalogForTests().ModelCapabilities(CapabilityProfile.Agent)
            .Select(binding => binding.Manifest.Id).ToHashSet();
        var full = CatalogForTests().ModelCapabilities(CapabilityProfile.Full)
            .Select(binding => binding.Manifest.Id).ToHashSet();

        Assert.True(agent.SetEquals(full));
        Assert.Contains("terminal.run", full);
        Assert.Contains("terminal.shell", full);
        Assert.Contains("terminal.run", agent);
        Assert.Contains("terminal.shell", agent);
    }

    [Fact]
    public void Catalog_fails_when_a_manifest_has_no_executor()
    {
        var executors = ExecutableStubs();
        executors.Remove("files.read");

        var error = Assert.Throws<InvalidOperationException>(() => new ToolCatalog(executors));

        Assert.Equal("incomplete_tool_binding", error.Message);
    }

    [Fact]
    public void Catalog_rejects_an_executor_without_a_manifest()
    {
        var executors = ExecutableStubs();
        executors["app.open"] = Succeeded;

        var error = Assert.Throws<InvalidOperationException>(() => new ToolCatalog(executors));

        Assert.Equal("unknown_tool_executor", error.Message);
    }

    [Fact]
    public void Catalog_contains_only_bound_v1_tools()
    {
        var ids = new ToolCatalog(ExecutableStubs()).Registry.List()
            .Select(binding => binding.Manifest.Id)
            .ToArray();

        Assert.Equal(new[]
        {
            "files.delete", "files.edit", "files.glob", "files.list", "files.read", "files.restore", "files.search", "files.write", "help.docs", "terminal.run", "terminal.shell",
        }, ids);
        Assert.DoesNotContain("app.open", ids);
        Assert.DoesNotContain("workspace.search", ids);
        Assert.DoesNotContain("web.search", ids);
        Assert.DoesNotContain("mail.send", ids);
    }

    internal static ToolBindingRegistry CatalogForTests() =>
        new ToolCatalog(ExecutableStubs()).Registry;

    internal static Dictionary<string, ToolExecutor> ExecutableStubs() => new(StringComparer.Ordinal)
    {
        ["help.docs"] = Succeeded,
        ["files.list"] = Succeeded,
        ["files.glob"] = Succeeded,
        ["files.search"] = Succeeded,
        ["files.read"] = Succeeded,
        ["files.edit"] = Succeeded,
        ["files.write"] = Succeeded,
        ["files.delete"] = Succeeded,
        ["files.restore"] = Succeeded,
        ["terminal.run"] = Succeeded,
        ["terminal.shell"] = Succeeded,
    };

    private static ValueTask<ToolExecutionOutcome> Succeeded(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(ToolExecutionOutcome.Succeeded(
            new JsonObject(),
            ToolVerificationStates.NotRequired,
            "completed"));
}
