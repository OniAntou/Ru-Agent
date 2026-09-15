using System.Text.RegularExpressions;
using Xunit;

namespace RuApp.Tests;

public sealed class ChatFollowContractTests
{
    [Fact]
    public void Follow_threshold_and_flush_batch_are_named_ui_timing_constants()
    {
        string timing = File.ReadAllText(FindSource("Services", "UiTiming.cs"));

        Assert.Contains("ChatFollowThresholdPx", timing, StringComparison.Ordinal);
        Assert.Contains("ActivityFlushBatch", timing, StringComparison.Ordinal);
        Assert.Contains("ContextMeterDebounce", timing, StringComparison.Ordinal);
    }

    [Fact]
    public void Chat_scroll_uses_the_shared_follow_threshold()
    {
        string code = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));

        Assert.Contains("Services.UiTiming.ChatFollowThresholdPx", code, StringComparison.Ordinal);
        Assert.DoesNotContain("maxOffset - verticalOffset <= 100", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Flush_and_hold_timers_use_the_shared_ui_timing_constants()
    {
        string code = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));

        Assert.Contains("Services.UiTiming.ActivityFlushBatch", code, StringComparison.Ordinal);
        Assert.DoesNotContain("FromMilliseconds(80)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Chat_has_a_new_messages_pill_with_jump_action()
    {
        string xaml = File.ReadAllText(FindSource("Views", "ChatView.xaml"));
        string code = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));

        Assert.Contains("x:Name=\"NewMessagesPill\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"NewMessagesPill\"", xaml, StringComparison.Ordinal);
        Assert.Contains("OnNewMessagesPillClick", xaml, StringComparison.Ordinal);
        Assert.Contains("OnNewMessagesPillClick", code, StringComparison.Ordinal);
        Assert.Contains("RestoreComposerDraft", code, StringComparison.Ordinal);
        Assert.Contains("AgentApproveButton?.Focus", code, StringComparison.Ordinal);
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
