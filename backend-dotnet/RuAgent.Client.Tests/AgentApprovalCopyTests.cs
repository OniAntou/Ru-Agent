using RuAgent.Client;
using Xunit;

namespace RuAgent.Client.Tests;

public sealed class AgentApprovalCopyTests
{
    [Theory]
    [InlineData(AgentApprovalCopy.ExternalDirectoryRequired, "folder outside the project workspace")]
    [InlineData(AgentApprovalCopy.TerminalDeleteRequiresApproval, "delete files")]
    [InlineData(AgentApprovalCopy.TerminalExternalMutationRequiresApproval, "outside the workspace")]
    [InlineData(AgentApprovalCopy.TerminalTimeoutRequiresApproval, "longer than the standard window")]
    [InlineData(AgentApprovalCopy.WebApprovalRequired, "fetch this web content")]
    [InlineData(AgentApprovalCopy.TerminalUnknownExecutable, "has not recognized the executable")]
    [InlineData(AgentApprovalCopy.TerminalExternalCwdRequiresApproval, "folder outside the workspace")]
    [InlineData(AgentApprovalCopy.TerminalCwdUntrusted, "cannot verify")]
    [InlineData(AgentApprovalCopy.TerminalNetworkRequiresApproval, "network access")]
    [InlineData(AgentApprovalCopy.TerminalExternalArgvRequiresApproval, "points outside the workspace")]
    [InlineData(AgentApprovalCopy.TerminalExternalExecutableRequiresApproval, "executable outside the workspace")]
    [InlineData(AgentApprovalCopy.TerminalUnparsedShellSyntaxRequiresApproval, "cannot fully verify")]
    [InlineData(AgentApprovalCopy.RecognizedExecutableUnclassifiedForm, "not the shape")]
    [InlineData(AgentApprovalCopy.GitSubmoduleRequiresApproval, "submodules")]
    [InlineData(AgentApprovalCopy.GitDirectoryOverrideRequiresApproval, "different directory")]
    [InlineData(AgentApprovalCopy.GitConfigOverrideRequiresApproval, "for one run")]
    [InlineData(AgentApprovalCopy.GitConfigMutationRequiresApproval, "changes git configuration")]
    [InlineData(AgentApprovalCopy.RuleAsks, "project rules")]
    [InlineData(AgentApprovalCopy.McpCallRequiresApproval, "MCP servers")]
    [InlineData(AgentApprovalCopy.ComputerAccessRequiresApproval, "see and control")]
    public void Every_known_ask_reason_has_a_sentence(string code, string fragment)
    {
        Assert.True(AgentApprovalCopy.TryGet(code, out var sentence), code);
        Assert.Contains(fragment, sentence, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('_', sentence);
    }

    [Theory]
    [InlineData("future_reason_code")]
    [InlineData(null)]
    [InlineData("  ")]
    public void An_unknown_reason_falls_back_without_the_token(string? code)
    {
        var sentence = AgentApprovalCopy.Render(code);

        Assert.Equal(AgentApprovalCopy.Generic, sentence);
        Assert.DoesNotContain("future_reason_code", sentence, StringComparison.Ordinal);
        Assert.DoesNotContain('_', sentence);
    }
}
