using Xunit;

namespace RuApp.Tests;

public sealed class AgentSessionUiSourceContractTests
{
    [Fact]
    public void Composer_exposes_one_static_agent_indicator_and_one_preset_selector()
    {
        string xaml = File.ReadAllText(FindSource("Views", "ChatView.xaml"));

        Assert.Contains("<Button x:Name=\"AgentModeButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"Collapsed\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"AgentModeButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Coding Agent\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AgentModeIcon\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AgentModeLabel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Agent\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AgentPresetBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"Visible\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectionChanged=\"OnAgentPresetChanged\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Standard\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Full Access\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentModeFlyout", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentModeChatOption", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentModeAgentOption", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentSettingsButton", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentSettingsFlyout", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentWorkspaceBox", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Click=\"OnAgentMode", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Composer_places_left_aligned_mic_and_agent_selectors_with_right_aligned_send_button()
    {
        string xaml = File.ReadAllText(FindSource("Views", "ChatView.xaml"));

        int composerStart = xaml.IndexOf("ColumnDefinitions=\"Auto,Auto,Auto,*,Auto\"", StringComparison.Ordinal);
        Assert.True(composerStart >= 0, "Composer columns must place mic, agent button, and preset on the left, followed by a spacer and send button.");

        int micStart = xaml.IndexOf("x:Name=\"MicButton\"", composerStart, StringComparison.Ordinal);
        int agentModeStart = xaml.IndexOf("x:Name=\"AgentModeButton\"", composerStart, StringComparison.Ordinal);
        int presetStart = xaml.IndexOf("x:Name=\"AgentPresetBox\"", composerStart, StringComparison.Ordinal);
        int sendStart = xaml.IndexOf("x:Name=\"SendButton\"", composerStart, StringComparison.Ordinal);
        Assert.True(micStart >= composerStart && agentModeStart > micStart && presetStart > agentModeStart && sendStart > presetStart);
        Assert.Contains("Grid.Column=\"0\"", xaml[micStart..agentModeStart], StringComparison.Ordinal);
        Assert.Contains("Grid.Column=\"1\"", xaml[agentModeStart..presetStart], StringComparison.Ordinal);
        Assert.Contains("Grid.Column=\"2\"", xaml[presetStart..sendStart], StringComparison.Ordinal);
        Assert.Contains("Grid.Column=\"4\"", xaml[sendStart..], StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"MicButton\"", xaml, StringComparison.Ordinal);
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
        Assert.Contains("AgentPresetBox.Visibility = Visibility.Visible", codeBehind, StringComparison.Ordinal);
        Assert.Contains("SendAgentTurnAsync", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_agentController.CreateAndRunAsync", codeBehind, StringComparison.Ordinal);
        Assert.Contains("AgentWorkspaceSelection.Resolve(null, _defaultAgentWorkspace)", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentSettingsFlyout", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentWorkspaceBox", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void Agent_starts_with_idle_status_hidden_and_preset_visible()
    {
        string codeBehind = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));

        int visualStart = codeBehind.IndexOf("private void UpdateAgentComposerVisuals", StringComparison.Ordinal);
        Assert.True(visualStart >= 0, "Agent composer visual setup is missing.");
        int visualEnd = codeBehind.IndexOf("\n        private", visualStart + 1, StringComparison.Ordinal);
        string visualBody = visualEnd > visualStart
            ? codeBehind[visualStart..visualEnd]
            : codeBehind[visualStart..];
        Assert.Contains("AgentPresetBox.Visibility = Visibility.Visible", visualBody, StringComparison.Ordinal);
        Assert.Contains("AgentModeLabel.Text = \"Agent\"", visualBody, StringComparison.Ordinal);

        int sendStart = codeBehind.IndexOf("private async Task SendAgentTurnAsync", StringComparison.Ordinal);
        Assert.True(sendStart >= 0, "SendAgentTurnAsync is missing.");
        int sendEnd = codeBehind.IndexOf("private void BeginAgentReply", sendStart, StringComparison.Ordinal);
        Assert.True(sendEnd > sendStart);
        string sendBody = codeBehind[sendStart..sendEnd];
        Assert.Contains("ShowAgentInlineStatus(", sendBody, StringComparison.Ordinal);
        Assert.DoesNotContain("SetAgentMode", sendBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Agent_session_reset_still_cancels_and_clears_active_state()
    {
        string codeBehind = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));

        int resetStart = codeBehind.IndexOf("private void ResetAgentState", StringComparison.Ordinal);
        Assert.True(resetStart >= 0);
        int nextMethod = codeBehind.IndexOf("\n        private", resetStart + 1, StringComparison.Ordinal);
        string resetBody = nextMethod > resetStart ? codeBehind[resetStart..nextMethod] : codeBehind[resetStart..];

        Assert.Contains("_agentController?.CanCancel == true", resetBody, StringComparison.Ordinal);
        Assert.Contains("_agentController.CancelAsync()", resetBody, StringComparison.Ordinal);
        Assert.Contains("AbandonActiveAgentReply", resetBody, StringComparison.Ordinal);
        Assert.Contains("UpdateAgentComposerVisuals", resetBody, StringComparison.Ordinal);
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
