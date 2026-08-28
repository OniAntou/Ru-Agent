using Xunit;

namespace RuApp.Tests;

public sealed class AgentApprovalUiContractTests
{
    [Fact]
    public void Chat_has_bounded_inline_trust_and_approval_surfaces()
    {
        string xaml = File.ReadAllText(FindSource("Views", "ChatView.xaml"));

        foreach (string marker in new[]
        {
            "x:Name=\"AgentTrustPanel\"",
            "x:Name=\"AgentTrustApproveButton\"",
            "x:Name=\"AgentTrustDenyButton\"",
            "x:Name=\"AgentApprovalPanel\"",
            "x:Name=\"AgentApprovalSummaryText\"",
            "x:Name=\"AgentApprovalScopePanel\"",
            "x:Name=\"AgentApprovalScopeBox\"",
            "x:Name=\"AgentApproveButton\"",
            "x:Name=\"AgentDenyButton\"",
            "AutomationProperties.LiveSetting=\"Polite\""
        })
        {
            Assert.Contains(marker, xaml, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("Arguments", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stdout", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("script", xaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Agent_actions_use_the_shared_api_controller_not_chat_websocket()
    {
        string code = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));

        Assert.Contains("ApproveAsync()", code, StringComparison.Ordinal);
        Assert.Contains("DenyAsync()", code, StringComparison.Ordinal);
        Assert.Contains("TrustAsync(approve:", code, StringComparison.Ordinal);
        Assert.Contains("CancelAsync()", code, StringComparison.Ordinal);
        Assert.DoesNotContain("SendAgentApprovalAsync", code, StringComparison.Ordinal);
        Assert.DoesNotContain("agent.approval.response", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Agent_transport_client_remains_chat_only()
    {
        string client = File.ReadAllText(FindSource("Services", "ChatWebSocketClient.cs"));

        Assert.DoesNotContain("mode =", client, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("agent.approval", client, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SendAgentApprovalAsync", client, StringComparison.Ordinal);
    }

    private static string FindSource(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string sourceRoot = Path.Combine(directory.FullName, "gui-winui3");
            if (Directory.Exists(sourceRoot))
            {
                return Path.Combine(new[] { sourceRoot }.Concat(parts).ToArray());
            }
        }

        throw new DirectoryNotFoundException("gui-winui3 source root was not found.");
    }
}
