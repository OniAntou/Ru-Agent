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

        Assert.Equal(new HashSet<string> { "files.list", "files.read" }, ids);
    }

    [Fact]
    public void Agent_profile_adds_mutating_terminal_bindings()
    {
        var ids = CatalogForTests().ModelCapabilities(CapabilityProfile.Agent)
            .Select(binding => binding.Manifest.Id).ToHashSet();

        Assert.Contains("files.write", ids);
        Assert.Contains("terminal.run", ids);
        Assert.Contains("terminal.shell", ids);
        Assert.Contains("system.volume", ids);
        Assert.DoesNotContain("app.list", ids);
    }

    [Fact]
    public void Full_profile_matches_agent_advertisement()
    {
        var agent = CatalogForTests().ModelCapabilities(CapabilityProfile.Agent)
            .Select(binding => binding.Manifest.Id).ToHashSet();
        var full = CatalogForTests().ModelCapabilities(CapabilityProfile.Full)
            .Select(binding => binding.Manifest.Id).ToHashSet();

        Assert.Equal(agent, full); // policy differs, ids identical
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
            "app.list", "files.edit", "files.list", "files.read", "files.write", "notes.create",
            "system.status", "system.time", "system.volume", "terminal.run", "terminal.shell",
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
        ["system.time"] = Succeeded,
        ["system.status"] = Succeeded,
        ["system.volume"] = Succeeded,
        ["app.list"] = Succeeded,
        ["files.list"] = Succeeded,
        ["files.read"] = Succeeded,
        ["files.edit"] = Succeeded,
        ["files.write"] = Succeeded,
        ["terminal.run"] = Succeeded,
        ["terminal.shell"] = Succeeded,
        ["notes.create"] = Succeeded,
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
