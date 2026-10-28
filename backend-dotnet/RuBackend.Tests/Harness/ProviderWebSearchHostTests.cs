using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Persistence;
using RuAgent.Protocol;
using RuAgent.Protocol.Subagents;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuAgent.Runtime.Adapters.Models;
using RuBackend.Harness.ModelRunner;
using RuBackend.Harness.Prompting;
using RuBackend.Providers;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>Provider-hosted web search on the Host side (ADR 0033).</summary>
public sealed class ProviderWebSearchHostTests
{
    [Theory]
    [InlineData("anthropic", "claude-sonnet-5-5", true)]
    [InlineData("anthropic", "claude-opus-4-1-20250805", true)]
    [InlineData("anthropic", "claude-fable-5-1", true)]
    [InlineData("anthropic", "claude-3-7-sonnet-20250219", false)]
    [InlineData("anthropic", "claude-3-haiku-20240307", false)]
    [InlineData("openai", "gpt-5.5", true)]
    [InlineData("openai", "gpt-5-mini", true)]
    [InlineData("openai", "gpt-4.1", true)]
    [InlineData("openai", "gpt-4o-mini", true)]
    [InlineData("openai", "gpt-5-nano", false)]
    [InlineData("openai", "gpt-4o-audio-preview", false)]
    [InlineData("openai", "o1-pro", false)]
    public void Only_documented_first_party_models_are_eligible(string alias, string model, bool expected) =>
        Assert.Equal(expected, ProviderWebSearchPolicy.Supports(Selection(alias, model), new JsonObject()));

    [Fact]
    public void Gateways_custom_endpoints_and_minimal_reasoning_are_not_eligible()
    {
        Assert.False(ProviderWebSearchPolicy.Supports(
            Selection("anthropic", "claude-sonnet-5-5") with { BaseUri = new Uri("https://gateway.example/") }, new JsonObject()));
        Assert.False(ProviderWebSearchPolicy.Supports(
            new ProviderSelection("Custom OpenAI", "custom", ProviderProtocols.ChatCompletions, new Uri("https://api.openai.com/v1/"), "gpt-5.5", null),
            new JsonObject()));
        Assert.False(ProviderWebSearchPolicy.Supports(Selection("openai", "gpt-5.5"),
            new JsonObject { ["reasoning"] = new JsonObject { ["effort"] = "minimal" } }));
        Assert.True(ProviderWebSearchPolicy.Supports(Selection("openai", "gpt-5.5"),
            new JsonObject { ["reasoning"] = new JsonObject { ["effort"] = "low" } }));
    }

    [Fact]
    public void The_setting_is_on_by_default_and_accepts_only_a_boolean()
    {
        var raw = new JsonObject();
        Assert.True(ProviderWebSearchPolicy.IsEnabled(Settings(raw)));
        ProviderWebSearchPolicy.Apply(new JsonObject { ["provider_web_search_enabled"] = false }, raw);
        Assert.False(ProviderWebSearchPolicy.IsEnabled(Settings(raw)));
        Assert.Throws<ArgumentException>(() => ProviderWebSearchPolicy.Apply(new JsonObject { ["provider_web_search_enabled"] = "no" }, raw));
    }

    [Fact]
    public void The_marker_never_reaches_the_provider_option_validator()
    {
        // Messages accepts at most two option keys and Responses exactly one: a marker
        // left behind would fail every pinned first-party run as provider_selection_invalid.
        var anthropic = new JsonObject { ["thinking"] = new JsonObject { ["type"] = "adaptive", ["display"] = "omitted" }, [ProviderWebSearchPolicy.PinField] = true };
        Assert.True(ProviderWebSearchPolicy.TakePinnedCapability(anthropic));
        Assert.False(anthropic.ContainsKey(ProviderWebSearchPolicy.PinField));
        var openAi = new JsonObject { ["reasoning"] = new JsonObject { ["effort"] = "low" }, [ProviderWebSearchPolicy.PinField] = true };
        Assert.True(ProviderWebSearchPolicy.TakePinnedCapability(openAi));
        Assert.Single(openAi);
        Assert.ThrowsAny<Exception>(() => ProviderWebSearchPolicy.TakePinnedCapability(new JsonObject { [ProviderWebSearchPolicy.PinField] = "yes" }));

        Assert.True(ProviderWebSearchPolicy.IsPinned(Pin("{\"_ru_provider_web_search_v1\":true}")));
        Assert.False(ProviderWebSearchPolicy.IsPinned(Pin("{}")));
        Assert.False(ProviderWebSearchPolicy.IsPinned(Pin("not json")));
    }

    [Fact]
    public void A_provider_search_run_advertises_no_Broker_web_search()
    {
        static bool Advertised(AgentToolRuntimeAvailability availability) =>
            AgentToolRuntimeProjection.Select(availability with { ToolProfile = AgentToolProfiles.Minimal })
                .Any(definition => definition.ToolId == "web_search");

        var root = new AgentToolRuntimeAvailability(true, true);
        Assert.True(Advertised(root));
        Assert.False(Advertised(root with { ProviderWebSearch = true }));
        Assert.True(AgentToolRuntimeProjection.Select(root with { ProviderWebSearch = true, ToolProfile = AgentToolProfiles.Minimal })
            .Any(definition => definition.ToolId == "web_fetch"));
    }

    [Fact]
    public void Anthropic_replay_keeps_search_for_a_search_request_and_drops_it_for_any_other()
    {
        var turns = ToolTurns();
        var continuation = new ProviderContinuation("anthropic", "anthropic_messages", "claude-sonnet-5-5", 1,
        [
            new JsonObject { ["type"] = "server_tool_use", ["id"] = "srvtoolu_done", ["name"] = "web_search", ["input"] = new JsonObject { ["query"] = "q" } },
            new JsonObject
            {
                ["type"] = "web_search_tool_result", ["tool_use_id"] = "srvtoolu_done",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "web_search_result", ["url"] = "https://example.com/", ["title"] = "Example", ["encrypted_content"] = "Eqg" }),
            },
            new JsonObject
            {
                ["type"] = "text", ["text"] = "Found.",
                ["citations"] = new JsonArray(new JsonObject { ["type"] = "web_search_result_location", ["url"] = "https://example.com/", ["title"] = "Example", ["encrypted_index"] = "Eo8", ["cited_text"] = "x" }),
            },
            new JsonObject { ["type"] = "server_tool_use", ["id"] = "srvtoolu_deferred", ["name"] = "web_search", ["input"] = new JsonObject { ["query"] = "later" } },
            new JsonObject { ["type"] = "tool_use", ["id"] = "toolu_1", ["name"] = "read", ["input"] = new JsonObject { ["path"] = "a.txt" } },
        ]);

        static string[] Types(IReadOnlyList<JsonObject> messages) =>
            messages[0]["content"]!.AsArray().Select(block => block!["type"]!.GetValue<string>()).ToArray();

        var searching = AnthropicMessagesHistoryProjector.Project(turns, [continuation], "claude-sonnet-5-5", providerWebSearch: true);
        Assert.Equal(new[] { "server_tool_use", "web_search_tool_result", "text", "tool_use" }, Types(searching));
        Assert.True(searching[0]["content"]![2]!.AsObject().ContainsKey("citations"));

        var other = AnthropicMessagesHistoryProjector.Project(turns, [continuation], "claude-sonnet-5-5");
        Assert.Equal(new[] { "text", "tool_use" }, Types(other));
        Assert.False(other[0]["content"]![0]!.AsObject().ContainsKey("citations"));
    }

    [Fact]
    public void Responses_replay_drops_search_calls_and_their_reasoning_for_other_requests()
    {
        var turns = ToolTurns();
        var continuation = new ProviderContinuation("openai", "openai_responses", "gpt-5.5", 1,
        [
            new JsonObject { ["type"] = "reasoning", ["id"] = "rs_1", ["encrypted_content"] = "opaque", ["summary"] = new JsonArray() },
            new JsonObject { ["type"] = "web_search_call", ["id"] = "ws_1", ["status"] = "completed", ["action"] = new JsonObject { ["type"] = "search", ["query"] = "q" } },
            new JsonObject { ["type"] = "function_call", ["id"] = "fc_1", ["call_id"] = "toolu_1", ["name"] = "read", ["arguments"] = "{\"path\":\"a.txt\"}" },
        ]);

        static string[] Types(IReadOnlyList<JsonObject> items) =>
            items.Select(item => item["type"]?.GetValue<string>() ?? item["role"]!.GetValue<string>()).ToArray();

        var searching = OpenAiResponsesHistoryProjector.Project(turns, [continuation], "gpt-5.5", providerWebSearch: true);
        Assert.Equal(new[] { "reasoning", "web_search_call", "function_call", "function_call_output" }, Types(searching));
        var other = OpenAiResponsesHistoryProjector.Project(turns, [continuation], "gpt-5.5");
        Assert.Equal(new[] { "function_call", "function_call_output" }, Types(other));
    }

    private static ModelTurn[] ToolTurns() =>
    [
        new ModelTurn("assistant", string.Empty, [new ModelFunctionCall("toolu_1", "read", JsonDocument.Parse("{\"path\":\"a.txt\"}").RootElement.Clone())]),
        new ModelTurn("tool", "contents", toolCallId: "toolu_1", toolResultCode: "ok"),
    ];

    private static ProviderSelection Selection(string alias, string model) => alias == "anthropic"
        ? new ProviderSelection("Anthropic", "anthropic", ProviderProtocols.AnthropicMessages, new Uri("https://api.anthropic.com/v1/"), model, "anthropic")
        : new ProviderSelection("OpenAI", "openai", ProviderProtocols.OpenAiResponses, new Uri("https://api.openai.com/v1/"), model, "openai");

    private static AgentRunProviderSelection Pin(string options) =>
        new("OpenAI", "openai", "responses", "https://api.openai.com/v1/", "gpt-5.5", null, options, null);

    private static RuBackend.Business.Settings.SettingsSnapshot Settings(JsonObject raw) =>
        new(raw, "OpenAI", "gpt-5.5", "", "", "gpt-5.5", "");
}
