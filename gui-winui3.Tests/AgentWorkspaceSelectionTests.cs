using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentWorkspaceSelectionTests
{
    [Theory]
    [InlineData(@"C:\User\Ru Agent")]
    [InlineData(@"C:\User\Ru Agent\")]
    [InlineData(@"D:\LLM")]
    [InlineData(@"C:\")]
    public void New_gui_work_has_no_implicit_project(string applicationRoot)
    {
        Assert.Null(AgentWorkspaceSelection.DefaultForApplication(applicationRoot));
    }

    [Fact]
    public void Harness_keeps_its_explicit_workspace()
    {
        Assert.Equal(@"D:\fixtures", AgentWorkspaceSelection.DefaultForApplication(@"D:\fixtures", isHarness: true));
    }

    [Theory]
    [InlineData("D:\\custom", "D:\\project", "D:\\custom")]
    [InlineData("", "D:\\project", "D:\\project")]
    [InlineData("   ", "D:\\project", "D:\\project")]
    [InlineData(null, "D:\\project", "D:\\project")]
    [InlineData("", "", null)]
    public void Resolve_prefers_requested_workspace_and_falls_back_to_default(
        string? requested,
        string? fallback,
        string? expected)
    {
        Assert.Equal(expected, AgentWorkspaceSelection.Resolve(requested, fallback));
    }
}
