using RuAgent.Broker.Policy;
using RuAgent.Broker.Tools;
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
    public void Internal_terminal_binding_is_executable_and_profile_gated()
    {
        var catalog = new ToolCatalog(ToolBindingRegistryTests.ExecutableStubs());
        var binding = catalog.Registry.Get("terminal.shell", "1.0.0");

        Assert.Equal(RiskClass.Destructive, binding.Manifest.Risk);
        Assert.False(binding.VerificationRequired);
        Assert.Equal("executable", binding.Availability);

        // Advertisement is derived from profile scopes; the destructive shell
        // never surfaces in Chat but is offered in Agent and Full Access.
        var advertised = Enum.GetValues<CapabilityProfile>()
            .Where(profile => catalog.Registry.ModelCapabilities(profile)
                .Any(item => item.Manifest.Id == "terminal.shell"))
            .ToArray();
        Assert.DoesNotContain(CapabilityProfile.Chat, advertised);
        Assert.Contains(CapabilityProfile.Agent, advertised);
        Assert.Contains(CapabilityProfile.Full, advertised);
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
}
