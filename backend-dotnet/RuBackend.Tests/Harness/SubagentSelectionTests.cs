using RuAgent.Harness.Persistence;
using RuAgent.Protocol.Subagents;
using RuBackend.Harness.Subagents;
using Xunit;
namespace RuBackend.Tests.Harness;
public sealed class SubagentSelectionTests
{
    private static readonly AgentRunProviderSelection Parent = new("OpenAI", "openai", "responses", "https://api.openai.com/v1/",
        "fixture-parent", null, "{}", 4096, CallingMode: "native");

    [Theory]
    [InlineData(SubagentRole.Coding)]
    [InlineData(SubagentRole.Review)]
    public async Task Inheritance_keeps_entire_pin_without_reading_settings_or_catalog(SubagentRole role)
    {
        Assert.Same(Parent, await SubagentSelectionResolver.ResolveAsync(Parent, role, null, null, "",
            (_, _, _) => throw new InvalidOperationException("must not resolve"), default));
    }

    [Theory]
    [InlineData("Do the task", "claude opus 4.5")]
    [InlineData("Use Claude Opus 45", "claude opus 4.5")]
    [InlineData("Use model-x2", "model-x")]
    [InlineData("Use another model", "fixture-coder")]
    public async Task Invented_model_request_does_not_resolve(string prompt, string model)
    {
        var error = await Assert.ThrowsAsync<SubagentConfigurationException>(() =>
            SubagentSelectionResolver.ResolveAsync(Parent, SubagentRole.Coding, model, null, prompt,
                (_, _, _) => throw new InvalidOperationException("must not resolve"), default));
        Assert.Equal("subagent_model_user_request_required", error.Code);
    }

    [Fact]
    public async Task Requested_model_is_pinned_and_never_substituted()
    {
        var selected = Parent with { Model = "claude-opus-4.5" };
        Assert.Same(selected, await SubagentSelectionResolver.ResolveAsync(Parent, SubagentRole.Review,
            "Claude Opus 4.5", null, "Use Claude Opus 4.5 for review", (_, _, _) => Task.FromResult(selected), default));
        var error = await Assert.ThrowsAsync<SubagentConfigurationException>(() =>
            SubagentSelectionResolver.ResolveAsync(Parent, SubagentRole.Review, "Claude Opus 4.5", null,
                "Use Claude Opus 4.5", (_, _, _) => Task.FromResult(Parent), default));
        Assert.Equal("subagent_model_override_mismatch", error.Code);
    }

    [Theory]
    [InlineData("claude-opus-4-5", "Claude Opus 4.5", true)]
    [InlineData("claude-opus-4-5-20251101", "Claude Opus 4.5", true)]
    [InlineData("claude-opus-45", "Claude Opus 4.5", false)]
    [InlineData("gpt-4.1", "GPT 41", false)]
    public void Matching_preserves_model_version_components(string id, string requested, bool matches)
    {
        Assert.Equal(matches, SubagentSelectionResolver.MatchesModel(id, requested));
    }

    [Fact]
    public void Context_is_bounded_and_does_not_implicitly_fork_parent_history()
    {
        var context = SubagentContextBuilder.Build(SubagentRole.Review, "Inspect parsing", "Relevant file: Parser.cs");
        Assert.Contains("Inspect parsing", context);
        Assert.Contains("Relevant file: Parser.cs", context);
        Assert.Throws<SubagentConfigurationException>(() => SubagentContextBuilder.Build(SubagentRole.Review, "task", new string('é', 32769)));
    }
}
