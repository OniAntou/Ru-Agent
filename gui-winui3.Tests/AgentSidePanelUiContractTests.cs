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

        var code = GuiSource.Text("Controls", "AgentSidePanelControl.Tabs.cs");
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
            "OpenSidePanelTab(kind)",
            GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "OnSidePanelTabOpenRequested"),
            StringComparison.Ordinal);
        Assert.Contains(
            "_sidePanelPolicy.OpenTab(_activeSessionId, kind)",
            GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "OpenSidePanelTab"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_strip_reorders_tabs_by_dragging()
    {
        var document = GuiSource.Xml("Controls", "AgentSidePanelControl.xaml");
        foreach (var chip in new[] { "ChangesChip", "TasksChip", "FilesChip" })
        {
            var element = GuiSource.Require(document, chip);
            Assert.Equal(chip.Replace("Chip", string.Empty, StringComparison.Ordinal), GuiSource.AttributeValue(element, "Tag"));
        }

        var code = GuiSource.Text("Controls", "AgentSidePanelControl.Tabs.cs");
        var wiring = GuiSource.MethodBody(code, "InitializeTabInteractions");
        Assert.Contains("PointerPressedEvent", wiring, StringComparison.Ordinal);
        Assert.Contains("handledEventsToo: true", wiring, StringComparison.Ordinal);
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
        Assert.Contains("M2.5,3.5 H13.5 V12.5 H2.5 Z", GuiSource.Text("Controls", "AgentSidePanelControl.xaml"), StringComparison.Ordinal);
        Assert.Contains("M2.5,3.5 H13.5 V12.5 H2.5 Z", GuiSource.Text("MainWindow.xaml"), StringComparison.Ordinal);
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
    public void Sidebar_controls_live_at_the_left_title_bar_edge_and_right_chat_header_edge()
    {
        var document = GuiSource.Xml("MainWindow.xaml");
        Assert.DoesNotContain("SidePanelToggleButton", GuiSource.XamlNames(document));
        var navigation = GuiSource.Require(document, "NavigationSidebarToggleButton");
        Assert.Same(GuiSource.Require(document, "AppTitleBar"), navigation.Parent);
        Assert.Equal("Left", GuiSource.AttributeValue(navigation, "HorizontalAlignment"));
        var chat = GuiSource.Xml("Views", "ChatView.xaml");
        var toggle = GuiSource.Require(chat, "SidePanelToggleButton");
        Assert.True(GuiSource.IsDescendantOf(toggle, GuiSource.Require(chat, "ChatHeader")));
        Assert.Equal("Right", GuiSource.AttributeValue(toggle, "HorizontalAlignment"));
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
        Assert.Contains("NavigationSidebarToggleButton", region, StringComparison.Ordinal);
        // A refused non-client region update (E_INVALIDARG during relayout) is traced and
        // retried; the crash policy treats ArgumentException as fatal, so it must not escape.
        Assert.Contains("catch (Exception error) when (error is ArgumentException or System.Runtime.InteropServices.COMException)", region, StringComparison.Ordinal);
        Assert.Contains("ShowUpdates", GuiSource.MethodBody(code, "OnTitleBarUpdateClick"), StringComparison.Ordinal);

        Assert.Contains(
            "ToggleSidePanel",
            GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "OnSidePanelToggleClick"),
            StringComparison.Ordinal);
        Assert.Contains(
            "ForgetSidePanelState",
            GuiSource.MethodBody(code, "DeleteSessionAsync"),
            StringComparison.Ordinal);

        string forget = GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "ForgetSidePanelState");
        Assert.Contains("Remove", forget, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_card_does_not_add_separate_files_or_terminal_shortcuts()
    {
        var names = GuiSource.XamlNames(GuiSource.Xml("Controls", "AgentSidePanelControl.xaml"));
        Assert.DoesNotContain("CardFilesButton", names);
        Assert.DoesNotContain("CardTerminalButton", names);
    }

    [Fact]
    public void Every_tab_chip_shares_one_shape_and_drag_wiring()
    {
        var document = GuiSource.Xml("Controls", "AgentSidePanelControl.xaml");
        foreach (var name in new[] { "ChangesChip", "TasksChip", "SubagentsChip", "FilesChip", "PreviewChip", "TerminalChip" })
        {
            var chip = GuiSource.Require(document, name);
            Assert.Equal("{StaticResource AppControlCornerRadius}", GuiSource.AttributeValue(chip, "CornerRadius"));
            Assert.Equal("Transparent", GuiSource.AttributeValue(chip, "BorderBrush"));
            // Drag handlers are attached in code with handledEventsToo; a XAML copy runs them twice.
            Assert.Null(GuiSource.AttributeValue(chip, "PointerPressed"));
            Assert.Null(GuiSource.AttributeValue(chip, "PointerMoved"));
        }

        var code = GuiSource.Text("Controls", "AgentSidePanelControl.Tabs.cs");
        Assert.Contains("isActive || isHovered", GuiSource.MethodBody(code, "RefreshChip"), StringComparison.Ordinal);
        Assert.Equal("OnTabStripPointerWheelChanged",
            GuiSource.AttributeValue(GuiSource.Require(document, "TabStripScroll"), "PointerWheelChanged"));
    }

    [Fact]
    public void Code_previews_are_a_flat_numbered_pane_without_a_card()
    {
        var preview = GuiSource.Xml("Controls", "DocumentPreviewControl.xaml");
        var frame = GuiSource.Require(preview, "CodeFrame");
        Assert.Contains(GuiSource.Require(preview, "LineNumbers"), frame.Descendants());
        var text = GuiSource.Require(preview, "TextContent");
        Assert.Contains(text, frame.Descendants());
        Assert.Equal("NoWrap", GuiSource.AttributeValue(text, "TextWrapping"));
        // The pane reads as an editor, not a floating document: no fill, hairline or rounding.
        foreach (var chrome in new[] { "Background", "BorderBrush", "BorderThickness", "CornerRadius" })
        {
            Assert.Null(GuiSource.AttributeValue(frame, chrome));
        }

        // The horizontal bar belongs to the code's own two-axis scroller. Inside the code
        // column it shortened that column and hid the last line under the bar.
        var codeScroll = GuiSource.Require(preview, "CodeScroll");
        Assert.Contains(text, codeScroll.Descendants());
        Assert.Equal("Auto", GuiSource.AttributeValue(codeScroll, "HorizontalScrollBarVisibility"));
        var gutterScroll = GuiSource.Require(preview, "GutterScroll");
        Assert.Contains(GuiSource.Require(preview, "LineNumbers"), gutterScroll.Descendants());
        Assert.Equal("Hidden", GuiSource.AttributeValue(gutterScroll, "VerticalScrollBarVisibility"));
        Assert.False(GuiSource.IsDescendantOf(frame, GuiSource.Require(preview, "ContentScroll")),
            "The code pane owns its scrolling; the page scroller must not wrap it.");
        Assert.Contains("GutterScroll.ChangeView",
            GuiSource.MethodBody(GuiSource.Text("Controls", "DocumentPreviewControl.Viewer.cs"), "SyncCodeGutter"),
            StringComparison.Ordinal);

        Assert.Contains("CodePreviewModel.Build(text, _path)",
            GuiSource.Text("Controls", "DocumentPreviewControl.Rendering.cs"), StringComparison.Ordinal);

        // Both diff views name the file, so the edit is colored by its language.
        foreach (var source in new[] { GuiSource.Xml("Views", "ChatView.xaml"), GuiSource.Xml("Controls", "AgentSidePanelControl.xaml") })
        {
            var diff = Assert.Single(source.Descendants(), element => element.Name.LocalName == "FileEditPreviewControl");
            Assert.StartsWith("{x:Bind FileName", GuiSource.AttributeValue(diff, "SyntaxFile"), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Changes_rows_offer_file_actions_and_a_scratch_toggle()
    {
        var document = GuiSource.Xml("Controls", "AgentSidePanelControl.xaml");
        var clicks = document.Descendants().Select(element => GuiSource.AttributeValue(element, "Click")).ToHashSet();
        Assert.Contains("OnChangeOpenPreviewClick", clicks);
        Assert.Contains("OnChangeRevealClick", clicks);
        Assert.Contains("OnChangeCopyPathClick", clicks);
        Assert.Equal("OnShowScratchClick", GuiSource.AttributeValue(GuiSource.Require(document, "ShowScratchToggle"), "Click"));
        GuiSource.Require(document, "ChangesSummaryText");
        Assert.DoesNotContain("CardRecentFilesPanel", GuiSource.XamlNames(document));

        var code = GuiSource.Text("Controls", "AgentSidePanelControl.Changes.cs");
        // Actions resolve against the workspace root and never open a bare basename.
        Assert.Contains("ChangesTabFiles.Resolve(_workspacePath, row.RelativePath)",
            GuiSource.MethodBody(code, "OnChangeOpenPreviewClick"), StringComparison.Ordinal);
        Assert.Contains("ChangesTabFiles.Resolve(_workspacePath, row.RelativePath)",
            GuiSource.MethodBody(code, "OnChangeRevealClick"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_preview_header_names_the_file_and_offers_its_own_actions()
    {
        var preview = GuiSource.Xml("Controls", "DocumentPreviewControl.xaml");
        foreach (var name in new[]
                 {
                     "PreviewDetailLabel", "CopyPathButton", "RevealFileButton",
                     "PageToolbar", "PageInput", "PageTotalLabel", "ZoomToolbar", "ZoomInButton", "ZoomOutButton", "ZoomLabel",
                 })
        {
            GuiSource.Require(preview, name);
        }

        var code = GuiSource.Text("Controls", "DocumentPreviewControl.xaml.cs");
        Assert.Contains("ExplorerReveal.Reveal(path)", GuiSource.MethodBody(code, "OnRevealFile"), StringComparison.Ordinal);
        Assert.Contains("Clipboard.SetContent", GuiSource.MethodBody(code, "OnCopyPath"), StringComparison.Ordinal);
        // The header composes type, size and last change in one place.
        Assert.Contains("PreviewDetail.Compose", GuiSource.MethodBody(code, "ShowDetail"), StringComparison.Ordinal);

        // The Changes tab reveals through the same helper; a second copy would drift.
        Assert.Contains("ExplorerReveal.Reveal(path)",
            GuiSource.MethodBody(GuiSource.Text("Controls", "AgentSidePanelControl.Changes.cs"), "OnChangeRevealClick"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_preview_takes_its_source_types_from_the_highlighter_table()
    {
        // A language the highlighter colors must never be refused by the preview, so the
        // decision lives in one method that asks CodeHighlighter rather than a second list.
        var source = GuiSource.Text("Services", "FilePreviewSource.cs");
        Assert.Contains("CodeHighlighter.LanguageForFile(", GuiSource.MethodBody(source, "IsTextual"), StringComparison.Ordinal);
    }

    [Fact]
    public void Edit_diffs_are_flat_and_scroll_sideways_like_the_preview()
    {
        // The diff body is built in code, so the contract is read from its declarations:
        // it paints no surface of its own and a long line stays one row that scrolls
        // sideways, matching how the Preview tab reads code.
        var source = GuiSource.Text("Controls", "FileEditPreviewControl.cs");
        // No surface, card or code tone of its own: the diff lies on the panel like the
        // Preview tab's code does. The rows keep their own added/removed tints.
        Assert.DoesNotContain("RuBackdropCodeBrush", source, StringComparison.Ordinal);
        Assert.Contains("Content = scrollViewer;", source, StringComparison.Ordinal);
        foreach (var chrome in new[] { "CornerRadius", "BorderThickness", "BorderBrush" })
        {
            Assert.DoesNotContain(chrome + " =", source, StringComparison.Ordinal);
        }

        Assert.Contains("TextWrapping = TextWrapping.NoWrap", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TextWrapping.Wrap", source, StringComparison.Ordinal);
        Assert.Contains("HorizontalScrollMode = ScrollMode.Enabled", source, StringComparison.Ordinal);
        Assert.Contains("HorizontalScrollBarVisibility = ScrollBarVisibility.Auto", source, StringComparison.Ordinal);

        // The block draws its own unified header and tints every changed line, a new file
        // included, so it reads like the diffs other agents print.
        Assert.Contains("DiffEngine.Header(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("additionsOnly", source, StringComparison.Ordinal);

        // The gutter is sized to the widest number. A fixed 44 px column left a dead band
        // on the left of every diff, which read as an indent the code did not have.
        Assert.DoesNotContain("GridLength(44", source, StringComparison.Ordinal);
        Assert.Contains("double numberWidth", source, StringComparison.Ordinal);
        var panel = GuiSource.Xml("Controls", "AgentSidePanelControl.xaml");
        var hosted = Assert.Single(panel.Descendants(), element => element.Name.LocalName == "FileEditPreviewControl");
        Assert.Equal("0,2,0,6", GuiSource.AttributeValue(hosted, "Margin"));
    }

    [Fact]
    public void An_edit_without_a_recorded_preview_does_not_print_a_placeholder()
    {
        // A binary/image edit has no before/after text to show. The expanded entry used to
        // narrate that absence ("No preview was recorded for this edit."); absent evidence
        // is now simply omitted, leaving the edit's own label and counts.
        string markup = GuiSource.Text("Controls", "AgentSidePanelControl.xaml");
        Assert.DoesNotContain("No preview was recorded", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("HasNoPreview", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("HasNoPreview", GuiSource.Text("Models", "ChangesTabRow.cs"), StringComparison.Ordinal);

        // Expanding a file shows its newest recorded edit, drawn over the file on disk when
        // the view could read it.
        Assert.Contains("BeforeText=\"{x:Bind PreviewBeforeText, Mode=OneWay}\"", markup, StringComparison.Ordinal);
        Assert.Contains("AfterText=\"{x:Bind PreviewAfterText, Mode=OneWay}\"", markup, StringComparison.Ordinal);
        Assert.Contains("ContextText=\"{x:Bind CurrentText, Mode=OneWay}\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("ItemsSource=\"{x:Bind PreviewEdits", markup, StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{x:Bind IsExpanded, Mode=OneWay, Converter={StaticResource BoolToVisibilityConverter}}\"",
            markup,
            StringComparison.Ordinal);
    }
}
