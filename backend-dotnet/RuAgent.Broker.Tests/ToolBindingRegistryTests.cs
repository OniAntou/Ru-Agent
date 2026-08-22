using System.Text.Json.Nodes;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class ToolBindingRegistryTests
{
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
            "app.list", "files.list", "files.read", "files.write", "notes.create",
            "system.status", "system.time", "system.volume", "terminal.run", "terminal.shell",
        }, ids);
        Assert.DoesNotContain("app.open", ids);
        Assert.DoesNotContain("workspace.search", ids);
        Assert.DoesNotContain("web.search", ids);
        Assert.DoesNotContain("mail.send", ids);
    }

    internal static Dictionary<string, ToolExecutor> ExecutableStubs() => new(StringComparer.Ordinal)
    {
        ["system.time"] = Succeeded,
        ["system.status"] = Succeeded,
        ["system.volume"] = Succeeded,
        ["app.list"] = Succeeded,
        ["files.list"] = Succeeded,
        ["files.read"] = Succeeded,
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
