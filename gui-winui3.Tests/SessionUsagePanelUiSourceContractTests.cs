using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The session usage panel and its home in the session row's menu: where it reads
/// from, what it is wired to, and that it is reachable. The arithmetic lives in
/// <see cref="SessionUsageSummaryTests"/>.
/// </summary>
public sealed class SessionUsagePanelUiSourceContractTests
{
    [Fact]
    public void The_row_offers_a_named_menu_button_beside_the_delete_control()
    {
        var document = GuiSource.Xml("MainWindow.xaml");
        var button = GuiSource.Require(document, "SessionUsageButton");

        Assert.Equal("OnSessionUsageClick", GuiSource.AttributeValue(button, "Click"));
        Assert.Equal("Session usage", GuiSource.AttributeValue(button, "Name"));
        Assert.Equal("{x:Bind Id, Mode=OneTime}", GuiSource.AttributeValue(button, "Tag"));

        // Hidden until the row is hovered, exactly like the delete control it sits
        // next to — and hidden from hit testing with it, so an invisible button can
        // never answer a click meant for the timestamp underneath.
        Assert.Equal("0", GuiSource.AttributeValue(button, "Opacity"));
        Assert.Equal("False", GuiSource.AttributeValue(button, "IsHitTestVisible"));

        var row = GuiSource.Require(document, "SessionDeleteButton").Parent!;
        Assert.True(GuiSource.IsDescendantOf(button, row));
    }

    [Fact]
    public void Both_row_actions_appear_and_disappear_together()
    {
        string code = GuiSource.ViewText("MainWindow");
        string reveal = GuiSource.MethodBody(code, "SetSessionRowActionsVisible");

        Assert.Contains("SessionDeleteButton", reveal, StringComparison.Ordinal);
        Assert.Contains("SessionUsageButton", reveal, StringComparison.Ordinal);
        Assert.Contains("SessionTimeText", reveal, StringComparison.Ordinal);
    }

    [Fact]
    public void Opening_the_menu_centres_a_panel_over_the_shell_and_reads_that_session()
    {
        string code = GuiSource.ViewText("MainWindow");
        string open = GuiSource.MethodBody(code, "OnSessionUsageClick");

        // The session comes off the button that was clicked, never off "the session
        // on screen": the whole point is asking about a row that may not be open.
        Assert.Contains("button.Tag", open, StringComparison.Ordinal);
        Assert.Contains("OpenSessionUsageAsync", open, StringComparison.Ordinal);

        string show = GuiSource.MethodBody(code, "OpenSessionUsageAsync");
        Assert.Contains("SessionUsageOverlay.Visibility", show, StringComparison.Ordinal);
        Assert.Contains("SessionUsagePanelControl.LoadAsync", show, StringComparison.Ordinal);
    }

    [Fact]
    public void The_centred_panel_can_be_closed_three_ways()
    {
        var document = GuiSource.Xml("MainWindow.xaml");
        string code = GuiSource.ViewText("MainWindow");

        // Backdrop, close button, and Escape: the ways out of every other modal in
        // the shell, so this one does not invent its own.
        Assert.Equal("OnSessionUsageBackdropPointerPressed",
            GuiSource.AttributeValue(GuiSource.Require(document, "SessionUsageBackdrop"), "PointerPressed"));
        Assert.Equal("OnCloseSessionUsageClick",
            GuiSource.AttributeValue(GuiSource.Require(document, "CloseSessionUsageButton"), "Click"));
        Assert.Equal("Close session usage",
            GuiSource.AttributeValue(GuiSource.Require(document, "CloseSessionUsageButton"), "Name"));
        Assert.Contains("_isSessionUsageOpen", GuiSource.MethodBody(code, "OnShellPreviewKeyDown"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_panel_states_its_headline_figures_its_split_and_its_rows()
    {
        var document = GuiSource.Xml("Controls", "SessionUsagePanel.xaml");

        GuiSource.Require(document, "SessionTitleText");
        GuiSource.Require(document, "CoverageText");
        GuiSource.Require(document, "ElapsedValueText");
        GuiSource.Require(document, "BilledValueText");
        GuiSource.Require(document, "PeakValueText");
        GuiSource.Require(document, "ShareBarGrid");
        GuiSource.Require(document, "InputValueText");
        GuiSource.Require(document, "OutputValueText");
        GuiSource.Require(document, "CacheReadValueText");
        GuiSource.Require(document, "CacheWriteValueText");
        GuiSource.Require(document, "RowsPanel");
        GuiSource.Require(document, "EmptyText");
        GuiSource.Require(document, "FooterText");
    }

    [Fact]
    public void The_panel_carries_no_pill_and_no_nested_card()
    {
        var document = GuiSource.Xml("Controls", "SessionUsagePanel.xaml");

        // Both were measured by eye on the live panel and neither earned its place: a
        // fully rounded bar end says "on/off" where the bar carries a proportion, and
        // the figure inside a card inside a card put two outlines and two backgrounds
        // behind a single number.
        var radii = GuiSource.AttributeLocalValues(document, "CornerRadius");
        Assert.DoesNotContain(
            radii,
            value => value.Contains("AppPillCornerRadius", StringComparison.Ordinal));
        Assert.DoesNotContain(
            radii,
            value => value.Contains("AppMicroPillCornerRadius", StringComparison.Ordinal));

        // The two figures sit on the surface, separated by one rule.
        Assert.DoesNotContain(
            radii,
            value => value.Contains("AppCardCornerRadius", StringComparison.Ordinal)
                || value.Contains("AppInsetCornerRadius", StringComparison.Ordinal));
    }

    [Fact]
    public void Each_part_names_itself_beside_its_own_bar()
    {
        // The legend this replaced cost three rounded swatches and a lookup. The colour
        // and the figure now sit in the same cell, so the split needs no key.
        var document = GuiSource.Xml("Controls", "SessionUsagePanel.xaml");

        foreach (string part in new[] { "Input", "Output", "CacheRead", "CacheWrite" })
        {
            var bar = GuiSource.Require(document, part + "ShareBar");
            var cell = bar.Parent!;
            Assert.Contains(
                cell.Elements()
                    .Select(element => GuiSource.AttributeValue(element, "Text")),
                label => label is not null
                    && label.StartsWith(part.Replace("CacheRead", "Cache read", StringComparison.Ordinal)
                        .Replace("CacheWrite", "Cache write", StringComparison.Ordinal), StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_split_keeps_cache_reads_and_writes_apart()
    {
        // One lumped cache figure hid the difference between a session that re-read a
        // warm cache and one that paid for fresh input, which is the whole reason the
        // split exists.
        var document = GuiSource.Xml("Controls", "SessionUsagePanel.xaml");

        var grid = GuiSource.Require(document, "ShareBarGrid");
        var columns = grid.Attribute("ColumnDefinitions")?.Value ?? string.Empty;
        Assert.Equal(4, columns.Split(',').Length);

        string code = GuiSource.Text("Controls", "SessionUsagePanel.xaml.cs");
        string render = GuiSource.MethodBody(code, "Render");
        Assert.Contains("CacheReadShare", render, StringComparison.Ordinal);
        Assert.Contains("CacheWriteShare", render, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_row_reports_when_it_ran_how_it_ended_and_how_long_it_took()
    {
        // All three are already on the wire. The row used to describe a run that
        // reported no usage as "no provider report", which is true of a failed run and
        // of a successful one and so says nothing about either.
        var document = GuiSource.Xml("Controls", "SessionUsagePanel.xaml");
        var runsHeader = GuiSource.Require(document, "RunsHeaderText").Parent!;
        Assert.Contains(
            runsHeader.Elements()
                .Select(element => GuiSource.AttributeValue(element, "Text")),
            label => label is not null && label.Contains("largest", StringComparison.Ordinal));

        string code = GuiSource.Text("Controls", "SessionUsagePanel.xaml.cs");
        string row = GuiSource.MethodBody(code, "BuildRow");
        Assert.Contains("row.Outcome", row, StringComparison.Ordinal);
        Assert.Contains("FormatDuration", row, StringComparison.Ordinal);
        Assert.Contains("ShareOfBilled", row, StringComparison.Ordinal);
        Assert.DoesNotContain("no provider report", row, StringComparison.Ordinal);
    }

    [Fact]
    public void The_panel_explains_nothing_about_itself()
    {
        // A pass of sentences about how to read the panel — a kicker, a caption per
        // figure, a note on what the meters were scaled against, a footer about request
        // counts — is what made it read as vague. Every line left has to be a
        // measurement or a state.
        var document = GuiSource.Xml("Controls", "SessionUsagePanel.xaml");
        string code = GuiSource.Text("Controls", "SessionUsagePanel.xaml.cs");

        foreach (string banned in new[]
        {
            "steps are never summed",
            "Each run costs two requests",
            "no provider report",
        })
        {
            Assert.DoesNotContain(
                GuiSource.StringLiterals(GuiSource.MethodBody(code, "Render")),
                value => value.Contains(banned, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                GuiSource.StringLiterals(GuiSource.MethodBody(code, "LoadAsync")),
                value => value.Contains(banned, StringComparison.OrdinalIgnoreCase));
        }

        // The kicker this lost. Checked as a shape rather than as a phrase, because a
        // phrase check also matched the diagnostic log line and told us nothing. A
        // value with no letters in it is a placeholder, not a label.
        var shouted = GuiSource.AttributeValues(document, "Text")
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Where(value => value.Any(char.IsLetter))
            .Where(value => value.All(character => !char.IsLetter(character) || char.IsUpper(character)))
            .ToList();
        Assert.True(
            shouted.Count == 0,
            "an all-caps label is the eyebrow grammar this panel dropped:\n  " + string.Join("\n  ", shouted));
    }

    [Fact]
    public void The_panel_reads_the_shared_summary_and_names_every_row()
    {
        string code = GuiSource.Text("Controls", "SessionUsagePanel.xaml.cs");

        Assert.True(GuiSource.HasMember(code, "LoadAsync"));
        Assert.Contains(
            "SessionUsagePolicy.Summarize",
            GuiSource.MethodBody(code, "Render"),
            StringComparison.Ordinal);

        // The bars are split by the shared shares, and a zero share gets no segment: a
        // sliver for a figure nobody measured would be a lie the panel cannot correct.
        string render = GuiSource.MethodBody(code, "Render");
        Assert.Contains("InputShare", render, StringComparison.Ordinal);
        Assert.Contains("OutputShare", render, StringComparison.Ordinal);
        Assert.Contains("CacheReadShare", render, StringComparison.Ordinal);
        Assert.Contains("CacheWriteShare", render, StringComparison.Ordinal);

        string cell = GuiSource.MethodBody(code, "PaintShareCell");
        Assert.Contains("if (share <= 0)", cell, StringComparison.Ordinal);
        Assert.Contains("return;", cell, StringComparison.Ordinal);

        string row = GuiSource.MethodBody(code, "BuildRow");
        Assert.Contains("AutomationProperties.SetName", row, StringComparison.Ordinal);
        Assert.Contains("ShareOfPeak", row, StringComparison.Ordinal);
    }

    [Fact]
    public void The_card_is_not_pinned_to_a_height_its_content_cannot_fill()
    {
        string code = GuiSource.ViewText("MainWindow");
        string size = GuiSource.MethodBody(code, "ApplySessionUsageCardSize");

        // A fixed Height here wins over the panel's own layout: the panel sizes itself
        // to its rows, and the slack the fixed height added rendered as a band of bare
        // card surface below the footer, which reads as a fault rather than as space.
        // Capping MaxHeight is what keeps the card inside the window.
        Assert.DoesNotContain("SessionUsageCard.Height", size, StringComparison.Ordinal);
        Assert.Contains("SessionUsageCard.MaxHeight", size, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_read_says_so_instead_of_showing_zeroes()
    {
        string code = GuiSource.Text("Controls", "SessionUsagePanel.xaml.cs");
        string render = GuiSource.MethodBody(code, "Render");

        Assert.Contains("unavailable", render, StringComparison.Ordinal);
    }

    [Fact]
    public void A_superseded_read_never_paints_over_the_new_session()
    {
        string code = GuiSource.Text("Controls", "SessionUsagePanel.xaml.cs");
        string load = GuiSource.MethodBody(code, "LoadAsync");

        Assert.Contains("_loadGeneration", load, StringComparison.Ordinal);
        Assert.Contains("token", load, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationToken.None", load, StringComparison.Ordinal);
    }

    [Fact]
    public void The_read_is_bounded_and_skips_runs_that_cannot_have_reported()
    {
        string history = GuiSource.Text("Services", "ApiService.AgentHistory.cs");

        Assert.True(GuiSource.HasMember(history, "ListSessionUsageAsync"));
        string read = GuiSource.MethodBody(history, "ListSessionUsageAsync");
        Assert.Contains("SessionUsagePolicy.MaxReportedRuns", read, StringComparison.Ordinal);
        // A live run has no durable report yet, so the walk must not ask for one.
        Assert.Contains("run.IsTerminal", read, StringComparison.Ordinal);
    }
}
