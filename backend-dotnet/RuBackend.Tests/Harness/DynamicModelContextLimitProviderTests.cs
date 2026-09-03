using RuAgent.Harness.Core;
using RuBackend.Business.Settings;
using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class DynamicModelContextLimitProviderTests
{
    [Theory]
    [InlineData("claude-3-5-sonnet", 200_000)]
    [InlineData("claude-3-7-sonnet-thought", 200_000)]
    [InlineData("gpt-4o", 128_000)]
    [InlineData("gpt-4.5-preview", 128_000)]
    [InlineData("o1-preview", 128_000)]
    [InlineData("o3-mini", 128_000)]
    [InlineData("gemini-2.0-flash", 1_000_000)]
    [InlineData("deepseek-r1", 65_536)]
    [InlineData("qwen-2.5-coder", 65_536)]
    [InlineData("llama-3-8b", 32_768)]
    public void ResolveLimitForModel_ReturnsExpectedLimits(string model, long expected)
    {
        var limit = DynamicModelContextLimitProvider.ResolveLimitForModel(model);
        Assert.Equal(expected, limit);
    }
}
