using System.Globalization;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Context-window meter arithmetic: which reading is authoritative, how the share is
/// computed, and when the meter turns amber or red.
/// </summary>
public sealed class AgentContextMeterPolicyTests
{
    [Fact]
    public void The_provider_report_wins_over_the_character_estimate()
    {
        // The provider total already covers system prompt, tool definitions and
        // history, so a smaller local estimate must not replace it.
        var meter = AgentContextMeterPolicy.Evaluate(
            maximumTokens: 100_000,
            displayLimit: "100K",
            estimatedTokens: 1_200,
            reportedTokens: 42_000);

        Assert.Equal(42_000, meter.UsedTokens);
    }

    [Fact]
    public void The_estimate_carries_a_turn_that_has_not_run_yet()
    {
        var meter = AgentContextMeterPolicy.Evaluate(
            maximumTokens: 100_000,
            displayLimit: "100K",
            estimatedTokens: 3_500,
            reportedTokens: 0);

        Assert.Equal(3_500, meter.UsedTokens);
    }

    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(50_000, 50.0)]
    [InlineData(100_000, 100.0)]
    [InlineData(150_000, 100.0)]
    public void The_share_never_exceeds_a_full_ring(int usedTokens, double expected)
    {
        var meter = AgentContextMeterPolicy.Evaluate(100_000, "100K", usedTokens, usedTokens);

        Assert.Equal(expected, meter.Percentage, precision: 3);
        Assert.InRange(meter.Percentage, 0.0, 100.0);
    }

    [Fact]
    public void An_unknown_budget_reports_a_count_instead_of_a_share()
    {
        var meter = AgentContextMeterPolicy.Evaluate(
            maximumTokens: 0,
            displayLimit: string.Empty,
            estimatedTokens: 900,
            reportedTokens: 0);

        Assert.False(meter.HasBudget);
        Assert.False(meter.ShowPercentage);
        Assert.Equal(0.0, meter.Percentage);
        Assert.Equal(900, meter.UsedTokens);
    }

    [Theory]
    [InlineData(0.0, AgentContextMeterSeverity.Normal)]
    [InlineData(70.0, AgentContextMeterSeverity.Normal)]
    [InlineData(70.1, AgentContextMeterSeverity.Warning)]
    [InlineData(90.0, AgentContextMeterSeverity.Warning)]
    [InlineData(90.1, AgentContextMeterSeverity.Danger)]
    [InlineData(100.0, AgentContextMeterSeverity.Danger)]
    public void Severity_turns_at_the_documented_thresholds(double percentage, AgentContextMeterSeverity expected)
    {
        Assert.Equal(expected, AgentContextMeterPolicy.Severity(percentage));
    }

    [Fact]
    public void The_thresholds_are_ordered()
    {
        // A warning band that opened after the danger band would make the amber state
        // unreachable.
        Assert.True(AgentContextMeterPolicy.WarningThresholdPercent < AgentContextMeterPolicy.DangerThresholdPercent);
        Assert.InRange(AgentContextMeterPolicy.WarningThresholdPercent, 1.0, 99.0);
        Assert.InRange(AgentContextMeterPolicy.DangerThresholdPercent, 1.0, 100.0);
    }

    [Fact]
    public void An_empty_transcript_measures_nothing()
    {
        var (characters, images) = AgentContextMeterPolicy.Measure([]);

        Assert.Equal(0, characters);
        Assert.Equal(0, images);
    }

    [Fact]
    public void The_draft_moves_the_meter_before_send()
    {
        // The user should see the cost of what they are typing, not only of what they
        // already sent.
        var (characters, _) = AgentContextMeterPolicy.Measure(
            [("hello", 0)],
            draftText: "and this draft");

        Assert.Equal("hello".Length + "and this draft".Length, characters);
    }

    [Fact]
    public void Attachments_count_from_the_transcript_and_the_pending_list()
    {
        var (_, images) = AgentContextMeterPolicy.Measure(
            [("first", 2), ("second", 1)],
            pendingAttachments: 1);

        Assert.Equal(4, images);
    }

    [Fact]
    public void A_null_message_content_is_skipped_without_throwing()
    {
        var (characters, images) = AgentContextMeterPolicy.Measure(
            [(null, 1), ("", 0), ("text", 0)]);

        Assert.Equal("text".Length, characters);
        Assert.Equal(1, images);
    }

    [Fact]
    public void The_largest_reported_total_wins()
    {
        // A provider report covers one step, so a long conversation is represented by
        // its largest report rather than a sum.
        Assert.Equal(42_000, AgentContextMeterPolicy.MaxReportedTokens([1_200, 42_000, 7_500]));
    }

    [Fact]
    public void No_reports_means_zero()
    {
        Assert.Equal(0, AgentContextMeterPolicy.MaxReportedTokens([]));
    }

    [Fact]
    public void Opening_a_session_repaints_the_meter_after_history_lands()
    {
        // Clearing the transcript repaints zero and the refills arrive inside
        // the one-second throttle, so the open path must repaint by force once
        // history is in place instead of staying stuck at zero.
        string code = GuiSource.ViewText("ChatView");
        string open = GuiSource.MethodBody(code, "OpenSessionAsync");
        Assert.Contains("UpdateContextUsage", open, StringComparison.Ordinal);
    }

    [Fact]
    public void The_flyout_paints_three_measured_categories_without_an_inferred_overhead()
    {
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        var segments = GuiSource.Require(document, "ContextSegments");
        Assert.Equal(3, segments.Elements().Count(element => element.Name.LocalName == "Border"));
        foreach (var name in new[] { "ContextSystemLabel", "ContextToolsLabel", "ContextMessagesLabel" })
            GuiSource.Require(document, name);
        var update = GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "UpdateContextUsage");
        Assert.Contains("ContextPanelPresenter.Project", update);
        Assert.DoesNotContain("OverheadTokens", update);
    }

    [Fact]
    public void The_fill_shrinks_with_the_budget_share_not_the_track()
    {
        // The bar must mirror the percentage against the budget: a near-empty
        // session paints a sliver, never a full track.
        string code = GuiSource.ViewText("ChatView");
        string paint = GuiSource.MethodBody(code, "PaintContextBreakdown");
        Assert.Contains("maximumTokens", paint, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reported_total_never_lowers_the_meter()
    {
        // A later, smaller report must not pull the reading back down below what was
        // already observed.
        Assert.Equal(9_000, AgentContextMeterPolicy.MaxReportedTokens([9_000, 100]));
    }

    [Fact]
    public void The_pin_identity_wraps_inside_a_width_bounded_well()
    {
        // The identity string carries unbreakable model ids, so without an explicit
        // content width the flyout clipped it mid-id instead of wrapping. The well
        // is the seam: the meter update shows and hides it, never the bare text.
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        var well = GuiSource.Require(document, "RetainedRunPinWell");
        var identity = GuiSource.Require(document, "RetainedRunIdentityText");
        Assert.True(GuiSource.IsDescendantOf(identity, well));
        Assert.Equal("Wrap", GuiSource.AttributeValue(identity, "TextWrapping"));

        Assert.True(GuiSource.IntAttribute(identity, "Width") > 0);
        Assert.Contains("ToolTip", identity.Parent!.Name.LocalName);

        string code = GuiSource.ViewText("ChatView");
        string update = GuiSource.MethodBody(code, "UpdateContextUsage");
        Assert.Contains("RetainedRunPinWell", update, StringComparison.Ordinal);
    }

    [Fact]
    public void The_flyout_content_fits_inside_its_presenter_frame()
    {
        // The presenter Width includes its padding and border; a content panel
        // wider than the remaining inner area clips its right edge, which cut
        // the last glyph off the legend value. The content must fit the frame.
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        var flyout = GuiSource.Require(document, "ContextWindowFlyout");
        var style = flyout.Descendants().Single(element => element.Name.LocalName == "Style" && GuiSource.AttributeValue(element, "TargetType") == "FlyoutPresenter");
        double StyleNumber(string property) => double.Parse(
            style.Elements()
                .Single(element => element.Name.LocalName == "Setter"
                    && string.Equals((string?)element.Attribute("Property"), property, StringComparison.Ordinal))
                .Attribute("Value")!.Value.Split(',')[0],
            CultureInfo.InvariantCulture);

        var content = GuiSource.Require(document, "ContextPanelContent");
        int contentWidth = GuiSource.IntAttribute(content, "Width");
        double innerWidth = StyleNumber("Width")
            - (2 * StyleNumber("Padding"))
            - (2 * StyleNumber("BorderThickness"));

        Assert.True(innerWidth >= contentWidth,
            $"The {contentWidth}px content needs {innerWidth}px inside the presenter frame.");
    }
}
