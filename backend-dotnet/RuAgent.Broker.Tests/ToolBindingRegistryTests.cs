using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class ToolBindingRegistryTests
{
    [Fact]
    public void Chat_profile_includes_read_and_web_scopes()
    {
        var ids = CatalogForTests().ModelCapabilities(CapabilityProfile.Chat)
            .Select(binding => binding.Manifest.Id).ToHashSet();

        // list, help, todo_write and skill are read-only, so the Chat profile advertises them too.
        Assert.Equal(
            new HashSet<string> { "read", "read_file", "list", "help", "todo_write", "skill", "grep", "glob", "web_fetch", "web_search" },
            ids);
    }

    /// <summary>
    /// The binding set must equal the canonical catalog minus the Host-intercepted
    /// <c>ask_user</c> (which deliberately has no Broker binding). This is the structural
    /// half of the <c>incomplete_tool_catalog</c> guard in the constructor: the guard
    /// fails closed at runtime, this fails loudly in the suite and names the tool that
    /// went missing. Order is not compared here — <see cref="ToolBindingRegistry.List"/>
    /// sorts by id (ordinal), which is deliberately independent of the catalog's prompt
    /// ordinal.
    /// </summary>
    [Fact]
    public void Binding_set_equals_the_canonical_catalog()
    {
        var bound = new ToolCatalog(ExecutableStubs()).Registry.List()
            .Select(binding => binding.Manifest.Id)
            .ToHashSet(StringComparer.Ordinal);
        var canonical = AgentToolDefinitionCatalog.Get()
            .Where(definition => definition.ToolId is not "ask_user")
            .Select(definition => definition.ToolId)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(canonical, bound);

        // Named per tool so a missing binding reports which tool, not just a count.
        foreach (var definition in AgentToolDefinitionCatalog.Get()
            .Where(definition => definition.ToolId is not "ask_user"))
        {
            Assert.True(
                bound.Contains(definition.ToolId),
                $"The Broker has no binding for the canonical tool '{definition.ToolId}'.");
        }

        Assert.DoesNotContain("ask_user", bound);
    }

    [Fact]
    public void Agent_profile_lists_web_bindings_with_their_own_scopes()
    {
        var registry = CatalogForTests();

        var agent = registry.ListCapabilities(CapabilityProfile.Agent, new HashSet<string>(StringComparer.Ordinal))
            .Select(capability => capability.ToolId).ToHashSet();
        Assert.Contains("web_search", agent);
        Assert.Contains("web_fetch", agent);

        var webSearch = registry.ListCapabilities(CapabilityProfile.Agent).First(capability => capability.ToolId == "web_search");
        var webFetch = registry.ListCapabilities(CapabilityProfile.Agent).First(capability => capability.ToolId == "web_fetch");
        Assert.Contains(RuAgent.Broker.Policy.ToolScope.WebSearch, webSearch.Scopes);
        Assert.Contains(RuAgent.Broker.Policy.ToolScope.WebFetch, webFetch.Scopes);
    }

    [Fact]
    public void Agent_profile_adds_model_visible_mutating_and_terminal_bindings()
    {
        var ids = CatalogForTests().ModelCapabilities(CapabilityProfile.Agent)
            .Select(binding => binding.Manifest.Id).ToHashSet();

        Assert.Contains("write_file", ids);
        Assert.Contains("edit_file", ids);
        Assert.Contains("read", ids);
        Assert.Contains("grep", ids);
        Assert.Contains("glob", ids);
        Assert.Contains("powershell", ids);
        Assert.Contains("run_process", ids);
        Assert.DoesNotContain("system.volume", ids);
        Assert.DoesNotContain("app.list", ids);
    }

    [Fact]
    public void Single_agent_profile_matches_legacy_full_model_bindings()
    {
        var agent = CatalogForTests().ModelCapabilities(CapabilityProfile.Agent)
            .Select(binding => binding.Manifest.Id).ToHashSet();

        Assert.Contains("powershell", agent);
        Assert.Contains("run_process", agent);
    }

    [Fact]
    public void Catalog_fails_when_a_manifest_has_no_executor()
    {
        var executors = ExecutableStubs();
        executors.Remove("read");

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

    /// <summary>
    /// The manifest declarations themselves must stay in the catalog's ordinal order.
    /// The registry cannot observe this — it sorts by id — so this compares the declared
    /// order through the catalog, which is what the prompt ordinal depends on. A tool
    /// declared out of order still binds and still executes, but it silently moves in the
    /// model-facing tool list.
    /// </summary>
    [Fact]
    public void Binding_set_covers_every_canonical_tool()
    {
        var bound = new ToolCatalog(ExecutableStubs()).Registry.List()
            .Select(binding => binding.Manifest.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                  "agent_apply", "agent_cancel", "agent_send", "agent_spawn", "agent_wait", "apply_patch",
                "edit_file", "file_delete", "file_restore", "glob", "grep", "help", "list", "mcp",
                "powershell", "read", "read_file", "run_code", "run_process", "skill", "terminal_read", "terminal_stop", "todo_write", "web_fetch", "web_search", "write_file",
            },
            bound);
        Assert.DoesNotContain("app.open", bound);
        Assert.DoesNotContain("workspace.search", bound);
        Assert.DoesNotContain("web.search", bound);
        Assert.DoesNotContain("mail.send", bound);
    }

    internal static ToolBindingRegistry CatalogForTests() =>
        new ToolCatalog(ExecutableStubs()).Registry;

    internal static Dictionary<string, ToolExecutor> ExecutableStubs() => new(StringComparer.Ordinal)
    {
        ["read"] = Succeeded,
        ["agent_spawn"] = Succeeded,
        ["agent_send"] = Succeeded,
        ["agent_wait"] = Succeeded,
        ["agent_cancel"] = Succeeded,
        ["agent_apply"] = Succeeded,

        ["list"] = Succeeded,
        ["help"] = Succeeded,
        ["todo_write"] = Succeeded,
        ["grep"] = Succeeded,
        ["glob"] = Succeeded,
        ["web_search"] = Succeeded,
        ["web_fetch"] = Succeeded,
        ["skill"] = Succeeded,
        ["mcp"] = Succeeded,
          ["read_file"] = Succeeded,
          ["run_code"] = Succeeded,
          ["apply_patch"] = Succeeded,
          ["write_file"] = Succeeded,
          ["edit_file"] = Succeeded,
          ["file_delete"] = Succeeded,
          ["file_restore"] = Succeeded,
          ["powershell"] = Succeeded,
          ["run_process"] = Succeeded,
          ["terminal_read"] = Succeeded,
          ["terminal_stop"] = Succeeded,
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
