using Xunit;

namespace RuAgent.Client.Tests;

public sealed class ProviderPromptTokensTests
{
    [Theory]
    [InlineData("chat_completions", null, 400L)]
    [InlineData("openai_responses", null, 400L)]
    [InlineData("anthropic_messages", null, 650L)]
    [InlineData("anthropic_messages", true, 400L)]
    [InlineData("chat_completions", false, 650L)]
    [InlineData("unknown", true, null)]
    public void Protocol_defaults_and_explicit_cache_convention_do_not_double_count(string protocol, bool? inclusive, long? expected) =>
        Assert.Equal(expected, ProviderPromptTokens.Resolve(protocol, 400, 200, 50, inclusive));

    [Fact]
    public void Missing_zero_and_saturated_counts_remain_distinct()
    {
        Assert.Null(ProviderPromptTokens.Resolve("openai_responses", null, 40, 20));
        Assert.Equal(0, ProviderPromptTokens.Resolve("openai_responses", 0, 40, 20));
        Assert.Equal(60, ProviderPromptTokens.Resolve("anthropic_messages", null, 40, 20));
        Assert.Equal(int.MaxValue, ProviderPromptTokens.Resolve("anthropic_messages", long.MaxValue, long.MaxValue, long.MaxValue));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("invalid")]
    [InlineData("{\"input_tokens\":-1,\"output_tokens\":400}")]
    public void Malformed_or_output_only_usage_cannot_invent_prompt_occupancy(string json)
    {
        Assert.False(ProviderPromptTokens.TryRead(json, "openai_responses", out var tokens, out _));
        Assert.Null(tokens);
    }

    [Fact]
    public void Payload_ignores_cumulative_total_and_inclusive_cache_buckets()
    {
        Assert.True(ProviderPromptTokens.TryRead(
            "{\"input_tokens\":52000,\"output_tokens\":500,\"total_tokens\":103000,\"cache_read_input_tokens\":1000,\"input_tokens_include_cache\":true}",
            "anthropic_messages", out var tokens, out _));
        Assert.Equal(52_000, tokens);
    }
}
