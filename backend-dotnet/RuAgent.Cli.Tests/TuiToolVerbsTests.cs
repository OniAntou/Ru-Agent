using RuAgent.Cli.Tui;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiToolVerbsTests
{
    [Theory]
    [InlineData("View", "reading", "read")]
    [InlineData("Read", "reading", "read")]
    [InlineData("Glob", "listing", "listed")]
    [InlineData("Grep", "searching", "searched")]
    [InlineData("Edit", "editing", "edited")]
    [InlineData("Write", "writing", "wrote")]
    [InlineData("Delete", "deleting", "deleted")]
    [InlineData("Terminal", "running", "ran")]
    [InlineData("Bash", "running", "ran")]
    [InlineData("Skill", "loading", "loaded")]
    public void NamedToolsBecomeVerbsAnOperatorWouldSay(string display, string present, string past)
    {
        Assert.Equal(present, TuiToolVerbs.Present(AgentActivityKind.Analyze, display));
        Assert.Equal(past, TuiToolVerbs.Past(AgentActivityKind.Analyze, display));
    }

    [Fact]
    public void TheKindCoversAToolWhoseDisplayNameIsUnrecognised()
    {
        Assert.Equal("searching", TuiToolVerbs.Present(AgentActivityKind.Search, "some_provider_verb"));
        Assert.Equal("searched", TuiToolVerbs.Past(AgentActivityKind.Search, "some_provider_verb"));
    }

    [Fact]
    public void AKnownDisplayNameWinsOverTheKind()
    {
        // A provider naming the tool precisely must not be overruled by a coarse kind.
        Assert.Equal("running", TuiToolVerbs.Present(AgentActivityKind.Edit, "Terminal"));
        Assert.Equal("ran", TuiToolVerbs.Past(AgentActivityKind.Edit, "Terminal"));
    }

    [Fact]
    public void AnUnknownNameKeepsItsOwnNameInsteadOfBeingInvented()
    {
        Assert.Equal("frobnicate", TuiToolVerbs.Past(AgentActivityKind.Narration, "Frobnicate"));
        Assert.Equal("frobnicate", TuiToolVerbs.Present(AgentActivityKind.Narration, "Frobnicate"));
    }

    [Fact]
    public void NoNameAtAllFallsBackToTheKindThenToTool()
    {
        Assert.Equal("read", TuiToolVerbs.Past(AgentActivityKind.Explore, null));
        Assert.Equal("thought", TuiToolVerbs.Past(AgentActivityKind.Thinking, "  "));
        Assert.Equal("tool", TuiToolVerbs.Past(AgentActivityKind.Narration, null));
    }

    [Fact]
    public void EveryKindProducesSomeVerb()
    {
        foreach (var kind in Enum.GetValues<AgentActivityKind>())
        {
            Assert.False(string.IsNullOrWhiteSpace(TuiToolVerbs.Present(kind, null)));
            Assert.False(string.IsNullOrWhiteSpace(TuiToolVerbs.Past(kind, null)));
        }
    }
}
