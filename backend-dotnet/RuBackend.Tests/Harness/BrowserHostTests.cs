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
    public void The_persistent_profile_is_pinned_only_with_the_setting_and_removed_before_providers()
    {
        static RuBackend.Business.Settings.SettingsSnapshot Settings(JsonObject raw) =>
            new(raw, "OpenAI", "model", string.Empty, string.Empty, "model", string.Empty);

        var off = new JsonObject();
        BrowserPolicy.PinNew(off, Settings(new JsonObject()));
        Assert.False(off.ContainsKey(BrowserPolicy.ProfilePinField));

        var on = new JsonObject();
        BrowserPolicy.PinNew(on, Settings(new JsonObject { [BrowserPolicy.ProfileSettingField] = true }));
        Assert.True(BrowserPolicy.IsPersistentProfilePinned(Selection(on.ToJsonString())));
        Assert.False(BrowserPolicy.IsPersistentProfilePinned(Selection("{\"_ru_browser_profile_v1\":true}")));
        Assert.True(BrowserPolicy.TakePinnedCapability(on));
        Assert.Empty(on);
        Assert.ThrowsAny<Exception>(() => BrowserPolicy.TakePinnedCapability(new JsonObject
        {
            [BrowserPolicy.PinField] = true,
            [BrowserPolicy.ProfilePinField] = "yes",
        }));

        var raw = new JsonObject();
        BrowserPolicy.Apply(new JsonObject { [BrowserPolicy.ProfileSettingField] = true }, raw);
        Assert.True(raw[BrowserPolicy.ProfileSettingField]!.GetValue<bool>());
        Assert.Throws<ArgumentException>(() => BrowserPolicy.Apply(new JsonObject { [BrowserPolicy.ProfileSettingField] = "on" }, raw));
    }

    [Fact]
    public void Browser_calls_receive_the_image_input_decision() =>
        Assert.True(InProcessToolGateway.ReturnsImages("browser"));

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
    [InlineData("screenshot", ToolAccessKind.ReadOnly)]
    [InlineData("find", ToolAccessKind.ReadOnly)]
    [InlineData("tabs", ToolAccessKind.ReadOnly)]
    [InlineData("upload", ToolAccessKind.Terminal)]
    [InlineData("select", ToolAccessKind.Terminal)]
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
