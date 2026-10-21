using System.Text.Json.Nodes;
using RuBackend.Providers;
using Xunit;

namespace RuBackend.Tests.Providers;

public sealed class ChatReasoningReplayPolicyTests
{
    private static ProviderSelection Selection(string root) => new("Custom OpenAI", "custom", "chat_completions",
        new Uri(root), "fixture", null);

    [Fact]
    public void Old_pins_and_unknown_gateways_do_not_acquire_a_new_wire_format()
    {
        Assert.False(ChatReasoningReplayPolicy.TakePinnedCapability(new JsonObject(), Selection("https://api.deepseek.com/v1")));
        Assert.False(ChatReasoningReplayPolicy.Supports(Selection("https://gateway.invalid/v1")));
        Assert.False(ChatReasoningReplayPolicy.Supports(Selection("http://api.deepseek.com/v1")));
        Assert.False(ChatReasoningReplayPolicy.Supports(Selection("https://api.deepseek.com/other/v1")));
        Assert.Throws<ProviderCatalogException>(() => ChatReasoningReplayPolicy.TakePinnedCapability(
            new JsonObject { [ChatReasoningReplayPolicy.PinField] = true }, Selection("https://gateway.invalid/v1")));
    }

    [Fact]
    public void Pinned_capability_is_consumed_before_constructing_wire_options()
    {
        var options = new JsonObject { [ChatReasoningReplayPolicy.PinField] = true, ["reasoning_effort"] = "high" };
        Assert.True(ChatReasoningReplayPolicy.TakePinnedCapability(options, Selection("https://api.deepseek.com/v1")));
        Assert.False(options.ContainsKey(ChatReasoningReplayPolicy.PinField));
        Assert.Equal("high", options["reasoning_effort"]!.GetValue<string>());
    }
}
