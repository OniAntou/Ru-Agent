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
            "x:Name=\"AgentApprovalPanel\"",
            "x:Name=\"AgentApprovalSummaryText\"",
            "x:Name=\"AgentApprovalCommandContainer\"",
            "x:Name=\"AgentApprovalCommandText\"",
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
    public void Chat_has_no_trust_panel_or_scope_controls()
    {
        string xaml = File.ReadAllText(FindSource("Views", "ChatView.xaml"));

        Assert.DoesNotContain("AgentTrustPanel", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentApprovalScopeBox", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentApprovalScopePanel", xaml, StringComparison.Ordinal);
        Assert.Contains("AgentApprovalPanel", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Composer_offers_a_bounded_standard_or_trusted_preset()
    {
        string xaml = File.ReadAllText(FindSource("Views", "ChatView.xaml"));
        string code = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));

        Assert.Contains("x:Name=\"AgentPresetBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"AgentPresetBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Agent permission preset\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Tag=\"standard\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Tag=\"trusted\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Tag=\"full_access\"", xaml, StringComparison.Ordinal);

        Assert.Contains("OnAgentPresetChanged", code, StringComparison.Ordinal);
        Assert.Contains("SelectedAgentPreset", code, StringComparison.Ordinal);
        Assert.Contains("ConfirmTrustedAsync", code, StringComparison.Ordinal);
        Assert.Contains("EnsureAgentPresetAsync", code, StringComparison.Ordinal);
        Assert.Contains("preset_version_conflict", code, StringComparison.Ordinal);
        Assert.DoesNotContain("OnAgentTrustApproveClick", code, StringComparison.Ordinal);
        Assert.DoesNotContain("OnAgentTrustDenyClick", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Approval_summary_renders_three_reason_specific_messages()
    {
        string code = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));

        Assert.Contains("external_directory_required", code, StringComparison.Ordinal);
        Assert.Contains("terminal_delete_requires_approval", code, StringComparison.Ordinal);
        Assert.Contains("terminal_external_mutation_requires_approval", code, StringComparison.Ordinal);
        Assert.Contains("terminal_timeout_requires_approval", code, StringComparison.Ordinal);
        Assert.Contains("web_approval_required", code, StringComparison.Ordinal);
        Assert.Contains("terminal_unknown_executable", code, StringComparison.Ordinal);
        Assert.Contains("Ru needs to access a directory", code, StringComparison.Ordinal);
        Assert.Contains("This terminal command will delete files", code, StringComparison.Ordinal);
        Assert.Contains("changes something outside the workspace", code, StringComparison.Ordinal);
        Assert.Contains("Ru has not recognized the executable", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Agent_actions_use_the_shared_api_controller_not_chat_websocket()
    {
        string code = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));

        Assert.Contains("ApproveAsync()", code, StringComparison.Ordinal);
        Assert.Contains("DenyAsync()", code, StringComparison.Ordinal);
        Assert.Contains("CancelAsync()", code, StringComparison.Ordinal);
        Assert.DoesNotContain("SendAgentApprovalAsync", code, StringComparison.Ordinal);
        Assert.DoesNotContain("agent.approval.response", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Agent_transport_client_owns_the_agent_api_and_sse_path()
    {
        string client = File.ReadAllText(FindSource("Services", "AgentRunClientAdapter.cs"));

        Assert.Contains("AgentApiClient", client, StringComparison.Ordinal);
        Assert.Contains("AgentSseClient", client, StringComparison.Ordinal);
        Assert.Contains("SubscribeEventsAsync", client, StringComparison.Ordinal);
        Assert.DoesNotContain("ChatWebSocketClient", client, StringComparison.Ordinal);
        Assert.DoesNotContain("/chat", client, StringComparison.Ordinal);
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
