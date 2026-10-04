using RuBackend.Providers;
using Xunit;

namespace RuBackend.Tests.Providers;

public sealed class ProviderCapabilityRegistryTests
{
    [Fact]
    public void Native_responses_support_is_an_exact_declared_model_capability()
    {
        Assert.True(ProviderCapabilityRegistry.TryGet("openai", "gpt-5.6-luna", out var declared));
        Assert.Equal("openai_responses", declared.Protocol);
        Assert.True(declared.SupportsNativeTools);
        Assert.False(ProviderCapabilityRegistry.TryGet("openai", "gpt-5.6-luna-unknown", out _));
    }

    [Fact]
    public void Native_anthropic_support_is_an_exact_declared_model_capability()
    {
        Assert.True(ProviderCapabilityRegistry.TryGet("anthropic", "claude-fable-5", out var declared));
        Assert.Equal("anthropic_messages", declared.Protocol);
        Assert.True(declared.SupportsNativeTools);
        Assert.False(ProviderCapabilityRegistry.TryGet("anthropic", "claude-fable-5-unknown", out _));
    }
}
