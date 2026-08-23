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
    public void Terminal_run_schema_requires_the_executor_contract_fields()
    {
        var catalog = new ToolCatalog(ToolBindingRegistryTests.ExecutableStubs());
        var schema = catalog.Registry.Get("terminal.run", "1.0.0").Manifest.InputSchema;
        var valid = new JsonObject
        {
            ["executable"] = "git",
            ["arguments"] = new JsonArray("status"),
            ["working_directory"] = ".",
            ["network"] = false,
        };

        ToolSchemaValidator.Validate(schema, valid);
        Assert.Equal(
            "invalid_arguments",
            Assert.Throws<ToolSchemaException>(() =>
                ToolSchemaValidator.Validate(schema, new JsonObject())).Code);
        Assert.Equal(
            "invalid_arguments",
            Assert.Throws<ToolSchemaException>(() =>
                ToolSchemaValidator.Validate(
                    schema,
                    new JsonObject
                    {
                        ["executable"] = "git",
                        ["arguments"] = "status",
                        ["working_directory"] = ".",
                        ["network"] = false,
                    })).Code);
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
}
