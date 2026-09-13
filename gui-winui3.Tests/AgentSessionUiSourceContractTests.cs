using Xunit;

namespace RuApp.Tests;

public sealed class AgentSessionUiSourceContractTests
{
    [Fact]
    public void Composer_exposes_one_static_agent_indicator()
    {
        string xaml = File.ReadAllText(FindSource("Views", "ChatView.xaml"));

        Assert.Contains("<Button x:Name=\"AgentModeButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"Collapsed\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"AgentModeButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Coding Agent\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AgentModeIcon\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AgentModeLabel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Agent\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentPresetBox", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentModeFlyout", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentModeChatOption", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentModeAgentOption", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentSettingsButton", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentSettingsFlyout", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentWorkspaceBox", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Click=\"OnAgentMode", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Composer_places_left_aligned_attachment_and_agent_selectors_with_right_aligned_send_button()
    {
        string xaml = File.ReadAllText(FindSource("Views", "ChatView.xaml"));

        int composerStart = xaml.IndexOf("ColumnDefinitions=\"Auto,Auto,Auto,*,Auto\"", StringComparison.Ordinal);
        Assert.True(composerStart >= 0, "Composer columns must place attachment and agent button on the left, followed by a spacer and the send button.");

        int attachStart = xaml.IndexOf("x:Name=\"AttachImageButton\"", composerStart, StringComparison.Ordinal);
        int agentModeStart = xaml.IndexOf("x:Name=\"AgentModeButton\"", composerStart, StringComparison.Ordinal);
        int sendStart = xaml.IndexOf("x:Name=\"SendButton\"", composerStart, StringComparison.Ordinal);
        Assert.True(attachStart >= composerStart && agentModeStart > attachStart && sendStart > agentModeStart);
        Assert.Contains("Grid.Column=\"0\"", xaml[attachStart..agentModeStart], StringComparison.Ordinal);
        Assert.Contains("Grid.Column=\"1\"", xaml[agentModeStart..sendStart], StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"SendButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"AttachImageButton\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Transcript_keeps_the_full_width_scroll_host_and_centered_reading_column()
    {
        string xaml = File.ReadAllText(FindSource("Views", "ChatView.xaml"));
        string codeBehind = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));

        int readingStart = xaml.IndexOf("<Grid x:Name=\"ReadingColumn\"", StringComparison.Ordinal);
        int inputStart = xaml.IndexOf("<Grid x:Name=\"InputContainer\"", readingStart, StringComparison.Ordinal);
        Assert.True(readingStart >= 0, "ReadingColumn is missing.");
        Assert.True(inputStart > readingStart, "InputContainer must remain outside ReadingColumn.");
        string readingBlock = xaml[readingStart..inputStart];

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
    public void Agent_code_has_no_mode_switch_or_legacy_chat_transport_branch()
    {
        string codeBehind = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));

        foreach (string retired in new[]
        {
            "_agentMode",
            "SetAgentMode",
            "OnAgentModeButtonClick",
            "OnAgentModeOptionClick",
            "ChatWebSocketClient",
            "ChatTurnPresenter",
            "OnWsFrameReceived",
            "/chat"
        })
        {
            Assert.DoesNotContain(retired, codeBehind, StringComparison.Ordinal);
        }

        Assert.Contains("UpdateAgentComposerVisuals", codeBehind, StringComparison.Ordinal);
        Assert.Contains("SendAgentTurnAsync", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_agentController.CreateAndRunAsync", codeBehind, StringComparison.Ordinal);
        Assert.Contains("AgentWorkspaceSelection.Resolve(null, _defaultAgentWorkspace)", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentSettingsFlyout", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentWorkspaceBox", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("OnAgentPresetChanged", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("OnAgentTrustApproveClick", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("OnAgentTrustDenyClick", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Agent_starts_with_idle_status_hidden()
    {
        string codeBehind = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));

        int visualStart = codeBehind.IndexOf("private void UpdateAgentComposerVisuals", StringComparison.Ordinal);
        Assert.True(visualStart >= 0, "Agent composer visual setup is missing.");
        int visualEnd = codeBehind.IndexOf("\n        private", visualStart + 1, StringComparison.Ordinal);
        string visualBody = visualEnd > visualStart
            ? codeBehind[visualStart..visualEnd]
            : codeBehind[visualStart..];
        Assert.Contains("AgentModeLabel.Text = \"Agent\"", visualBody, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentPresetBox", visualBody, StringComparison.Ordinal);

        int sendStart = codeBehind.IndexOf("private async Task SendAgentTurnAsync", StringComparison.Ordinal);
        Assert.True(sendStart >= 0, "SendAgentTurnAsync is missing.");
        int sendEnd = codeBehind.IndexOf("private void BeginAgentReply", sendStart, StringComparison.Ordinal);
        Assert.True(sendEnd > sendStart);
        string sendBody = codeBehind[sendStart..sendEnd];
        Assert.Contains("ShowAgentInlineStatus(", sendBody, StringComparison.Ordinal);
        Assert.DoesNotContain("SetAgentMode", sendBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"standard\",\n                    0,", sendBody.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("\"standard\",\n                    null,", sendBody.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void Agent_session_reset_clears_state_and_session_switch_cancels_before_replacement()
    {
        string codeBehind = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));

        int resetStart = codeBehind.IndexOf("private void ResetAgentState", StringComparison.Ordinal);
        Assert.True(resetStart >= 0);
        int nextMethod = codeBehind.IndexOf("\n        private", resetStart + 1, StringComparison.Ordinal);
        string resetBody = nextMethod > resetStart ? codeBehind[resetStart..nextMethod] : codeBehind[resetStart..];

        Assert.Contains("AbandonActiveAgentReply", resetBody, StringComparison.Ordinal);
        Assert.Contains("UpdateAgentComposerVisuals", resetBody, StringComparison.Ordinal);

        int openStart = codeBehind.IndexOf("public async Task OpenSessionAsync", StringComparison.Ordinal);
        int switchSession = codeBehind.IndexOf("_activeSessionId = sessionId;", openStart, StringComparison.Ordinal);
        Assert.True(openStart >= 0);
        Assert.True(switchSession > openStart);
        Assert.True(
            codeBehind.LastIndexOf("await StopActiveAgentAsync(force: true);", switchSession, StringComparison.Ordinal) > openStart);
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
