using RuAgent.Broker.Policy;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;
using System.Text.Json.Nodes;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class ToolCatalogTests
{
    [Fact]
    public void Volume_tool_is_reversible_local_system_write()
    {
        var catalog = new ToolCatalog(ToolBindingRegistryTests.ExecutableStubs());
        var binding = catalog.Registry.Get("system.volume", "1.0.0");
        var manifest = binding.Manifest;

        Assert.Equal(RiskClass.Reversible, manifest.Risk);
        Assert.Contains(ToolScope.SystemWrite, manifest.Scopes);
        Assert.False(manifest.NetworkAllowed);
        Assert.False(manifest.WorkspaceRequired);
        Assert.Equal("desktop", manifest.Adapter);
        Assert.InRange(manifest.TimeoutMs, 1, 30_000);
        Assert.InRange(manifest.MaxOutputBytes, 1, 256 * 1024);
        Assert.True(binding.ModelEnabled);
        Assert.True(binding.VerificationRequired);
        Assert.Equal("native_pending", binding.Availability);
    }

    [Theory]
    [InlineData("system.time", "system.read", RiskClass.Read, false, false)]
    [InlineData("system.status", "system.read", RiskClass.Read, false, false)]
    [InlineData("app.list", "system.read", RiskClass.Read, false, false)]
    [InlineData("files.list", "workspace.read", RiskClass.Read, false, true)]
    [InlineData("files.glob", "workspace.read", RiskClass.Read, false, true)]
    [InlineData("files.search", "workspace.read", RiskClass.Read, false, true)]
    [InlineData("files.read", "workspace.read", RiskClass.Read, false, true)]
    [InlineData("system.volume", "system.write", RiskClass.Reversible, false, false)]
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

    [Fact]
    public void Internal_terminal_bindings_are_approval_required_and_full_only()
    {
        var catalog = new ToolCatalog(ToolBindingRegistryTests.ExecutableStubs());
        var run = catalog.Registry.Get("terminal.run", "1.0.0");
        var shell = catalog.Registry.Get("terminal.shell", "1.0.0");

        Assert.True(run.Manifest.RequiresApproval);
        Assert.True(shell.Manifest.RequiresApproval);
        Assert.Equal(RiskClass.Destructive, shell.Manifest.Risk);
        Assert.False(shell.VerificationRequired);
        Assert.Equal("executable", shell.Availability);

        // Advertisement is derived from profile scopes; the destructive shell
        // never surfaces in Chat or Agent and is offered only in Full Access.
        var advertised = Enum.GetValues<CapabilityProfile>()
            .Where(profile => catalog.Registry.ModelCapabilities(profile)
                .Any(item => item.Manifest.Id == "terminal.shell"))
            .ToArray();
        Assert.DoesNotContain(CapabilityProfile.Chat, advertised);
        Assert.DoesNotContain(CapabilityProfile.Agent, advertised);
        Assert.Equal([CapabilityProfile.Full], advertised);
    }

    [Fact]
    public void Capability_projection_is_derived_from_complete_bindings()
    {
        var catalog = new ToolCatalog(ToolBindingRegistryTests.ExecutableStubs());

        var capabilities = catalog.Registry.ListCapabilities();
        var volume = Assert.Single(capabilities, item => item.ToolId == "system.volume");

        Assert.Equal("1.0.0", volume.ToolVersion);
        Assert.Equal(new[] { "system.write" }, volume.Scopes);
        Assert.Equal("reversible", volume.Risk);
        Assert.Equal("native_pending", volume.Availability);
        Assert.True(volume.ModelEnabled);
        Assert.DoesNotContain(capabilities, item => item.ToolId == "app.open");
    }

    [Fact]
    public void Terminal_run_schema_requires_the_canonical_intent_contract_fields()
    {
        var catalog = new ToolCatalog(ToolBindingRegistryTests.ExecutableStubs());
        var runSchema = catalog.Registry.Get("terminal.run", "1.0.0").Manifest.InputSchema;
        var runProperties = runSchema["properties"]!.AsObject();

        Assert.True(runProperties.ContainsKey("executable"));
        Assert.True(runProperties.ContainsKey("arguments"));
        Assert.True(runProperties.ContainsKey("working_directory"));
        Assert.True(runProperties.ContainsKey("timeout_ms"));
        Assert.True(runProperties.ContainsKey("network_intent"));
        Assert.False(runProperties.ContainsKey("shell"));
        Assert.False(runProperties.ContainsKey("network"));

        var timeoutSchema = runProperties["timeout_ms"]!.AsObject();
        Assert.Equal(1, timeoutSchema["minimum"]!.GetValue<int>());
        Assert.Equal(TerminalLimits.HardMaxTimeoutMs, timeoutSchema["maximum"]!.GetValue<int>());
        var argumentsSchema = runProperties["arguments"]!.AsObject();
        Assert.Equal(TerminalLimits.MaxArguments, argumentsSchema["maxItems"]!.GetValue<int>());

        var valid = new JsonObject
        {
            ["executable"] = "git",
            ["arguments"] = new JsonArray("status"),
            ["working_directory"] = ".",
            ["timeout_ms"] = TerminalLimits.DefaultTimeoutMs,
            ["network_intent"] = false,
        };
        ToolSchemaValidator.Validate(runSchema, valid);

        Assert.Equal(
            "invalid_arguments",
            Assert.Throws<ToolSchemaException>(() =>
                ToolSchemaValidator.Validate(runSchema, new JsonObject())).Code);
        Assert.Equal(
            "invalid_arguments",
            Assert.Throws<ToolSchemaException>(() =>
                ToolSchemaValidator.Validate(
                    runSchema,
                    new JsonObject
                    {
                        ["executable"] = "git",
                        ["arguments"] = "status",
                    })).Code);
        // The legacy declarative network field no longer exists.
        Assert.Equal(
            "invalid_arguments",
            Assert.Throws<ToolSchemaException>(() =>
                ToolSchemaValidator.Validate(
                    runSchema,
                    new JsonObject
                    {
                        ["executable"] = "git",
                        ["arguments"] = new JsonArray("status"),
                        ["network"] = true,
                    })).Code);
        Assert.Equal(
            "invalid_arguments",
            Assert.Throws<ToolSchemaException>(() =>
                ToolSchemaValidator.Validate(
                    runSchema,
                    new JsonObject
                    {
                        ["executable"] = "git",
                        ["timeout_ms"] = TerminalLimits.HardMaxTimeoutMs + 1,
                    })).Code);
        Assert.Equal(
            "invalid_arguments",
            Assert.Throws<ToolSchemaException>(() =>
                ToolSchemaValidator.Validate(
                    runSchema,
                    new JsonObject
                    {
                        ["executable"] = "git",
                        ["arguments"] = new JsonArray(
                            Enumerable.Range(0, TerminalLimits.MaxArguments + 1)
                                .Select(i => (JsonNode?)$"a{i}")
                                .ToArray()),
                    })).Code);
        Assert.Equal(
            "invalid_arguments",
            Assert.Throws<ToolSchemaException>(() =>
                ToolSchemaValidator.Validate(
                    runSchema,
                    new JsonObject
                    {
                        ["executable"] = "git",
                        ["arguments"] = new JsonArray(42),
                    })).Code);
    }

    [Fact]
    public void Terminal_shell_schema_is_bounded_powershell_text_without_shell_switching()
    {
        var catalog = new ToolCatalog(ToolBindingRegistryTests.ExecutableStubs());
        var shellSchema = catalog.Registry.Get("terminal.shell", "1.0.0").Manifest.InputSchema;
        var properties = shellSchema["properties"]!.AsObject();

        Assert.True(properties.ContainsKey("script"));
        Assert.True(properties.ContainsKey("working_directory"));
        Assert.True(properties.ContainsKey("timeout_ms"));
        Assert.True(properties.ContainsKey("network_intent"));
        Assert.False(properties.ContainsKey("shell"));
        Assert.Contains("script", shellSchema["required"]!.AsArray().Select(node => node!.GetValue<string>()));

        var scriptSchema = properties["script"]!.AsObject();
        Assert.Equal(TerminalLimits.MaxScriptChars, scriptSchema["maxLength"]!.GetValue<int>());

        var valid = new JsonObject { ["script"] = "Get-Date" };
        ToolSchemaValidator.Validate(shellSchema, valid);

        Assert.Equal(
            "invalid_arguments",
            Assert.Throws<ToolSchemaException>(() =>
                ToolSchemaValidator.Validate(
                    shellSchema,
                    new JsonObject { ["script"] = new string('x', TerminalLimits.MaxScriptChars + 1) })).Code);
        Assert.Equal(
            "invalid_arguments",
            Assert.Throws<ToolSchemaException>(() =>
                ToolSchemaValidator.Validate(
                    shellSchema,
                    new JsonObject
                    {
                        ["script"] = "dir",
                        ["shell_runtime"] = "cmd",
                    })).Code);

        var contract = AgentModelToolContractCatalog.Get("terminal.run", "1.0.0");
        ToolSchemaValidator.Validate(contract.InputSchema, contract.ExampleArguments ?? new JsonObject());
        var shellContract = AgentModelToolContractCatalog.Get("terminal.shell", "1.0.0");
        ToolSchemaValidator.Validate(shellContract.InputSchema, shellContract.ExampleArguments ?? new JsonObject());
    }

    [Fact]
    public void Canonical_contract_returns_fresh_schema_and_example_nodes()
    {
        var first = AgentModelToolContractCatalog.Get("terminal.run", "1.0.0");
        var second = AgentModelToolContractCatalog.Get("terminal.run", "1.0.0");

        first.InputSchema.Clear();
        first.ExampleArguments!["executable"] = "python";

        Assert.Equal("object", second.InputSchema["type"]!.GetValue<string>());
        Assert.Equal("git", second.ExampleArguments!["executable"]!.GetValue<string>());
        ToolSchemaValidator.Validate(second.InputSchema, second.ExampleArguments);
    }

    [Fact]
    public void File_discovery_tools_have_bounded_schemas_and_executable_availability()
    {
        var catalog = new ToolCatalog(ToolBindingRegistryTests.ExecutableStubs());
        foreach (var toolId in new[] { "files.list", "files.glob", "files.search", "files.read" })
        {
            var binding = catalog.Registry.Get(toolId, "1.0.0");
            Assert.Equal("executable", binding.Availability);
            Assert.False(binding.VerificationRequired);
            Assert.True(binding.ModelEnabled);
            Assert.Equal("desktop", binding.Manifest.Adapter);
            Assert.True(binding.Manifest.WorkspaceRequired);
            Assert.Contains(ToolScope.WorkspaceRead, binding.Manifest.Scopes);
            Assert.Equal(RiskClass.Read, binding.Manifest.Risk);
            Assert.InRange(binding.Manifest.TimeoutMs, 1, 30_000);
            Assert.InRange(binding.Manifest.MaxOutputBytes, 1, 256 * 1024);
        }

        // files.list: path optional, max_entries bounded 1..512
        var listSchema = catalog.Registry.Get("files.list", "1.0.0").Manifest.InputSchema;
        Assert.True(listSchema["properties"]!.AsObject().ContainsKey("path"));
        Assert.True(listSchema["properties"]!.AsObject().ContainsKey("max_entries"));
        ToolSchemaValidator.Validate(listSchema, new JsonObject { ["path"] = "." });
        ToolSchemaValidator.Validate(listSchema, new JsonObject { ["path"] = ".", ["max_entries"] = 10 });
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(listSchema, new JsonObject { ["max_entries"] = 0 }));
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(listSchema, new JsonObject { ["max_entries"] = 513 }));

        // files.glob: path optional, pattern required, max_results bounded
        var globSchema = catalog.Registry.Get("files.glob", "1.0.0").Manifest.InputSchema;
        Assert.True(globSchema["properties"]!.AsObject().ContainsKey("pattern"));
        Assert.True(globSchema["properties"]!.AsObject().ContainsKey("max_results"));
        ToolSchemaValidator.Validate(globSchema, new JsonObject { ["pattern"] = "*.txt" });
        ToolSchemaValidator.Validate(globSchema, new JsonObject { ["path"] = ".", ["pattern"] = "*.txt", ["max_results"] = 5 });
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(globSchema, new JsonObject { ["path"] = "." }));
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(globSchema, new JsonObject { ["pattern"] = "*.txt", ["max_results"] = 65 }));

        // files.search: query required, file_glob?, case_sensitive?, max_results bounded
        var searchSchema = catalog.Registry.Get("files.search", "1.0.0").Manifest.InputSchema;
        Assert.True(searchSchema["properties"]!.AsObject().ContainsKey("query"));
        Assert.True(searchSchema["properties"]!.AsObject().ContainsKey("file_glob"));
        Assert.True(searchSchema["properties"]!.AsObject().ContainsKey("case_sensitive"));
        ToolSchemaValidator.Validate(searchSchema, new JsonObject { ["query"] = "hello" });
        ToolSchemaValidator.Validate(searchSchema, new JsonObject { ["path"] = ".", ["query"] = "hello", ["file_glob"] = "*.cs", ["case_sensitive"] = true, ["max_results"] = 10 });
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(searchSchema, new JsonObject { ["path"] = "." }));
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(searchSchema, new JsonObject { ["query"] = "hello", ["max_results"] = 0 }));

        // files.read: path required, offset/limit bounded
        var readSchema = catalog.Registry.Get("files.read", "1.0.0").Manifest.InputSchema;
        Assert.True(readSchema["properties"]!.AsObject().ContainsKey("offset"));
        Assert.True(readSchema["properties"]!.AsObject().ContainsKey("limit"));
        ToolSchemaValidator.Validate(readSchema, new JsonObject { ["path"] = "a.txt" });
        ToolSchemaValidator.Validate(readSchema, new JsonObject { ["path"] = "a.txt", ["offset"] = 0, ["limit"] = 1024 });
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(readSchema, new JsonObject()));
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(readSchema, new JsonObject { ["path"] = "a.txt", ["limit"] = 0 }));
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(readSchema, new JsonObject { ["path"] = "a.txt", ["limit"] = 300 * 1024 }));
        Assert.Throws<ToolSchemaException>(() => ToolSchemaValidator.Validate(readSchema, new JsonObject { ["path"] = "a.txt", ["offset"] = -1 }));

        // Catalog contract schemas match
        foreach (var id in new[] { "files.list", "files.glob", "files.search", "files.read" })
        {
            var contract = AgentModelToolContractCatalog.Get(id, "1.0.0");
            ToolSchemaValidator.Validate(contract.InputSchema, contract.ExampleArguments ?? new JsonObject());
        }
    }

    [Fact]
    public void File_discovery_contracts_match_exact_ids_and_fields()
    {
        var list = AgentModelToolContractCatalog.Get("files.list", "1.0.0");
        Assert.True(list.InputSchema["properties"]!.AsObject().ContainsKey("path"));
        Assert.True(list.InputSchema["properties"]!.AsObject().ContainsKey("max_entries"));

        var glob = AgentModelToolContractCatalog.Get("files.glob", "1.0.0");
        Assert.Equal("object", glob.InputSchema["type"]!.GetValue<string>());
        Assert.Contains("pattern", glob.InputSchema["required"]!.AsArray().Select(n => n!.GetValue<string>()));

        var search = AgentModelToolContractCatalog.Get("files.search", "1.0.0");
        Assert.Contains("query", search.InputSchema["required"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.True(search.InputSchema["properties"]!.AsObject().ContainsKey("file_glob"));

        var read = AgentModelToolContractCatalog.Get("files.read", "1.0.0");
        Assert.Contains("path", read.InputSchema["required"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.True(read.InputSchema["properties"]!.AsObject().ContainsKey("offset"));
        Assert.True(read.InputSchema["properties"]!.AsObject().ContainsKey("limit"));
    }
}
