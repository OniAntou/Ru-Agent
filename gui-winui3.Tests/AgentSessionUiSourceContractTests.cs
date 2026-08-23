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
        Assert.Contains("UpdateModeSelectionVisuals();", setup, StringComparison.Ordinal);
        Assert.Contains("AgentActivity.Visibility = Visibility.Collapsed;", setup, StringComparison.Ordinal);
        Assert.Contains("ClearAgentApproval();", setup, StringComparison.Ordinal);
    }

    [Fact]
    public void Toolbar_offers_three_mode_segments_with_agent_kept_as_a_toggle()
    {
        string xaml = File.ReadAllText(FindSource("Views", "ChatView.xaml"));

        Assert.Contains("x:Name=\"ModeSelector\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ModeChatButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<ToggleButton x:Name=\"ModeAgentButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ModeFullButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"ModeChatButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"AgentModeButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"ModeFullButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnModeChatClick\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnModeAgentClick\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnModeFullClick\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Agent_segment_selecting_agent_observes_session_before_beginning_request()
    {
        string codeBehind = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));
        int clickHandler = codeBehind.IndexOf("private void OnModeAgentClick", StringComparison.Ordinal);
        int nextHandler = codeBehind.IndexOf(
            "private async void OnModeFullClick",
            clickHandler,
            StringComparison.Ordinal);
        string handler = codeBehind[clickHandler..nextHandler];

        int observe = handler.IndexOf(
            "_agentSession.ObserveSession(_activeSessionId);",
            StringComparison.Ordinal);
        int begin = handler.IndexOf(
            "_agentSession.BeginRequest(_activeSessionId, RuAppMode.Agent);",
            StringComparison.Ordinal);

        Assert.True(observe >= 0);
        Assert.True(begin > observe);
    }

    [Fact]
    public void Full_segment_requires_a_one_time_confirmation_and_reverts_on_cancel()
    {
        string codeBehind = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));
        int fullHandler = codeBehind.IndexOf("private async void OnModeFullClick", StringComparison.Ordinal);
        int nextHandler = codeBehind.IndexOf(
            "private void OnAgentSessionReceived",
            fullHandler,
            StringComparison.Ordinal);
        string handler = codeBehind[fullHandler..nextHandler];

        int gate = handler.IndexOf(
            "_agentSession.HasConfirmedFullAccess",
            StringComparison.Ordinal);
        int confirm = handler.IndexOf("_agentSession.ConfirmFullAccess();", StringComparison.Ordinal);
        int beginFull = handler.IndexOf(
            "_agentSession.BeginRequest(_activeSessionId, RuAppMode.Full);",
            StringComparison.Ordinal);

        Assert.True(gate >= 0);
        Assert.True(confirm > gate);
        Assert.True(beginFull > confirm);
        Assert.Contains("Title = \"Full Access\"", handler, StringComparison.Ordinal);
        Assert.Contains("\"Tôi hiểu, bật Full Access\"", handler, StringComparison.Ordinal);
        Assert.Contains("ContentDialogResult.Primary", handler, StringComparison.Ordinal);
        Assert.Contains("ShowFullAccessConfirmationAsync()", handler, StringComparison.Ordinal);
        Assert.Contains("shell tự do", handler, StringComparison.Ordinal);
        Assert.True(
            handler.IndexOf("UpdateModeSelectionVisuals();", StringComparison.Ordinal) < confirm,
            "Cancelling the confirmation must revert the visual selection.");
    }

    [Fact]
    public void Disconnect_and_new_session_reset_the_selector_to_chat()
    {
        string codeBehind = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));
        int disconnected = codeBehind.IndexOf("private void OnWsDisconnected", StringComparison.Ordinal);
        int sessionReceived = codeBehind.IndexOf(
            "private void OnWsSessionReceived",
            disconnected,
            StringComparison.Ordinal);
        int modeChatClick = codeBehind.IndexOf(
            "private void OnModeChatClick",
            sessionReceived,
            StringComparison.Ordinal);

        string disconnectBody = codeBehind[disconnected..sessionReceived];
        string sessionBody = codeBehind[sessionReceived..modeChatClick];

        foreach (string body in new[] { disconnectBody, sessionBody })
        {
            Assert.Contains("_agentSession.Disable();", body, StringComparison.Ordinal);
            Assert.Contains("UpdateModeSelectionVisuals();", body, StringComparison.Ordinal);
            Assert.Contains("ClearAgentApproval();", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Mode_selection_visuals_follow_the_service_mode()
    {
        string codeBehind = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));

        int visuals = codeBehind.IndexOf("private void UpdateModeSelectionVisuals", StringComparison.Ordinal);
        int next = codeBehind.IndexOf("private async void OnModeFullClick", StringComparison.Ordinal);
        Assert.True(visuals >= 0 && next > visuals);
        string body = codeBehind[visuals..next];

        Assert.Contains("ModeAgentButton.IsChecked =", body, StringComparison.Ordinal);
        Assert.Contains("RuAppMode.Agent", body, StringComparison.Ordinal);
        Assert.Contains("RuAppMode.Full", body, StringComparison.Ordinal);
        Assert.Contains("RuAppMode.Chat", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Chat_payloads_send_the_typed_mode_instead_of_agent_mode()
    {
        string client = File.ReadAllText(FindSource("Services", "ChatWebSocketClient.cs"));
        string codeBehind = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));
        string serviceSource = File.ReadAllText(FindSource("Services", "AgentSessionService.cs"));

        Assert.Contains("RuAppMode mode = RuAppMode.Chat)", client, StringComparison.Ordinal);
        Assert.DoesNotContain("agent_mode", client, StringComparison.Ordinal);
        Assert.Contains(
            "SendMessageAsync(text, _activeSessionId, cancellationToken, _agentSession.CurrentMode)",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains("public RuAppMode CurrentMode { get; private set; }", serviceSource, StringComparison.Ordinal);
        Assert.Contains("public enum RuAppMode", serviceSource, StringComparison.Ordinal);
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
