using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence;
using RuAgent.Protocol;
using RuAgent.Protocol.Subagents;
using RuBackend.Harness.Broker;
using RuBackend.Harness.Prompting;
using RuBackend.Providers;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class ComputerUseHostTests
{
    [Fact]
    public void Settings_opt_in_accepts_only_a_boolean()
    {
        var raw = new JsonObject();
        ComputerUsePolicy.Apply(new JsonObject { ["computer_use_enabled"] = true }, raw);
        Assert.True(raw["computer_use_enabled"]!.GetValue<bool>());

        ComputerUsePolicy.Apply(new JsonObject { ["model"] = "x" }, raw);
        Assert.True(raw["computer_use_enabled"]!.GetValue<bool>());

        Assert.Throws<ArgumentException>(() => ComputerUsePolicy.Apply(new JsonObject { ["computer_use_enabled"] = "yes" }, raw));
    }

    [Fact]
    public void The_run_marker_is_removed_before_provider_options_and_read_back_from_the_pin()
    {
        var options = new JsonObject { [ComputerUsePolicy.PinField] = true, ["temperature"] = 0.2 };
        Assert.True(ComputerUsePolicy.TakePinnedCapability(options));
        Assert.False(options.ContainsKey(ComputerUsePolicy.PinField));
        Assert.False(ComputerUsePolicy.TakePinnedCapability(options));
        Assert.ThrowsAny<Exception>(() => ComputerUsePolicy.TakePinnedCapability(new JsonObject { [ComputerUsePolicy.PinField] = false }));

        Assert.True(ComputerUsePolicy.IsPinned(Selection("{\"_ru_computer_use_v1\":true}")));
        Assert.False(ComputerUsePolicy.IsPinned(Selection("{\"_ru_image_input_v1\":true}")));
        Assert.False(ComputerUsePolicy.IsPinned(Selection("not json")));
        Assert.False(ComputerUsePolicy.IsPinned(null));
    }

    [Fact]
    public void The_tool_is_advertised_only_to_a_pinned_root_run()
    {
        static bool Advertised(AgentToolRuntimeAvailability availability) =>
            AgentToolRuntimeProjection.Select(availability).Any(definition => definition.ToolId == "computer");

        var root = new AgentToolRuntimeAvailability(true, true);
        Assert.False(Advertised(root));
        Assert.True(Advertised(root with { ComputerUseEnabled = true }));
        Assert.False(Advertised(root with { ComputerUseEnabled = true, ChildRole = SubagentRole.Coding }));
    }

    [Theory]
    [InlineData("list", ToolAccessKind.ReadOnly)]
    [InlineData("screenshot", ToolAccessKind.ReadOnly)]
    [InlineData("click", ToolAccessKind.Terminal)]
    [InlineData("request_access", ToolAccessKind.Terminal)]
    public void Observation_actions_have_no_side_effects(string action, ToolAccessKind expected) =>
        Assert.Equal(expected, BrokerGatewayContracts.AccessKindForTool("computer", new JsonObject { ["action"] = action }));

    private static AgentRunProviderSelection Selection(string options) =>
        new("OpenAI", "openai", "responses", "https://api.example.com/", "model", null, options, null);
}
