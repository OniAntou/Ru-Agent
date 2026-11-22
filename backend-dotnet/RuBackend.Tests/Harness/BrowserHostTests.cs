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

public sealed class BrowserHostTests
{
    [Fact]
    public void The_run_marker_is_removed_before_provider_options_and_read_back_from_the_pin()
    {
        var options = new JsonObject();
        BrowserPolicy.PinNew(options);
        Assert.True(BrowserPolicy.TakePinnedCapability(options));
        Assert.False(options.ContainsKey(BrowserPolicy.PinField));
        Assert.False(BrowserPolicy.TakePinnedCapability(options));
        Assert.ThrowsAny<Exception>(() => BrowserPolicy.TakePinnedCapability(new JsonObject { [BrowserPolicy.PinField] = false }));

        Assert.True(BrowserPolicy.IsPinned(Selection("{\"_ru_browser_v1\":true}")));
        Assert.False(BrowserPolicy.IsPinned(Selection("{\"_ru_computer_use_v1\":true}")));
        Assert.False(BrowserPolicy.IsPinned(Selection("not json")));
        Assert.False(BrowserPolicy.IsPinned(null));
    }

    [Fact]
    public void The_tool_is_advertised_only_to_a_pinned_root_run()
    {
        static bool Advertised(AgentToolRuntimeAvailability availability) =>
            AgentToolRuntimeProjection.Select(availability).Any(definition => definition.ToolId == "browser");

        var root = new AgentToolRuntimeAvailability(true, true);
        Assert.False(Advertised(root));
        Assert.True(Advertised(root with { BrowserEnabled = true }));
        Assert.False(Advertised(root with { BrowserEnabled = true, ChildRole = SubagentRole.Coding }));
        Assert.False(Advertised(root with { BrowserEnabled = true, ChildRole = SubagentRole.Review }));
    }

    [Theory]
    [InlineData("snapshot", ToolAccessKind.ReadOnly)]
    [InlineData("console", ToolAccessKind.ReadOnly)]
    [InlineData("network", ToolAccessKind.ReadOnly)]
    [InlineData("open", ToolAccessKind.Terminal)]
    [InlineData("navigate", ToolAccessKind.Terminal)]
    [InlineData("click", ToolAccessKind.Terminal)]
    [InlineData("type", ToolAccessKind.Terminal)]
    [InlineData("fill", ToolAccessKind.Terminal)]
    [InlineData("press", ToolAccessKind.Terminal)]
    [InlineData("eval", ToolAccessKind.Terminal)]
    [InlineData("wait", ToolAccessKind.Terminal)]
    [InlineData("close", ToolAccessKind.Terminal)]
    public void Observation_actions_have_no_side_effects(string action, ToolAccessKind expected) =>
        Assert.Equal(expected, BrokerGatewayContracts.AccessKindForTool("browser", new JsonObject { ["action"] = action }));

    private static AgentRunProviderSelection Selection(string options) =>
        new("OpenAI", "openai", "responses", "https://api.example.com/", "model", null, options, null);
}
