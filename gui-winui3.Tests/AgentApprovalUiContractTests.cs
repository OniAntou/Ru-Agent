using Xunit;

namespace RuApp.Tests;

public sealed class AgentApprovalUiContractTests
{
    [Fact]
    public void Chat_has_a_compact_approval_card_outside_the_transcript()
    {
        string xaml = File.ReadAllText(FindSource("Views", "ChatView.xaml"));

        Assert.Contains("x:Name=\"AgentApprovalCard\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AgentApprovalActionText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AgentApprovalScopeText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AgentApprovalExpiryText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AgentApproveButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AgentRejectButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"AgentApprovalCard\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"AgentApproveButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"AgentRejectButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Arguments", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("script", xaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Approval_response_uses_a_minimal_control_payload_and_never_enters_messages()
    {
        string client = File.ReadAllText(FindSource("Services", "ChatWebSocketClient.cs"));
        string code = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));

        Assert.Contains("SendAgentApprovalAsync", client, StringComparison.Ordinal);
        Assert.Contains("agent.approval.response", client, StringComparison.Ordinal);
        Assert.Contains("approval_id", client, StringComparison.Ordinal);
        Assert.Contains("approved", client, StringComparison.Ordinal);
        Assert.DoesNotContain("arguments =", client, StringComparison.Ordinal);
        Assert.DoesNotContain("script =", client, StringComparison.Ordinal);
        Assert.Contains("OnAgentApprovalResult", client, StringComparison.Ordinal);
        Assert.Contains("SendAgentApprovalAsync", code, StringComparison.Ordinal);
        Assert.Contains("Messages.Add", code, StringComparison.Ordinal);
        Assert.Contains("ClearAgentApproval", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Approval_card_is_bound_to_the_active_session_and_expires()
    {
        string code = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));

        Assert.Contains("approval.SessionId != _activeSessionId", code, StringComparison.Ordinal);
        Assert.Contains("approval.IsExpired()", code, StringComparison.Ordinal);
        Assert.Contains("AgentApprovalCard.Visibility", code, StringComparison.Ordinal);
        Assert.Contains("AgentApproveButton.IsEnabled", code, StringComparison.Ordinal);
        Assert.Contains("AgentRejectButton.IsEnabled", code, StringComparison.Ordinal);
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
