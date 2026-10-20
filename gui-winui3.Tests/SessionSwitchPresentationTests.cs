using Xunit;

namespace RuApp.Tests;

public sealed class SessionSwitchPresentationTests
{
    [Fact]
    public void Switching_preserves_outgoing_rows_until_history_is_ready()
    {
        var code = GuiSource.ViewText("ChatView");
        var open = GuiSource.MethodBody(code, "OpenSessionAsync");
        Assert.Contains("ResetAgentState(preserveTranscript: true)", open);
        Assert.DoesNotContain("DetachAgentActivitiesFromMessages()", open);
        int loaded = open.IndexOf("await AgentActivityReplayLoader.LoadAsync", StringComparison.Ordinal);
        int clear = open.IndexOf("ClearAgentActivities()", StringComparison.Ordinal);
        int replace = open.IndexOf("Messages.ReplaceAll(opened)", StringComparison.Ordinal);
        Assert.True(loaded >= 0 && clear > loaded && replace > clear);
        var detach = GuiSource.MethodBody(code, "DetachAgentForSessionSwitchAsync");
        Assert.Contains("AbandonActiveAgentReply(preservePresentation: true)", detach);
    }

    [Fact]
    public void Transcript_replacement_has_no_per_row_entrance_or_exit_animation()
    {
        var xaml = GuiSource.Text("Views", "ChatView.xaml");
        Assert.DoesNotContain("AddDeleteThemeTransition", xaml);
        Assert.Contains("<ListView.ItemContainerTransitions>", xaml);
        Assert.Contains("<TransitionCollection />", xaml);
    }

    [Fact]
    public void Loading_blocks_session_mutations_and_failures_do_not_mix_transcripts()
    {
        var code = GuiSource.ViewText("ChatView");
        Assert.Contains("_isSessionHistoryLoading", GuiSource.MethodBody(code, "SendCurrentMessageAsync"));
        var open = GuiSource.MethodBody(code, "OpenSessionAsync");
        Assert.Contains("if (history is null)", open);
        Assert.DoesNotContain("Messages.Add(", open);
        Assert.Contains("if (IsCurrentOpen(generation))", open);
    }
}
