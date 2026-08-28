using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentWorkspaceSelectionTests
{
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
