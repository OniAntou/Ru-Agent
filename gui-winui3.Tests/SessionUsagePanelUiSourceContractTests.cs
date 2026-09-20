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
        string code = GuiSource.Text("MainWindow.xaml.cs");
        string reveal = GuiSource.MethodBody(code, "SetSessionRowActionsVisible");

        Assert.Contains("SessionDeleteButton", reveal, StringComparison.Ordinal);
        Assert.Contains("SessionUsageButton", reveal, StringComparison.Ordinal);
        Assert.Contains("SessionTimeText", reveal, StringComparison.Ordinal);
    }

    [Fact]
    public void Opening_the_menu_centres_a_panel_over_the_shell_and_reads_that_session()
    {
        string code = GuiSource.Text("MainWindow.xaml.cs");
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
        string code = GuiSource.Text("MainWindow.xaml.cs");

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
        GuiSource.Require(document, "BilledValueText");
        GuiSource.Require(document, "PeakValueText");
        GuiSource.Require(document, "ShareBarGrid");
        GuiSource.Require(document, "InputValueText");
        GuiSource.Require(document, "OutputValueText");
        GuiSource.Require(document, "CacheValueText");
        GuiSource.Require(document, "RowsPanel");
        GuiSource.Require(document, "EmptyText");
        GuiSource.Require(document, "FooterText");
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

        // The bar is split by the shared shares, and a zero share gets no segment: a
        // sliver for a figure nobody measured would be a lie the legend cannot correct.
        Assert.Contains(
            "InputShare",
            GuiSource.MethodBody(code, "PaintShareBar"),
            StringComparison.Ordinal);

        string row = GuiSource.MethodBody(code, "BuildRow");
        Assert.Contains("AutomationProperties.SetName", row, StringComparison.Ordinal);
        Assert.Contains("ShareOfPeak", row, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_read_says_so_instead_of_showing_zeroes()
    {
        string code = GuiSource.Text("Controls", "SessionUsagePanel.xaml.cs");
        string render = GuiSource.MethodBody(code, "Render");

        Assert.Contains("unavailable", render, StringComparison.Ordinal);
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
