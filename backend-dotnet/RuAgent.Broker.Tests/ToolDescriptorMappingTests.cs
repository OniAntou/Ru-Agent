using RuAgent.Broker.Policy;
using RuAgent.Broker.Tools;
using RuAgent.Broker.Tools.Files;
using RuAgent.Protocol.Tools;
using Xunit;

namespace RuAgent.Broker.Tests;

/// <summary>
/// The descriptor-to-Broker mapping. The enum switches carry a throwing arm (the compiler requires a
/// catch-all for enum switches), so these tests are what proves every member maps.
/// </summary>
public sealed class ToolDescriptorMappingTests
{
    [Fact]
    public void Every_risk_member_maps_to_the_risk_class_of_the_same_name()
    {
        foreach (var risk in Enum.GetValues<AgentToolRisk>())
        {
            Assert.Equal(risk.ToString(), ToolDescriptorMapping.ToRiskClass(risk).ToString());
        }
    }

    [Fact]
    public void Every_adapter_member_except_host_intercepted_maps_to_a_trusted_adapter_name()
    {
        var names = Enum.GetValues<AgentToolAdapter>()
            .Where(adapter => adapter is not AgentToolAdapter.HostIntercepted)
            .Select(ToolDescriptorMapping.ToAdapterName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "builtin", "connector", "desktop", "terminal", "web" }, names);
        Assert.Throws<InvalidOperationException>(() => ToolDescriptorMapping.ToAdapterName(AgentToolAdapter.HostIntercepted));
    }

    [Fact]
    public void Unknown_enum_values_throw_instead_of_mapping_silently()
    {
        Assert.Throws<InvalidOperationException>(() => ToolDescriptorMapping.ToRiskClass((AgentToolRisk)99));
        Assert.Throws<InvalidOperationException>(() => ToolDescriptorMapping.ToAdapterName((AgentToolAdapter)99));
    }

    [Fact]
    public void Output_bounds_mirror_the_broker_adapters_they_describe()
    {
        // Protocol cannot reference the Broker, so each mirrored literal is pinned against its adapter here.
        Assert.Equal(FileReadAdapter.MaximumLineOutputBytes, AgentToolDescriptorTable.Find("read_file")!.MaxOutputBytes);
        Assert.Equal(FileToolLimits.MaxReadOutputBytes, AgentToolDescriptorTable.Find("read")!.MaxOutputBytes ?? 4 * 1024 * 1024);
    }

    [Fact]
    public void Host_intercepted_ask_user_has_a_descriptor_row_but_no_manifest()
    {
        Assert.Equal(AgentToolAdapter.HostIntercepted, AgentToolDescriptorTable.Find("ask_user")!.Adapter);
        Assert.DoesNotContain(
            "ask_user",
            new ToolCatalog(ToolBindingRegistryTests.ExecutableStubs()).Registry.List().Select(binding => binding.Manifest.Id));
    }
}
