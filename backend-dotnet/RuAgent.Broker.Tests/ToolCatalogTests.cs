using RuAgent.Broker.Policy;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;
using System.Text.Json.Nodes;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class ToolCatalogTests
{
    [Theory]
    [InlineData("read", "system.read", RiskClass.Read, false, false)]
    [InlineData("list", "workspace.read", RiskClass.Read, false, false)]
    [InlineData("help", "system.read", RiskClass.Read, false, false)]
    public void First_usable_tool_set_has_explicit_policy(
        string toolId,
        string scope,
        RiskClass risk,
        bool networkAllowed,
        bool workspaceRequired)
    {
        var catalog = new ToolCatalog(ToolBindingRegistryTests.ExecutableStubs());
        var manifest = catalog.Registry.Get(toolId, "1.0.0").Manifest;

        Assert.Equal(risk, manifest.Risk);
        Assert.Contains(scope, manifest.Scopes);
        Assert.Equal(networkAllowed, manifest.NetworkAllowed);
        Assert.Equal(workspaceRequired, manifest.WorkspaceRequired);
        Assert.InRange(manifest.TimeoutMs, 1, 30_000);
        Assert.InRange(manifest.MaxOutputBytes, 1, 4 * 1024 * 1024);
    }

    [Theory]
    [InlineData("system.time")]
    [InlineData("system.status")]
    [InlineData("app.list")]
    [InlineData("notes.create")]
    [InlineData("system.volume")]
    [InlineData("files.list")]
    [InlineData("files.read")]
    [InlineData("terminal.run")]
    [InlineData("write")]
    [InlineData("edit")]
    [InlineData("bash")]
    [InlineData("web.search")]
    [InlineData("help.docs")]
    public void Retired_tool_is_not_bound(string toolId)
    {
        var catalog = new ToolCatalog(ToolBindingRegistryTests.ExecutableStubs());

        Assert.Throws<KeyNotFoundException>(() =>
            catalog.Registry.Get(toolId, AgentToolDefinitionCatalog.CurrentVersion));
    }

    [Fact]
    public void Terminal_bindings_are_model_visible_in_agent_but_still_require_approval()
    {
        var catalog = new ToolCatalog(ToolBindingRegistryTests.ExecutableStubs());
        var powershell = catalog.Registry.Get("powershell", "1.0.0");

        Assert.True(powershell.Manifest.RequiresApproval);
        Assert.Equal(RiskClass.Reversible, powershell.Manifest.Risk);
        Assert.False(powershell.VerificationRequired);
        Assert.Equal("executable", powershell.Availability);

        // Advertisement is derived from profile scopes. The single Agent
        // profile includes terminal execution; policy still decides whether
        // each concrete command can run or needs one-time approval.
        var advertised = Enum.GetValues<CapabilityProfile>()
            .Where(profile => catalog.Registry.ModelCapabilities(profile)
                .Any(item => item.Manifest.Id == "powershell"))
            .ToArray();
        Assert.DoesNotContain(CapabilityProfile.Chat, advertised);
        Assert.Contains(CapabilityProfile.Agent, advertised);
    }

    [Fact]
    public void Capability_projection_is_derived_from_complete_bindings()
    {
        var catalog = new ToolCatalog(ToolBindingRegistryTests.ExecutableStubs());

        var capabilities = catalog.Registry.ListCapabilities();
        Assert.Equal(26, capabilities.Count);
        Assert.DoesNotContain(capabilities, item => item.ToolId == "app.open");
        // ask_user is Host-intercepted before gateway dispatch and deliberately has
        // no Broker binding, so it never appears in the Broker's capability surface.
        Assert.DoesNotContain(capabilities, item => item.ToolId == "ask_user");

        foreach (var binding in catalog.Registry.List())
        {
            var definition = AgentToolDefinitionCatalog.Find(binding.Manifest.Id, binding.Manifest.Version)!;
            Assert.True(binding.ModelEnabled);
        }

        var agentModelIds = catalog.Registry.ModelCapabilities(CapabilityProfile.Agent)
            .Select(binding => binding.Manifest.Id)
            .ToHashSet(StringComparer.Ordinal);
        Assert.All(
            new[] { "system.time", "files.read", "terminal.run", "web.search", "help.docs", "write", "edit", "bash" },
            retiredId => Assert.DoesNotContain(retiredId, agentModelIds));
    }

    [Fact]
    public void Modern_coding_schemas_are_separate_and_reject_legacy_discriminators()
    {
        var catalog = new ToolCatalog(ToolBindingRegistryTests.ExecutableStubs());
        var writeSchema = catalog.Registry.Get("write_file", "1.0.0").Manifest.InputSchema;
        ToolSchemaValidator.Validate(writeSchema, new JsonObject { ["path"] = "a.txt", ["content"] = "text" });
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(writeSchema,
            new JsonObject { ["op"] = "write", ["path"] = "a.txt", ["content"] = "text" }));
        var editSchema = catalog.Registry.Get("edit_file", "1.0.0").Manifest.InputSchema;
        ToolSchemaValidator.Validate(editSchema, new JsonObject { ["path"] = "a.txt", ["edits"] = new JsonArray(
            new JsonObject { ["old_string"] = "before", ["new_string"] = "after" }) });
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(editSchema,
            new JsonObject { ["op"] = "edit", ["path"] = "a.txt", ["old_string"] = "before", ["new_string"] = "after" }));

        var powershellSchema = catalog.Registry.Get("powershell", "1.0.0").Manifest.InputSchema;
        ToolSchemaValidator.Validate(powershellSchema, new JsonObject { ["script"] = "Get-Date" });
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(powershellSchema,
            new JsonObject { ["mode"] = "shell", ["script"] = "Get-Date" }));
        var processSchema = catalog.Registry.Get("run_process", "1.0.0").Manifest.InputSchema;
        ToolSchemaValidator.Validate(processSchema, new JsonObject { ["executable"] = "git", ["arguments"] = new JsonArray("status") });
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(processSchema,
            new JsonObject { ["mode"] = "run", ["executable"] = "git", ["arguments"] = new JsonArray("status") }));
    }

    [Fact]
    public void Canonical_contract_returns_fresh_schema_and_example_nodes()
    {
        var first = AgentModelToolContractCatalog.Get("read", "1.0.0");
        var second = AgentModelToolContractCatalog.Get("read", "1.0.0");

        first.InputSchema.Clear();
        first.ExampleArguments!["path"] = "changed";

        Assert.Equal("object", second.InputSchema["type"]!.GetValue<string>());
        Assert.NotEqual("changed", second.ExampleArguments!["path"]!.GetValue<string>());
        ToolSchemaValidator.Validate(second.InputSchema, second.ExampleArguments);
    }

    [Fact]
    public void Read_list_and_help_schemas_are_single_purpose_and_reject_removed_fields()
    {
        var catalog = new ToolCatalog(ToolBindingRegistryTests.ExecutableStubs());

        var readSchema = catalog.Registry.Get("read", "1.0.0").Manifest.InputSchema;
        var readProperties = readSchema["properties"]!.AsObject();
        foreach (var field in new[] { "path", "offset", "limit" })
        {
            Assert.True(readProperties.ContainsKey(field));
        }
        ToolSchemaValidator.Validate(readSchema, new JsonObject { ["path"] = "a.txt" });
        ToolSchemaValidator.Validate(readSchema, new JsonObject { ["path"] = "a.txt", ["offset"] = 0, ["limit"] = 64 });
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(readSchema, new JsonObject()));
        // The discriminator is gone: op is not a declared property any more, so the
        // strict schema rejects it instead of dispatching on it.
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(readSchema, new JsonObject { ["op"] = "read", ["path"] = "a.txt" }));
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(readSchema, new JsonObject { ["path"] = "a.txt", ["limit"] = 4 * 1024 * 1024 + 1 }));
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(readSchema, new JsonObject { ["op"] = "list", ["path"] = "." }));
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(readSchema, new JsonObject { ["op"] = "help", ["query"] = "approvals" }));

        var listSchema = catalog.Registry.Get("list", "1.0.0").Manifest.InputSchema;
        ToolSchemaValidator.Validate(listSchema, new JsonObject());
        ToolSchemaValidator.Validate(listSchema, new JsonObject { ["path"] = "." });
        ToolSchemaValidator.Validate(listSchema, new JsonObject { ["path"] = "src", ["max_entries"] = 8 });
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(listSchema, new JsonObject { ["max_entries"] = 0 }));
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(listSchema, new JsonObject { ["max_entries"] = 513 }));

        var helpSchema = catalog.Registry.Get("help", "1.0.0").Manifest.InputSchema;
        ToolSchemaValidator.Validate(helpSchema, new JsonObject { ["query"] = "approvals" });
        ToolSchemaValidator.Validate(helpSchema, new JsonObject { ["query"] = "approvals", ["max_results"] = 3 });
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(helpSchema, new JsonObject()));
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(helpSchema, new JsonObject { ["query"] = "" }));
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(helpSchema, new JsonObject { ["query"] = "approvals", ["max_results"] = 6 }));

        // Catalog contract schemas match
        var contract = AgentModelToolContractCatalog.Get("read", "1.0.0");
        ToolSchemaValidator.Validate(contract.InputSchema, contract.ExampleArguments ?? new JsonObject());

        var grepSchema = catalog.Registry.Get("grep", "1.0.0").Manifest.InputSchema;
        ToolSchemaValidator.Validate(grepSchema, new JsonObject { ["query"] = "hello" });
        ToolSchemaValidator.Validate(grepSchema, new JsonObject { ["query"] = "hello", ["path"] = ".", ["file_glob"] = "*.cs", ["is_regex"] = true, ["max_results"] = 3 });
        ToolSchemaValidator.Validate(grepSchema, new JsonObject { ["query"] = "hello", ["max_results"] = 128 });
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(grepSchema, new JsonObject { ["path"] = "." }));
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(grepSchema, new JsonObject { ["query"] = "hello", ["max_results"] = 129 }));

        var globSchema = catalog.Registry.Get("glob", "1.0.0").Manifest.InputSchema;
        ToolSchemaValidator.Validate(globSchema, new JsonObject { ["pattern"] = "**/*.cs" });
        ToolSchemaValidator.Validate(globSchema, new JsonObject { ["pattern"] = "**/*.cs", ["path"] = ".", ["max_results"] = 3 });
        ToolSchemaValidator.Validate(globSchema, new JsonObject { ["pattern"] = "**/*.cs", ["max_results"] = 128 });
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(globSchema, new JsonObject { ["path"] = "." }));
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(globSchema, new JsonObject { ["pattern"] = "**/*.cs", ["max_results"] = 129 }));

        var searchSchema = catalog.Registry.Get("web_search", "1.0.0").Manifest.InputSchema;
        ToolSchemaValidator.Validate(searchSchema, new JsonObject { ["query"] = "hello" });
        ToolSchemaValidator.Validate(searchSchema, new JsonObject { ["query"] = "hello", ["max_results"] = 3 });
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(searchSchema, new JsonObject { ["query"] = "hello", ["max_results"] = 99 }));
        var fetchSchema = catalog.Registry.Get("web_fetch", "1.0.0").Manifest.InputSchema;
        ToolSchemaValidator.Validate(fetchSchema, new JsonObject { ["url"] = "https://example.com" });
    }
}
