using System;
using System.Linq;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The right side surface's layout contract: the card stays the summary, the
/// details panel carries the tabs, and the title bar carries the one control
/// that hides and restores the whole surface.
/// </summary>
public sealed class AgentSidePanelUiContractTests
{
    [Fact]
    public void The_surface_has_a_summary_card_and_a_details_panel()
    {
        var document = GuiSource.Xml("Controls", "AgentSidePanelControl.xaml");
        foreach (var name in new[]
        {
            "CardHost", "PanelHost", "PanelDivider", "PanelResizeGrip",
            "ChangesChip", "TasksChip", "AddTabButton", "FullscreenButton", "CollapseButton",
            "ChangesBody", "TasksBody", "ChangesList", "AgentTasksList", "ChangesEmptyText",
        })
        {
            GuiSource.Require(document, name);
        }
    }

    [Fact]
    public void The_summary_card_lists_tasks_without_the_stop_control()
    {
        var document = GuiSource.Xml("Controls", "AgentSidePanelControl.xaml");
        var named = GuiSource.NamedElements(document);

        var cardList = named["CardTasksList"];
        Assert.DoesNotContain(
            cardList.Descendants(),
            element => GuiSource.AttributeValue(element, "Click") == "OnTaskStopClick");

        var panelList = named["AgentTasksList"];
        Assert.Contains(
            panelList.Descendants(),
            element => GuiSource.AttributeValue(element, "Click") == "OnTaskStopClick");
    }

    [Fact]
    public void The_details_panel_closes_tabs_and_collapses_back_to_the_card()
    {
        var code = GuiSource.ViewText("ChatView");
        Assert.Contains("CloseTab", GuiSource.MethodBody(code, "OnSidePanelTabClosed"), StringComparison.Ordinal);
        Assert.Contains("CollapseToCard", GuiSource.MethodBody(code, "OnSidePanelCollapseRequested"), StringComparison.Ordinal);
        Assert.Contains("ToggleVisibility", GuiSource.MethodBody(code, "ToggleSidePanel"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_view_answers_the_toggle_state()
    {
        var code = GuiSource.ViewText("ChatView");
        Assert.Contains(
            "UpdateSidePanelToggleState",
            GuiSource.MethodBody(code, "UpdateSidePanelLayout"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_chip_hover_covers_the_whole_chip()
    {
        // The label and the close control are separate buttons; the chip's Border
        // owns the hover surface so the chip reads as one block, not two fills.
        var document = GuiSource.Xml("Controls", "AgentSidePanelControl.xaml");
        foreach (var chip in new[] { "ChangesChip", "TasksChip", "FilesChip" })
        {
            var element = GuiSource.Require(document, chip);
            Assert.Equal("OnChipPointerEntered", GuiSource.AttributeValue(element, "PointerEntered"));
            Assert.Equal("OnChipPointerExited", GuiSource.AttributeValue(element, "PointerExited"));
        }

        var code = GuiSource.Text("Controls", "AgentSidePanelControl.xaml.cs");
        Assert.Contains(
            "SetChipHovered",
            GuiSource.MethodBody(code, "OnChipPointerEntered"),
            StringComparison.Ordinal);
        Assert.Contains(
            "_changesChipHovered = hovered",
            GuiSource.MethodBody(code, "SetChipHovered"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_files_tab_lists_the_workspace_through_the_bounded_lister()
    {
        var document = GuiSource.Xml("Controls", "AgentSidePanelControl.xaml");
        foreach (var name in new[] { "FilesChip", "FilesBody", "FilesList", "FilesRefreshButton", "FilesRootText" })
        {
            GuiSource.Require(document, name);
        }

        // The list must be bound to the collection; an unbound ListView renders
        // nothing while every other contract still passes.
        var filesList = GuiSource.Require(document, "FilesList");
        Assert.Contains(
            "Files",
            GuiSource.AttributeValue(filesList, "ItemsSource") ?? string.Empty,
            StringComparison.Ordinal);

        var code = GuiSource.Text("Controls", "AgentSidePanelControl.Files.cs");
        Assert.Contains(
            "FilesRefresh.RefreshAsync",
            GuiSource.MethodBody(code, "RefreshFilesAsync"),
            StringComparison.Ordinal);
        Assert.Contains("FileTreeLister.List", code, StringComparison.Ordinal);
        Assert.Contains("MaxFileTreeRows", code, StringComparison.Ordinal);
        Assert.Contains(
            "CollapseFileRow",
            GuiSource.MethodBody(code, "OnFileRowClick"),
            StringComparison.Ordinal);

        Assert.Contains(
            "SetWorkspace",
            GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "UpdateSidePanel"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_card_rows_open_the_details_tabs_on_request()
    {
        var document = GuiSource.Xml("Controls", "AgentSidePanelControl.xaml");
        Assert.Equal(
            "OnCardChangesRowClick",
            GuiSource.AttributeValue(GuiSource.Require(document, "CardChangesRow"), "Click"));
        Assert.Equal(
            "OnCardTasksRowClick",
            GuiSource.AttributeValue(GuiSource.Require(document, "CardTasksRow"), "Click"));
        Assert.DoesNotContain("CardChangesButton", GuiSource.XamlNames(document));
        Assert.DoesNotContain("CardTasksButton", GuiSource.XamlNames(document));

        var code = GuiSource.Text("Controls", "AgentSidePanelControl.xaml.cs");
        Assert.Contains(
            "TabOpenRequested",
            GuiSource.MethodBody(code, "OnCardChangesRowClick"),
            StringComparison.Ordinal);
        Assert.Contains(
            "OpenTab",
            GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "OnSidePanelTabOpenRequested"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_strip_reorders_tabs_by_dragging()
    {
        var document = GuiSource.Xml("Controls", "AgentSidePanelControl.xaml");
        foreach (var chip in new[] { "ChangesChip", "TasksChip", "FilesChip" })
        {
            var element = GuiSource.Require(document, chip);
            Assert.Equal("OnChipPointerPressed", GuiSource.AttributeValue(element, "PointerPressed"));
            Assert.Equal("OnChipPointerMoved", GuiSource.AttributeValue(element, "PointerMoved"));
            Assert.Equal("OnChipPointerReleased", GuiSource.AttributeValue(element, "PointerReleased"));
        }

        var code = GuiSource.Text("Controls", "AgentSidePanelControl.xaml.cs");
        Assert.Contains("TabMoved", GuiSource.MethodBody(code, "OnChipPointerMoved"), StringComparison.Ordinal);
        Assert.Contains("Move", GuiSource.MethodBody(code, "SyncChipOrder"), StringComparison.Ordinal);
        Assert.Contains(
            "MoveTab",
            GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "OnSidePanelTabMoved"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_collapse_control_draws_the_panel_glyph()
    {
        Assert.Contains("M4.5,3.5", GuiSource.Text("Controls", "AgentSidePanelControl.xaml"), StringComparison.Ordinal);
        Assert.Contains("M4.5,3.5", GuiSource.Text("MainWindow.xaml"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_add_menu_opens_or_reopens_every_panel_tab()
    {
        var document = GuiSource.Xml("Controls", "AgentSidePanelControl.xaml");
        GuiSource.Require(document, "TerminalChip");
        GuiSource.Require(document, "TerminalBody");
        GuiSource.Require(document, "TerminalTabView");

        var code = GuiSource.Text("Controls", "AgentSidePanelControl.xaml.cs");
        var add = GuiSource.Require(document, "AddTabButton");
        var menuItems = add.Descendants().Where(element => element.Name.LocalName == "MenuFlyoutItem").ToArray();
        Assert.Equal(new[] { "Files", "Changes", "Tasks", "Subagents", "Preview", "Terminal" }, menuItems.Select(element => GuiSource.AttributeValue(element, "Tag")));
        Assert.All(menuItems, element => Assert.Equal("OnOpenPanelTabClick", GuiSource.AttributeValue(element, "Click")));
        Assert.Contains("OpenPanelTab", GuiSource.MethodBody(code, "OnOpenPanelTabClick"), StringComparison.Ordinal);

        Assert.Contains(
            "ConPtyTerminalSession.Start",
            GuiSource.Text("Controls", "InteractiveTerminalControl.xaml.cs"),
            StringComparison.Ordinal);
        Assert.Contains(
            "SetTerminalWorkingDirectory",
            GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "UpdateSidePanel"),
            StringComparison.Ordinal);
        Assert.Contains(
            "ShutdownTerminal",
            GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "Dispose"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_terminal_tab_mark_is_the_prompt_itself()
    {
        // The boxed CommandPrompt glyph read as a badge at chip size, out of step
        // with the folder and pencil line weight. The tab draws its mark as the
        // literal prompt in the app's mono font instead, on a fixed slot so the
        // labels of every chip stay aligned.
        var document = GuiSource.Xml("Controls", "AgentSidePanelControl.xaml");
        var chip = GuiSource.Require(document, "TerminalChip");

        Assert.DoesNotContain(
            chip.Descendants().Select(element => GuiSource.AttributeValue(element, "Glyph")),
            glyph => glyph is "\uE756");

        var mark = chip.Descendants().Single(element => GuiSource.AttributeValue(element, "Text") == ">_");
        Assert.Equal(18, GuiSource.IntAttribute(mark, "Width"));
        Assert.Contains(
            "RuMonoFontFamily",
            GuiSource.AttributeValue(mark, "FontFamily"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_title_bar_toggle_sits_beside_the_caption_buttons_and_forwards_input()
    {
        var document = GuiSource.Xml("MainWindow.xaml");
        GuiSource.Require(document, "SidePanelToggleButton");
        var update = GuiSource.Require(document, "TitleBarUpdateButton");
        Assert.Equal("OnTitleBarUpdateClick", GuiSource.AttributeValue(update, "Click"));

        var code = GuiSource.ViewText("MainWindow");
        string layout = GuiSource.MethodBody(code, "UpdateTitleBarToggleLayout");
        Assert.Contains("RightInset", layout, StringComparison.Ordinal);
        Assert.Contains("RasterizationScale", layout, StringComparison.Ordinal);
        Assert.Contains("TitleBar.Height", layout, StringComparison.Ordinal);

        string region = GuiSource.MethodBody(code, "ApplySidePanelToggleRegion");
        Assert.Contains("SetRegionRects", region, StringComparison.Ordinal);
        Assert.Contains("TitleBarActions", region, StringComparison.Ordinal);
        Assert.Contains("ShowUpdates", GuiSource.MethodBody(code, "OnTitleBarUpdateClick"), StringComparison.Ordinal);

        Assert.Contains(
            "ToggleSidePanel",
            GuiSource.MethodBody(code, "OnSidePanelToggleClick"),
            StringComparison.Ordinal);
        Assert.Contains(
            "ForgetSidePanelState",
            GuiSource.MethodBody(code, "DeleteSessionAsync"),
            StringComparison.Ordinal);

        string forget = GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "ForgetSidePanelState");
        Assert.Contains("Remove", forget, StringComparison.Ordinal);
    }
}
