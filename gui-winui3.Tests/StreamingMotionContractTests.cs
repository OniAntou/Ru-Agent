using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Streaming motion contract: the reply renders on a cadence instead of per delta,
/// the first text arrives with a short fade, the waiting dots pulse only when the
/// reader allows animation, and the follow scroll glides with the growing reply
/// while deliberate jumps stay instant.
/// </summary>
public sealed class StreamingMotionContractTests
{
    [Fact]
    public void Streaming_text_renders_on_the_cadence_and_defers_the_newest_text()
    {
        var code = GuiSource.ViewText("ChatView");

        var apply = GuiSource.MethodBody(code, "ApplyReplySnapshot");
        Assert.Contains("UiTiming.StreamTextRefresh", apply, StringComparison.Ordinal);
        Assert.Contains("SchedulePendingReplyText", apply, StringComparison.Ordinal);

        // The deferred render is scheduled, not dropped, and it re-checks that the
        // message it carries is still the active one before touching the model.
        var tick = GuiSource.MethodBody(code, "OnReplyTextApplyTick");
        Assert.Contains("FlushPendingReplyText", tick, StringComparison.Ordinal);
        var flush = GuiSource.MethodBody(code, "FlushPendingReplyText");
        Assert.Contains("_pendingReplyText", flush, StringComparison.Ordinal);
        Assert.Contains(
            "ReferenceEquals(message, _activeAgentAssistantMessage)",
            flush,
            StringComparison.Ordinal);
        Assert.Contains("ApplyReplyText", flush, StringComparison.Ordinal);

        // One render moves content, live tail and scroll together.
        var applyText = GuiSource.MethodBody(code, "ApplyReplyText");
        Assert.Contains("UpdateLiveTailText", applyText, StringComparison.Ordinal);
        Assert.Contains("ScrollChatToBottom", applyText, StringComparison.Ordinal);
    }

    [Fact]
    public void The_arrival_fade_is_short_gated_and_only_for_the_first_text()
    {
        var block = GuiSource.Text("Controls", "MarkdownBlock.cs");

        var render = GuiSource.MethodBody(block, "RenderMarkdown");
        Assert.Contains("wasEmpty", render, StringComparison.Ordinal);
        Assert.Contains("PlayArrival", render, StringComparison.Ordinal);
        Assert.Contains("IsStreaming", render, StringComparison.Ordinal);

        var arrival = GuiSource.MethodBody(block, "PlayArrival");
        Assert.Contains("UiTiming.AnimationsEnabled", arrival, StringComparison.Ordinal);
        Assert.Contains("ReplyTextArrivalMilliseconds", arrival, StringComparison.Ordinal);

        Assert.InRange(UiTiming.ReplyTextArrivalMilliseconds, 1, 200);
        Assert.InRange(UiTiming.StreamTextRefresh, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(150));
    }

    [Fact]
    public void Waiting_dots_pulse_only_when_animations_are_enabled()
    {
        var dots = GuiSource.Text("Controls", "TypingDotsControl.cs");
        var apply = GuiSource.MethodBody(dots, "Apply");
        Assert.Contains("UiTiming.AnimationsEnabled", apply, StringComparison.Ordinal);
        Assert.Contains("StartPulse", apply, StringComparison.Ordinal);

        // The static fallback still shows the dots: waiting is readable with
        // animation off, it just stops moving.
        Assert.Contains("StaticOpacity", apply, StringComparison.Ordinal);

        var pulse = GuiSource.MethodBody(dots, "StartPulse");
        Assert.Contains("AnimationIterationBehavior.Forever", pulse, StringComparison.Ordinal);

        // The template drives the dots from the model's waiting state.
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        Assert.Contains(
            document.Descendants(),
            element => element.Name.LocalName == "TypingDotsControl"
                && GuiSource.AttributeValue(element, "IsActive") is { } active
                && active.Contains("ShowTypingDots", StringComparison.Ordinal));
    }

    [Fact]
    public void Automatic_follow_does_not_animate_obsolete_offsets_across_layouts()
    {
        var code = GuiSource.ViewText("ChatView");

        // Native animations can outlive their measured layout or session. Every
        // automatic follow path uses immediate positioning, even with motion on.
        foreach (var member in new[] { "ScrollChatToBottom", "RetryScrollAfterLayout", "OnTranscriptLayoutUpdated" })
        {
            var follow = GuiSource.MethodBody(code, member);
            Assert.Contains("disableAnimation: true", follow, StringComparison.Ordinal);
            Assert.DoesNotContain("disableAnimation: !UiTiming", follow, StringComparison.Ordinal);
        }

        // Session opens remain a deliberate instant landing.
        var landing = GuiSource.MethodBody(code, "ScrollToLatestAfterLayout");
        Assert.Contains("disableAnimation: true", landing, StringComparison.Ordinal);
    }
}
