using Xunit;

namespace RuApp.Tests;

public sealed class AgentSessionUiSourceContractTests
{
    [Fact]
    public void Chat_displays_only_typed_truthful_activity_on_the_existing_surface()
    {
        string codeBehind = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));
        string xaml = File.ReadAllText(FindSource("Views", "ChatView.xaml"));
        string client = File.ReadAllText(FindSource("Services", "ChatWebSocketClient.cs"));
        string serviceSource = File.ReadAllText(FindSource("Services", "AgentSessionService.cs"));
        var oldWaitingCopy = string.Concat("Đang chờ ", "Host cấp quyền…");
        var oldGrantCopy = string.Concat("Quyền đã ", "cấp:");

        Assert.Contains("OnAgentSessionReceived", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Agent mode đã bật", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Đang thực hiện thao tác đã cấp quyền…", codeBehind, StringComparison.Ordinal);
        Assert.Contains("OnAgentActivityReceived(AgentActivityModel activity)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("AgentActivity.Text = activity.DisplayText;", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Action<AgentActivityModel>? OnAgentActivityReceived", client, StringComparison.Ordinal);
        Assert.Contains("AgentActivityModel.TryParse(root, out var activity)", client, StringComparison.Ordinal);
        Assert.DoesNotContain(oldWaitingCopy, codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain(oldGrantCopy, codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentActivity.Text = content", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("workspace and terminal tools", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("_scopes.Add(\"workspace.read\")", serviceSource, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AgentActivity\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Manual_session_switch_resets_agent_state_before_loading_history()
    {
        string codeBehind = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));
        int openSession = codeBehind.IndexOf("public async Task OpenSessionAsync", StringComparison.Ordinal);
        int loadHistory = codeBehind.IndexOf("GetSessionMessagesAsync", openSession, StringComparison.Ordinal);
        string setup = codeBehind[openSession..loadHistory];

        Assert.Contains("_agentSession.ObserveSession(sessionId);", setup, StringComparison.Ordinal);
        Assert.Contains("_agentSession.Disable();", setup, StringComparison.Ordinal);
        Assert.Contains("AgentModeButton.IsChecked = false;", setup, StringComparison.Ordinal);
        Assert.Contains("AgentActivity.Visibility = Visibility.Collapsed;", setup, StringComparison.Ordinal);
        Assert.Contains("ClearAgentApproval();", setup, StringComparison.Ordinal);
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
