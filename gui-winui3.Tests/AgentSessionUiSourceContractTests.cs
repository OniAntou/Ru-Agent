using Xunit;

namespace RuApp.Tests;

public sealed class AgentSessionUiSourceContractTests
{
    [Fact]
    public void Chat_surface_exposes_a_compact_agent_mode_inside_the_composer()
    {
        string xaml = File.ReadAllText(FindSource("Views", "ChatView.xaml"));

        Assert.Contains("x:Name=\"AgentModeButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"AgentModeButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AgentModeFlyout\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AgentSettingsButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AgentSettingsFlyout\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AgentModeChatOption\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AgentModeAgentOption\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AgentWorkspaceBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AgentPresetBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AgentStatusText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AgentStopButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AgentContinueButton\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Mode_flyout_contains_only_mode_choices_and_agent_settings_are_separate()
    {
        string xaml = File.ReadAllText(FindSource("Views", "ChatView.xaml"));

        int modeStart = xaml.IndexOf("x:Name=\"AgentModeFlyout\"", StringComparison.Ordinal);
        int settingsStart = xaml.IndexOf("x:Name=\"AgentSettingsFlyout\"", StringComparison.Ordinal);
        Assert.True(modeStart >= 0, "Agent mode flyout is missing.");
        Assert.True(settingsStart > modeStart, "Agent settings flyout must be separate from the mode flyout.");

        string modeBlock = xaml[modeStart..settingsStart];
        Assert.Contains("AgentModeChatOption", modeBlock, StringComparison.Ordinal);
        Assert.Contains("AgentModeAgentOption", modeBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentWorkspaceBox", modeBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentPresetBox", modeBlock, StringComparison.Ordinal);

        string settingsBlock = xaml[settingsStart..];
        Assert.Contains("AgentWorkspaceBox", settingsBlock, StringComparison.Ordinal);
        Assert.Contains("AgentPresetBox", settingsBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void Chat_mode_flyout_and_transcript_viewport_follow_ru_layout_contract()
    {
        string xaml = File.ReadAllText(FindSource("Views", "ChatView.xaml"));
        string codeBehind = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));

        int modeStart = xaml.IndexOf("x:Name=\"AgentModeFlyout\"", StringComparison.Ordinal);
        int settingsStart = xaml.IndexOf("x:Name=\"AgentSettingsFlyout\"", StringComparison.Ordinal);
        Assert.True(modeStart >= 0, "Agent mode flyout is missing.");
        Assert.True(settingsStart > modeStart, "Agent settings flyout must remain separate.");

        string modeBlock = xaml[modeStart..settingsStart];
        Assert.Contains("<Flyout.FlyoutPresenterStyle>", modeBlock, StringComparison.Ordinal);
        Assert.Contains("TargetType=\"FlyoutPresenter\"", modeBlock, StringComparison.Ordinal);
        Assert.Contains("RuSurfaceBrush", modeBlock, StringComparison.Ordinal);
        Assert.Contains("RuLineBrush", modeBlock, StringComparison.Ordinal);
        Assert.Contains("RuAccentSoftBrush", modeBlock, StringComparison.Ordinal);
        Assert.Contains("RadioButtonBackgroundPointerOver", modeBlock, StringComparison.Ordinal);
        Assert.Contains("RadioButtonBackgroundPressed", modeBlock, StringComparison.Ordinal);
        Assert.Contains("Width=\"208\"", modeBlock, StringComparison.Ordinal);
        Assert.Contains("MinHeight=\"38\"", modeBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentWorkspaceBox", modeBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentPresetBox", modeBlock, StringComparison.Ordinal);

        int readingStart = xaml.IndexOf("<Grid x:Name=\"ReadingColumn\"", StringComparison.Ordinal);
        int inputStart = xaml.IndexOf("<Grid x:Name=\"InputContainer\"", readingStart, StringComparison.Ordinal);
        Assert.True(readingStart >= 0, "ReadingColumn is missing.");
        Assert.True(inputStart > readingStart, "InputContainer must remain outside ReadingColumn.");
        string readingBlock = xaml[readingStart..inputStart];
        Assert.Contains("HorizontalAlignment=\"Stretch\"", readingBlock, StringComparison.Ordinal);

        int scrollStart = readingBlock.IndexOf("<ScrollViewer x:Name=\"ChatScrollHost\"", StringComparison.Ordinal);
        Assert.True(scrollStart >= 0, "ChatScrollHost is missing.");
        string scrollBlock = readingBlock[scrollStart..];
        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", scrollBlock, StringComparison.Ordinal);
        Assert.Contains("HorizontalScrollBarVisibility=\"Disabled\"", scrollBlock, StringComparison.Ordinal);
        Assert.Contains("<ScrollViewer.Resources>", scrollBlock, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"ScrollBarThumbFill\"", scrollBlock, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"ScrollBarSize\">8", scrollBlock, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ChatContentGrid\"", scrollBlock, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Center\"", scrollBlock, StringComparison.Ordinal);

        int applyWidthStart = codeBehind.IndexOf("private void ApplyInputWidth", StringComparison.Ordinal);
        Assert.True(applyWidthStart >= 0, "ApplyInputWidth is missing.");
        int nextMethod = codeBehind.IndexOf("\n        private", applyWidthStart + 1, StringComparison.Ordinal);
        string applyWidthBody = nextMethod > applyWidthStart
            ? codeBehind[applyWidthStart..nextMethod]
            : codeBehind[applyWidthStart..];
        Assert.DoesNotContain("ReadingColumn.Width", applyWidthBody, StringComparison.Ordinal);
        Assert.Contains("ChatContentGrid.Width = targetWidth", applyWidthBody, StringComparison.Ordinal);
        Assert.Contains("ChatListView.Width = targetWidth", applyWidthBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Selecting_agent_does_not_show_idle_inline_activity()
    {
        string codeBehind = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));

        int setModeStart = codeBehind.IndexOf("private void SetAgentMode", StringComparison.Ordinal);
        Assert.True(setModeStart >= 0, "SetAgentMode is missing.");
        int nextMethod = codeBehind.IndexOf("\n        private", setModeStart + 1, StringComparison.Ordinal);
        string setModeBody = nextMethod > setModeStart
            ? codeBehind[setModeStart..nextMethod]
            : codeBehind[setModeStart..];

        Assert.Contains("_agentActivityVisible = false", setModeBody, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentStatusText.Text = \"Coding Agent sẵn sàng.\"", setModeBody, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentInlinePanel.Visibility = Visibility.Visible", setModeBody, StringComparison.Ordinal);

        int sendStart = codeBehind.IndexOf("private async Task SendAgentMessageAsync", StringComparison.Ordinal);
        Assert.True(sendStart >= 0, "SendAgentMessageAsync is missing.");
        int nextSendMethod = codeBehind.IndexOf("\n        private async", sendStart + 1, StringComparison.Ordinal);
        string sendBody = nextSendMethod > sendStart
            ? codeBehind[sendStart..nextSendMethod]
            : codeBehind[sendStart..];

        Assert.Contains("ShowAgentInlineStatus(", sendBody, StringComparison.Ordinal);

        int helperStart = codeBehind.IndexOf("private void ShowAgentInlineStatus", StringComparison.Ordinal);
        Assert.True(helperStart >= 0, "ShowAgentInlineStatus is missing.");
        int nextHelperMethod = codeBehind.IndexOf("\n        private", helperStart + 1, StringComparison.Ordinal);
        string helperBody = nextHelperMethod > helperStart
            ? codeBehind[helperStart..nextHelperMethod]
            : codeBehind[helperStart..];
        Assert.Contains("_agentActivityVisible = true", helperBody, StringComparison.Ordinal);
        Assert.Contains("AgentInlinePanel.Visibility = Visibility.Visible", helperBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Chat_code_behind_routes_agent_prompt_through_the_shared_controller()
    {
        string codeBehind = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));

        Assert.Contains("AgentRunController", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CreateAgentRunRequest", codeBehind, StringComparison.Ordinal);
        Assert.Contains("SendAgentMessageAsync", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_agentController.CreateAndRunAsync", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_agentWorkspace = workspace;", codeBehind, StringComparison.Ordinal);
        Assert.Contains("AgentWorkspaceSelection.Resolve", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_defaultAgentWorkspace", codeBehind, StringComparison.Ordinal);
        Assert.Contains("AgentSettingsFlyout.ShowAt", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("Cần nhập thư mục làm việc cho Coding Agent.", codeBehind, StringComparison.Ordinal);

        string xaml = File.ReadAllText(FindSource("Views", "ChatView.xaml"));
        Assert.Contains("Thư mục làm việc (tuỳ chọn)", xaml, StringComparison.Ordinal);
        Assert.Contains("Để trống để dùng project root", xaml, StringComparison.Ordinal);

        int agentSend = codeBehind.IndexOf("private async Task SendAgentMessageAsync", StringComparison.Ordinal);
        Assert.True(agentSend >= 0);
        int nextMethod = codeBehind.IndexOf("private async Task SendVoiceMessageAsync", agentSend, StringComparison.Ordinal);
        Assert.True(nextMethod > agentSend);
        string agentBody = codeBehind[agentSend..nextMethod];
        Assert.DoesNotContain("_wsClient.SendMessageAsync", agentBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Session_and_transport_reset_clear_agent_state()
    {
        string codeBehind = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));

        foreach (string method in new[] { "OnWsSessionReceived", "OnWsResetReceived", "OnWsDisconnected", "OpenSessionAsync" })
        {
            string signature = method == "OpenSessionAsync"
                ? "public async Task OpenSessionAsync"
                : $"private void {method}";
            int start = codeBehind.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, $"Missing {method} handler.");
            int next = codeBehind.IndexOf("\n        private", start + method.Length, StringComparison.Ordinal);
            string body = next > start ? codeBehind[start..next] : codeBehind[start..];
            Assert.Contains("ResetAgentState", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Session_reset_detaches_and_cancels_an_active_agent_run()
    {
        string codeBehind = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));
        int resetStart = codeBehind.IndexOf("private void ResetAgentState", StringComparison.Ordinal);
        Assert.True(resetStart >= 0);
        int nextMethod = codeBehind.IndexOf("\n        private", resetStart + 1, StringComparison.Ordinal);
        string resetBody = nextMethod > resetStart ? codeBehind[resetStart..nextMethod] : codeBehind[resetStart..];

        Assert.Contains("_agentController?.CanCancel == true", resetBody, StringComparison.Ordinal);
        Assert.Contains("_agentController.CancelAsync()", resetBody, StringComparison.Ordinal);
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
