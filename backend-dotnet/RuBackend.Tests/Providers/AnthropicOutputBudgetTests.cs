using RuBackend.Providers;
using Xunit;

namespace RuBackend.Tests.Providers;

public sealed class AnthropicOutputBudgetTests
{
    [Theory]
    [InlineData(null, 200_000, 8_192)]
    [InlineData(0, 200_000, 8_192)]
    [InlineData(128_000, 200_000, 32_000)]
    [InlineData(64_000, null, 8_192)]
    [InlineData(8_192, 200_000, 8_192)]
    [InlineData(4_096, 200_000, 4_096)]
    public void A_published_limit_lifts_the_reserve_up_to_the_default(int? modelLimit, int? contextWindow, int expected)
    {
        Assert.Equal(expected, AnthropicOutputBudget.For(modelLimit, contextWindow));
    }

    [Theory]
    [InlineData(64_000, 100_000, 25_000)]
    [InlineData(64_000, 16_000, 8_192)]
    [InlineData(4_096, 8_000, 4_096)]
    public void A_small_context_window_holds_the_reserve_to_a_quarter_but_never_below_the_old_cap(
        int modelLimit, int contextWindow, int expected)
    {
        Assert.Equal(expected, AnthropicOutputBudget.For(modelLimit, contextWindow));
    }
}
