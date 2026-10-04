using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuBackend.Business.Settings;
using RuBackend.Harness;
using RuBackend.Providers;
using RuAgent.Runtime.Adapters.Bootstrap;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class DynamicModelContextLimitProviderTests
{
    [Fact]
    public async Task Uses_the_run_pinned_context_window_and_output_reserve()
    {
        var resolver = new FixedMetadataResolver(new AgentRunModelMetadata(
            "model-a",
            "chat_completions",
            ProviderToolCallingMode.Native,
            131_072,
            "provider_catalog",
            "catalog-v1",
            8_192));
        var provider = new DynamicModelContextLimitProvider(resolver);

        var limit = await provider.MaxPromptTokensAsync(
            new RunId("run_11111111111111111111111111111111"),
            CancellationToken.None);

        Assert.Equal(122_880, limit);
        Assert.Equal(1, resolver.Calls);
    }

    [Fact]
    public async Task Uses_explicit_conservative_cap_when_the_pinned_window_is_unknown()
    {
        var provider = new DynamicModelContextLimitProvider(new FixedMetadataResolver(new AgentRunModelMetadata(
            "custom-model",
            "chat_completions",
            ProviderToolCallingMode.Native,
            null,
            "unknown",
            "unknown-v1",
            8_192)));

        var limit = await provider.MaxPromptTokensAsync(
            new RunId("run_11111111111111111111111111111111"),
            CancellationToken.None);

        Assert.Equal(DynamicModelContextLimitProvider.ConservativeUnknownContextWindowTokens - 8_192, limit);
    }

    [Fact]
    public async Task Does_not_swallow_provider_metadata_resolution_failures()
    {
        var provider = new DynamicModelContextLimitProvider(new FailingMetadataResolver());

        var error = await Assert.ThrowsAsync<ModelContextLimitResolutionException>(async () =>
            await provider.MaxPromptTokensAsync(
                new RunId("run_11111111111111111111111111111111"),
                CancellationToken.None));

        Assert.Equal("catalog_unauthorized", error.StepErrorCode);
    }

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

    private sealed class FixedMetadataResolver(AgentRunModelMetadata metadata) : IAgentRunModelMetadataResolver
    {
        public int Calls { get; private set; }

        public Task<AgentRunModelMetadata> ResolveAsync(RunId runId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(metadata);
        }
    }

    private sealed class FailingMetadataResolver : IAgentRunModelMetadataResolver
    {
        public Task<AgentRunModelMetadata> ResolveAsync(RunId runId, CancellationToken cancellationToken) =>
            throw new ProviderCatalogException("catalog_unauthorized");
    }
}
