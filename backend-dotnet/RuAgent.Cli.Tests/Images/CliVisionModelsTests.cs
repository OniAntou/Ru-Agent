using RuAgent.Cli.Images;
using Xunit;

namespace RuAgent.Cli.Tests.Images;

public sealed class CliVisionModelsTests
{
    [Theory]
    [InlineData("gpt-5-codex", true)]
    [InlineData("gpt-4o-mini", true)]
    [InlineData("claude-opus-4-5", true)]
    [InlineData("claude-sonnet-4", true)]
    [InlineData("gemini-2.5-pro", true)]
    [InlineData("qwen-vision-plus", true)]
    [InlineData("llama-3.2-vision:11b", true)]
    [InlineData("deepseek-v3", false)]
    [InlineData("step-3.7-flash:free", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsVisionModel_MatchesTheAdapterAllowlist(string? model, bool expected) =>
        Assert.Equal(expected, CliVisionModels.IsVisionModel(model));
}
