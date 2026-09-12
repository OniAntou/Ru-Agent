using RuApp.Models;
using Xunit;

namespace RuApp.Tests;

public sealed class ProviderModelMappingTests
{
    private static AppSettingsDto Settings() => new()
    {
        OpenCodeModel = "opencode-model",
        KiloModel = "kilo-model",
        OpenAiModel = "openai-model",
        AnthropicModel = "anthropic-model",
        OllamaModel = "ollama-model",
        LmStudioModel = "lmstudio-model",
        CustomModel = "custom-model",
    };

    [Theory]
    [InlineData("opencode", "opencode-model")]
    [InlineData("kilo", "kilo-model")]
    [InlineData("openai", "openai-model")]
    [InlineData("anthropic", "anthropic-model")]
    [InlineData("ollama", "ollama-model")]
    [InlineData("lmstudio", "lmstudio-model")]
    [InlineData("custom", "custom-model")]
    public void GetSelectedModel_ReturnsModelForBackend(string backend, string expected)
    {
        Assert.Equal(expected, ProviderModelSelection.GetSelectedModel(Settings(), backend));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown-backend")]
    public void GetSelectedModel_ReturnsEmpty_ForUnknownBackend(string? backend)
    {
        Assert.Equal(string.Empty, ProviderModelSelection.GetSelectedModel(Settings(), backend));
    }

    [Fact]
    public void GetSelectedModel_ReturnsEmpty_WhenModelMissing()
    {
        Assert.Equal(string.Empty, ProviderModelSelection.GetSelectedModel(new AppSettingsDto(), "opencode"));
    }
}
