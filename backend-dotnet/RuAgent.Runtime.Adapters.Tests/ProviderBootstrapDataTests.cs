using RuAgent.Runtime.Adapters.Bootstrap;
using System.Text.Json.Nodes;
using Xunit;

namespace RuAgent.Runtime.Adapters.Tests;

/// <summary>
/// Record-validation coverage carried over from the retired
/// <c>ProtectedProviderBootstrapTests</c>. The bootstrap file is gone, but the
/// allowlist and bounds its constructor enforced are still live on every step.
/// </summary>
public sealed class ProviderBootstrapDataTests
{
    [Fact]
    public void Request_options_are_allowlisted_and_cloned()
    {
        var requestOptions = new JsonObject
        {
            ["reasoning"] = new JsonObject
            {
                ["effort"] = "high",
                ["enabled"] = true,
                ["max_tokens"] = 4096,
            },
        };

        var data = new ProviderBootstrapData("Kilo", "chat_completions", new Uri("https://provider.invalid/v1"),
            "model-a",
            string.Empty,
            requestOptions);

        requestOptions["reasoning"]!["effort"] = "low";

        Assert.Equal("high", data.RequestOptions!["reasoning"]!["effort"]!.GetValue<string>());
        Assert.NotSame(requestOptions, data.RequestOptions);
    }

    [Fact]
    public void Empty_request_options_mean_provider_default()
    {
        var data = new ProviderBootstrapData("Zen", "chat_completions", new Uri("https://provider.invalid/v1"),
            "model-a",
            string.Empty,
            new JsonObject());

        Assert.Null(data.RequestOptions);
    }

    [Fact]
    public void Legacy_calling_mode_is_fixed_by_protocol_and_unsupported_native_mode_fails_closed()
    {
        var textSelection = new ProviderBootstrapData(
            "OpenAI", ProviderProtocols.OpenAiResponses, new Uri("https://provider.invalid/v1"),
            "model-a", string.Empty);

        Assert.Equal(ProviderToolCallingMode.TextSelection, textSelection.ToolCallingMode);
        Assert.Throws<ArgumentException>(() => new ProviderBootstrapData(
            "OpenAI", ProviderProtocols.OpenAiResponses, new Uri("https://provider.invalid/v1"),
            "model-a", string.Empty, toolCallingMode: ProviderToolCallingMode.Native));
    }

    [Fact]
    public void Declared_new_responses_native_mode_requires_an_explicit_capability_grant()
    {
        Assert.Throws<ArgumentException>(() => new ProviderBootstrapData(
            "OpenAI", ProviderProtocols.OpenAiResponses, new Uri("https://provider.invalid/v1"),
            "gpt-5.6-luna", string.Empty, toolCallingMode: ProviderToolCallingMode.Native));
        var supported = new ProviderBootstrapData(
            "OpenAI", ProviderProtocols.OpenAiResponses, new Uri("https://provider.invalid/v1"),
            "gpt-5.6-luna", string.Empty, toolCallingMode: ProviderToolCallingMode.Native,
            allowNativeResponses: true);
        Assert.Equal(ProviderToolCallingMode.Native, supported.ToolCallingMode);
        Assert.Equal(ProviderToolCallingMode.TextSelection,
            ProviderBootstrapData.ModeForProtocol(ProviderProtocols.OpenAiResponses));
    }

    [Fact]
    public void Anthropic_native_mode_requires_an_explicit_exact_capability_grant()
    {
        Assert.Throws<ArgumentException>(() => new ProviderBootstrapData(
            "Anthropic", ProviderProtocols.AnthropicMessages, new Uri("https://provider.invalid/v1/messages"),
            "claude-fable-5", string.Empty, maximumOutputTokens: 8192,
            toolCallingMode: ProviderToolCallingMode.Native));
        var supported = new ProviderBootstrapData(
            "Anthropic", ProviderProtocols.AnthropicMessages, new Uri("https://provider.invalid/v1/messages"),
            "claude-fable-5", string.Empty, maximumOutputTokens: 8192,
            toolCallingMode: ProviderToolCallingMode.Native, allowNativeAnthropic: true);

        Assert.Equal(ProviderToolCallingMode.Native, supported.ToolCallingMode);
        Assert.Equal(ProviderToolCallingMode.TextSelection,
            ProviderBootstrapData.ModeForProtocol(ProviderProtocols.AnthropicMessages));
    }

    [Theory]
    [InlineData("effort")]
    [InlineData("enabled")]
    [InlineData("max_tokens")]
    public void Reasoning_options_accept_a_nonempty_validated_subset(string field)
    {
        var reasoning = new JsonObject();
        reasoning[field] = field switch
        {
            "effort" => "high",
            "enabled" => true,
            "max_tokens" => 256,
            _ => throw new InvalidOperationException(),
        };

        var data = new ProviderBootstrapData("Kilo", "chat_completions", new Uri("https://provider.invalid/v1"),
            "model-a",
            string.Empty,
            new JsonObject { ["reasoning"] = reasoning });

        Assert.NotNull(data.RequestOptions);
        Assert.Equal(reasoning.ToJsonString(), data.RequestOptions!["reasoning"]!.ToJsonString());
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("default")]
    public void Request_options_reject_unknown_reasoning_efforts(string effort)
    {
        Assert.Throws<ArgumentException>(() => new ProviderBootstrapData("Kilo", "chat_completions", new Uri("https://provider.invalid/v1"),
            "model-a",
            string.Empty,
            new JsonObject { ["reasoning_effort"] = effort }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1_000_001)]
    public void Request_options_reject_out_of_range_reasoning_tokens(int maxTokens)
    {
        Assert.Throws<ArgumentException>(() => new ProviderBootstrapData("Kilo", "chat_completions", new Uri("https://provider.invalid/v1"),
            "model-a",
            string.Empty,
            new JsonObject
            {
                ["reasoning"] = new JsonObject
                {
                    ["effort"] = "high",
                    ["enabled"] = true,
                    ["max_tokens"] = maxTokens,
                },
            }));
    }

    [Fact]
    public void Request_options_reject_arbitrary_provider_body_fields()
    {
        Assert.Throws<ArgumentException>(() => new ProviderBootstrapData("Kilo", "chat_completions", new Uri("https://provider.invalid/v1"),
            "model-a",
            string.Empty,
            new JsonObject { ["model"] = "other-model" }));
    }
}
