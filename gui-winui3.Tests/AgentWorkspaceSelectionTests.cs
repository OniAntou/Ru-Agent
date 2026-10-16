using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentWorkspaceSelectionTests
{
    [Theory]
    [InlineData(@"C:\User\Ru Agent", @"C:\User")]
    [InlineData(@"C:\User\Ru Agent\", @"C:\User")]
    [InlineData(@"D:\LLM", @"D:\")]
    [InlineData(@"C:\", @"C:\")]
    public void New_gui_work_uses_application_parent(string applicationRoot, string expected)
    {
        Assert.Equal(expected, AgentWorkspaceSelection.DefaultForApplication(applicationRoot));
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
