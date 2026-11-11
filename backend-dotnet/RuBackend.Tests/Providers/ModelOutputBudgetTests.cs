using RuBackend.Providers;
using Xunit;

namespace RuBackend.Tests.Providers;

public sealed class ModelOutputBudgetTests
{
    [Theory]
    [InlineData(null, 200_000, 8_192)]
    [InlineData(0, 200_000, 8_192)]
    [InlineData(128_000, 200_000, 50_000)]
    [InlineData(64_000, null, 8_192)]
    [InlineData(8_192, 200_000, 8_192)]
    [InlineData(4_096, 200_000, 4_096)]
    public void A_published_limit_lifts_the_reserve_within_a_quarter_of_the_window(int? modelLimit, int? contextWindow, int expected)
    {
        Assert.Equal(expected, ModelOutputBudget.For(modelLimit, contextWindow));
    }

    [Fact]
    public void A_large_published_limit_on_a_million_token_window_reserves_a_quarter_of_it()
    {
        // e.g. a Models.dev chat-completions reasoning model: limit.output 384,000, context 1,048,576.
        Assert.Equal(Math.Min(262_144, RuAgent.Runtime.Adapters.Models.AnthropicMessagesModelProvider.MaximumSupportedOutputTokens),
            ModelOutputBudget.For(384_000, 1_048_576));
    }

    [Theory]
    [InlineData(64_000, 100_000, 25_000)]
    [InlineData(64_000, 16_000, 8_192)]
    [InlineData(4_096, 8_000, 4_096)]
    public void A_small_context_window_holds_the_reserve_to_a_quarter_but_never_below_the_old_cap(
        int modelLimit, int contextWindow, int expected)
    {
        Assert.Equal(expected, ModelOutputBudget.For(modelLimit, contextWindow));
    }
}
