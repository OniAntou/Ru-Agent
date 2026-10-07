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
        Assert.InRange(manifest.MaxOutputBytes, 1, 256 * 1024);
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
        var bash = catalog.Registry.Get("bash", "1.0.0");

        Assert.True(bash.Manifest.RequiresApproval);
        Assert.Equal(RiskClass.Reversible, bash.Manifest.Risk);
        Assert.False(bash.VerificationRequired);
        Assert.Equal("executable", bash.Availability);

        // Advertisement is derived from profile scopes. The single Agent
        // profile includes terminal execution; policy still decides whether
        // each concrete command can run or needs one-time approval.
        var advertised = Enum.GetValues<CapabilityProfile>()
            .Where(profile => catalog.Registry.ModelCapabilities(profile)
                .Any(item => item.Manifest.Id == "bash"))
            .ToArray();
        Assert.DoesNotContain(CapabilityProfile.Chat, advertised);
        Assert.Contains(CapabilityProfile.Agent, advertised);
    }

    [Fact]
    public void Capability_projection_is_derived_from_complete_bindings()
    {
        var catalog = new ToolCatalog(ToolBindingRegistryTests.ExecutableStubs());

        var capabilities = catalog.Registry.ListCapabilities();
        Assert.Equal(12, capabilities.Count);
        Assert.DoesNotContain(capabilities, item => item.ToolId == "app.open");

        foreach (var binding in catalog.Registry.List())
        {
            var definition = AgentToolDefinitionCatalog.Find(binding.Manifest.Id, binding.Manifest.Version)!;
            Assert.True(binding.ModelEnabled);
        }

        var agentModelIds = catalog.Registry.ModelCapabilities(CapabilityProfile.Agent)
            .Select(binding => binding.Manifest.Id)
            .ToHashSet(StringComparer.Ordinal);
        Assert.All(
            new[] { "system.time", "files.read", "terminal.run", "web.search", "help.docs" },
            retiredId => Assert.DoesNotContain(retiredId, agentModelIds));
    }

    [Fact]
    public void Bash_schema_requires_mode_and_canonical_intent_contract_fields()
    {
        var catalog = new ToolCatalog(ToolBindingRegistryTests.ExecutableStubs());
        var bashSchema = catalog.Registry.Get("bash", "1.0.0").Manifest.InputSchema;
        var properties = bashSchema["properties"]!.AsObject();

        Assert.True(properties.ContainsKey("mode"));
        Assert.True(properties.ContainsKey("executable"));
        Assert.True(properties.ContainsKey("arguments"));
        Assert.True(properties.ContainsKey("script"));
        Assert.True(properties.ContainsKey("working_directory"));
        Assert.True(properties.ContainsKey("timeout_ms"));
        Assert.True(properties.ContainsKey("network_intent"));
        Assert.True(properties.ContainsKey("background"));
        Assert.False(properties.ContainsKey("shell"));
        Assert.False(properties.ContainsKey("network"));

        var timeoutSchema = properties["timeout_ms"]!.AsObject();
        Assert.Equal(1, timeoutSchema["minimum"]!.GetValue<int>());
        Assert.Equal(TerminalLimits.BackgroundMaxLifetimeMs, timeoutSchema["maximum"]!.GetValue<int>());
        var modeSchema = properties["mode"]!.AsObject();
        Assert.Contains("kill", modeSchema["enum"]!.AsArray().Select(item => item!.GetValue<string>()));
        var argumentsSchema = properties["arguments"]!.AsObject();
        Assert.Equal(TerminalLimits.MaxArguments, argumentsSchema["maxItems"]!.GetValue<int>());

        var valid = new JsonObject
        {
            ["mode"] = "run",
            ["executable"] = "git",
            ["arguments"] = new JsonArray("status"),
            ["working_directory"] = ".",
            ["timeout_ms"] = TerminalLimits.DefaultTimeoutMs,
            ["network_intent"] = false,
        };
        ToolSchemaValidator.Validate(bashSchema, valid);

        Assert.Equal(
            "invalid_arguments",
            Assert.Throws<ToolSchemaException>(() =>
                ToolSchemaValidator.Validate(bashSchema, new JsonObject())).Code);
        Assert.Equal(
            "invalid_arguments",
            Assert.Throws<ToolSchemaException>(() =>
                ToolSchemaValidator.Validate(
                    bashSchema,
                    new JsonObject
                    {
                        ["mode"] = "run",
                        ["executable"] = "git",
                        ["arguments"] = "status",
                    })).Code);
        // The legacy declarative network field no longer exists.
        Assert.Equal(
            "invalid_arguments",
            Assert.Throws<ToolSchemaException>(() =>
                ToolSchemaValidator.Validate(
                    bashSchema,
                    new JsonObject
                    {
                        ["mode"] = "run",
                        ["executable"] = "git",
                        ["arguments"] = new JsonArray("status"),
                        ["network"] = true,
                    })).Code);
        Assert.Equal(
            "invalid_arguments",
            Assert.Throws<ToolSchemaException>(() =>
                ToolSchemaValidator.Validate(
                    bashSchema,
                    new JsonObject
                    {
                        ["mode"] = "run",
                        ["executable"] = "git",
                        ["timeout_ms"] = TerminalLimits.BackgroundMaxLifetimeMs + 1,
                    })).Code);
        Assert.Equal(
            "invalid_arguments",
            Assert.Throws<ToolSchemaException>(() =>
                ToolSchemaValidator.Validate(
                    bashSchema,
                    new JsonObject
                    {
                        ["mode"] = "shell",
                        ["script"] = new string('x', TerminalLimits.MaxScriptChars + 1),
                    })).Code);

        var contract = AgentModelToolContractCatalog.Get("bash", "1.0.0");
        ToolSchemaValidator.Validate(contract.InputSchema, contract.ExampleArguments ?? new JsonObject());
    }

    [Fact]
    public void Canonical_contract_returns_fresh_schema_and_example_nodes()
    {
        var first = AgentModelToolContractCatalog.Get("bash", "1.0.0");
        var second = AgentModelToolContractCatalog.Get("bash", "1.0.0");

        first.InputSchema.Clear();
        first.ExampleArguments!["mode"] = "shell";

        Assert.Equal("object", second.InputSchema["type"]!.GetValue<string>());
        Assert.Equal("run", second.ExampleArguments!["mode"]!.GetValue<string>());
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
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(readSchema, new JsonObject { ["path"] = "a.txt", ["limit"] = 300 * 1024 }));
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
