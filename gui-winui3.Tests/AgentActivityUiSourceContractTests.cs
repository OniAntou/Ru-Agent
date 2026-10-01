using System.Reflection;
using System.Xml.Linq;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using RuApp.Views;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Agent activity feed and terminal panel.
///
/// The feed's contract is about which state and metadata reach the screen, so it is
/// asserted on the item model and on the template's bindings rather than on the text
/// of the code-behind. The terminal block's bound is asserted by feeding it more than
/// it may keep.
/// </summary>
public sealed class AgentActivityUiSourceContractTests
{
    [Fact]
    public void The_feed_lives_inside_the_transcript_scroll_host()
    {
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        var contentGrid = GuiSource.Require(document, "ChatContentGrid");
        var list = GuiSource.Require(document, "ChatListView");

        // One scroll host, inside the transcript list, and the feed lives inside it:
        // the feed never grows a second scroller.
        Assert.True(GuiSource.IsDescendantOf(list, contentGrid));
        Assert.Equal("Auto", GuiSource.AttributeValue(list, "VerticalScrollBarVisibility"));
        Assert.Equal("Disabled", GuiSource.AttributeValue(list, "HorizontalScrollBarVisibility"));

        var feed = document.Descendants()
            .FirstOrDefault(element => GuiSource.AttributeValue(element, "AutomationId") == "AgentActivityFeed");
        Assert.True(feed is not null, "AgentActivityFeed must exist.");
        Assert.Equal("ItemsControl", feed!.Name.LocalName);
    }

    [Fact]
    public void The_assistant_template_renders_the_feed_above_its_content()
    {
        var document = GuiSource.Xml("Views", "ChatView.xaml");

        var template = document.Descendants()
            .FirstOrDefault(element => element.Attribute(GuiSource.Xaml + "Key")?.Value == "AssistantMessageTemplate");
        Assert.True(template is not null, "AssistantMessageTemplate must exist.");

        var feed = template!.Descendants()
            .FirstOrDefault(element => GuiSource.AttributeValue(element, "AutomationId") == "AgentActivityFeed");
        Assert.True(feed is not null, "AgentActivityFeed must belong to the assistant template.");

        var markdown = template.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "MarkdownBlock");
        Assert.True(markdown is not null, "The assistant template must render MarkdownBlock.");

        Assert.True(
            GuiSource.DocumentOrder(document, feed!) < GuiSource.DocumentOrder(document, markdown!),
            "Assistant content must follow the activity feed.");
    }

    [Fact]
    public void The_feed_template_binds_only_safe_state_and_metadata()
    {
        var template = ActivityTemplate();

        foreach (var state in new[] { "IsRunning", "IsCompleted", "IsFailed", "IsWaiting" })
        {
            Assert.True(Binds(template, state), $"AgentActivityTemplate must bind {state}.");
        }

        Assert.True(Binds(template, "MetadataText"), "AgentActivityTemplate must bind MetadataText.");

        // The pill is words only. It used to open with a document glyph, which said
        // nothing the file name beside it did not — and it was the one mark the row
        // painted in a muted tone next to text that already reads as metadata.
        var pillIcons = template.Descendants().Where(element => element.Name.LocalName == "FontIcon");
        Assert.DoesNotContain(
            pillIcons,
            element => GuiSource.AttributeValue(element, "Glyph")?.Contains("FileIconGlyph", StringComparison.Ordinal) == true);

        // The live region is on the row root inside the template, where the state
        // actually changes.
        Assert.Contains(
            template.DescendantsAndSelf(),
            element => GuiSource.AttributeValue(element, "LiveSetting") == "Polite");
    }

    [Fact]
    public void The_feed_template_binds_bounded_detail_and_edit_preview()
    {
        var template = ActivityTemplate();

        Assert.True(Binds(template, "Detail"));

        // The plain header and the plain detail line are one decision: a thinking row
        // carries neither, because its deliberation lives in the collapsible quote
        // block instead. Binding the detail without the gate is what put a long
        // deliberation on the row and pushed the answer off screen.
        Assert.True(Binds(template, "ShowPlainDetail"));
        Assert.True(Binds(template, "ShowPlainHeader"));
        Assert.True(Binds(template, "EditBeforeText"));
        Assert.True(Binds(template, "EditAfterText"));

        // The row's other detail panel: what a terminal command printed, with the command
        // repeated above it so the panel stands on its own. Folded by the same state as the
        // diff, so the template has to reach both through the row.
        Assert.True(Binds(template, "TerminalPanelText"));
        Assert.True(Binds(template, "ShowTerminalOutput"));
        Assert.True(Binds(template, "ShowRowDetailToggle"));
    }

    [Fact]
    public void The_sweep_lookup_names_the_visible_header()
    {
        // The wave animation and the running-to-settled style updates target one
        // label per row shape. The mapping lives in code so it is unit-testable;
        // the names it returns are pinned to the template by the test below.
        Assert.Equal(
            "ThinkingLabel",
            ChatView.ActivityLabelElementName(
                Item(AgentActivityState.Started, kind: AgentActivityKind.Thinking, detail: "deliberation")));
        Assert.Equal(
            "EditPreviewLabel",
            ChatView.ActivityLabelElementName(
                Item(AgentActivityState.Started, kind: AgentActivityKind.Edit, detail: "@@ old ==> new")));
        Assert.Equal(
            "AgentActivityText",
            ChatView.ActivityLabelElementName(Item(AgentActivityState.Started)));
    }

    [Fact]
    public void The_activity_template_names_the_labels_the_sweep_lookup_needs()
    {
        // AttachActivityItem finds the running row's label by name. It used to
        // search by grid column, which broke silently when the header was
        // restructured and every Thinking row went static with no error.
        var template = ActivityTemplate();
        foreach (var name in new[] { "AgentActivityText", "ThinkingLabel", "EditPreviewLabel" })
        {
            Assert.Contains(
                template.Descendants(),
                element => element.Name.LocalName == "TextBlock"
                    && element.Attribute(GuiSource.Xaml + "Name")?.Value == name);
        }
    }

    [Fact]
    public void The_activity_item_carries_no_raw_payload_or_reasoning()
    {
        // The row is presentation only: anything the host keeps private must have no
        // property to travel in, and model reasoning must not surface here.
        var forbidden = new[]
        {
            "TransientData", "Command", "Arguments", "Diff",
            "Stdout", "Stderr", "Reasoning", "Payload",
        };

        var properties = typeof(AgentActivityItem)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToList();

        Assert.NotEmpty(properties);
        foreach (var name in forbidden)
        {
            Assert.DoesNotContain(properties, property => property.Contains(name, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void The_activity_row_has_no_status_icon()
    {
        // Tried and removed: the per-action glyphs did not read with the work.
        // Neither the markup nor the code may reference the icon again.
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        Assert.False(GuiSource.NamedElements(document).ContainsKey("AgentActivityIcon"));

        string code = GuiSource.ViewText("ChatView");
        Assert.DoesNotContain("ApplyActivityIcon", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ActionGlyph", code, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_row_carries_no_copy_affordance()
    {
        // The row used to offer "Copy error", which copied the headline plus the
        // machine code. The headline is now the wording the row ran with and the code
        // is off the row entirely, so the button had nothing of its own left to carry.
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        var template = document.Descendants().FirstOrDefault(element =>
            (string?)element.Attribute(GuiSource.Xaml + "Key") == "AgentActivityTemplate");

        Assert.NotNull(template);
        Assert.DoesNotContain(
            template!.Descendants(),
            element => (string?)element.Attribute(GuiSource.Xaml + "Name") == "CopyActivityErrorButton");

        Assert.False(GuiSource.HasMember(GuiSource.ViewText("ChatView"), "OnCopyActivityErrorClick"));
    }

    [Fact]
    public void Motion_respects_the_os_animation_setting()
    {
        // Sweep and entrance stay off when the user disables animations: the
        // running row falls back to a static tint.
        string code = GuiSource.ViewText("ChatView");

        string attach = GuiSource.MethodBody(code, "AttachActivityItem");
        Assert.Contains("AnimationsEnabled", attach, StringComparison.Ordinal);

        string sweep = GuiSource.MethodBody(code, "StartTextSweep");
        Assert.Contains("AnimationsEnabled", sweep, StringComparison.Ordinal);
    }

    [Fact]
    public void The_edit_preview_opens_while_unsettled_and_folds_when_done()
    {
        // A long diff must not shove the transcript while settled: open on
        // Started/Waiting, folded on Completed. The panel itself stretches the full
        // feed width, so it carries no cap to clamp a long line into.
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        var preview = document.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "FileEditPreviewControl");

        Assert.NotNull(preview);
        Assert.Null(GuiSource.AttributeValue(preview!, "MaxWidth"));
        Assert.Equal("Stretch", GuiSource.AttributeValue(preview!, "HorizontalAlignment"));

        // The panel draws only while the row says so, and the row header is the
        // toggle: the fold decision is the row's, not the view's. The header shows the
        // fold whenever the row has a detail at all — a diff or a command's output.
        Assert.Equal(
            "{x:Bind ShowEditPreview, Mode=OneWay, Converter={StaticResource BoolToVisibilityConverter}}",
            GuiSource.AttributeValue(preview!, "Visibility"));

        var toggle = GuiSource.Require(document, "EditPreviewToggle");
        Assert.Equal(
            "{x:Bind IsEditPreviewExpanded, Mode=TwoWay}",
            GuiSource.AttributeValue(toggle, "IsChecked"));
        Assert.Equal(
            "{x:Bind ShowRowDetailToggle, Mode=OneWay, Converter={StaticResource BoolToVisibilityConverter}}",
            GuiSource.AttributeValue(toggle, "Visibility"));

        string code = GuiSource.ViewText("ChatView");
        Assert.False(GuiSource.HasMember(code, "ApplyEditPreviewExpanded"));
    }

    [Fact]
    public void The_edit_panel_carries_no_header_of_its_own()
    {
        // The row above the panel already names the file and counts the changes; a
        // second header inside the panel repeated them and pushed the diff down. With
        // no header there is nothing left for the panel to be told, so the properties
        // that fed it are gone rather than left dangling.
        var properties = typeof(RuApp.Controls.FileEditPreviewControl)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToList();

        Assert.DoesNotContain("FileName", properties);
        Assert.DoesNotContain("AddedCount", properties);
        Assert.DoesNotContain("RemovedCount", properties);
        Assert.DoesNotContain("IsExpanded", properties);
        Assert.Contains("BeforeText", properties);
        Assert.Contains("AfterText", properties);
    }

    [Fact]
    public void A_waiting_row_announces_assertively_and_still_carries_its_state()
    {
        // Waiting is the moment the user must not miss, and it is announced rather
        // than decorated: the live setting turns assertive for a waiting row and
        // stays polite for every other one, set from code beside the label.
        //
        // The warning pill this used to assert no longer exists, so the check moved
        // to the announcement it was standing in for. What is asserted is that the
        // state still reaches the template and that the code still raises the live
        // setting - not which element wears the class.
        var template = ActivityTemplate();
        Assert.True(Binds(template, "IsWaiting"), "AgentActivityTemplate must bind IsWaiting.");

        string code = GuiSource.ViewText("ChatView");
        string attach = GuiSource.MethodBody(code, "AttachActivityItem");
        Assert.Contains("ApplyActivityLiveSetting", attach, StringComparison.Ordinal);

        string live = GuiSource.MethodBody(code, "ApplyActivityLiveSetting");
        Assert.Contains("Assertive", live, StringComparison.Ordinal);
        Assert.Contains("IsWaiting", live, StringComparison.Ordinal);
    }

    [Fact]
    public void The_approval_panel_floats_above_the_composer_instead_of_pushing_it()
    {
        // The approval card overlays the composer: showing it must not shove
        // the todo panel and the input upward. The wrapper sits in the root
        // grid (declared before the input, raised with a ZIndex); the panel
        // itself stays out of the input container's flow.
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        var panel = GuiSource.Require(document, "ApprovalPanel");
        var root = GuiSource.Require(document, "RootGrid");
        var inputContainer = GuiSource.Require(document, "InputContainer");

        Assert.False(GuiSource.IsDescendantOf(panel, inputContainer));

        var wrapper = Assert.IsType<System.Xml.Linq.XElement>(panel.Parent);
        Assert.Equal(root, wrapper.Parent);
        Assert.Equal("1", GuiSource.AttributeValue(wrapper, "Row"));
        Assert.Equal("Bottom", GuiSource.AttributeValue(wrapper, "VerticalAlignment"));
        Assert.Equal("20", GuiSource.AttributeValue(wrapper, "ZIndex"));
    }

    [Fact]
    public void Activity_lifecycle_flags_follow_the_wire_state()
    {
        var running = Item(AgentActivityState.Started);
        var completed = Item(AgentActivityState.Completed);
        var failed = Item(AgentActivityState.Failed);
        var waiting = Item(AgentActivityState.Waiting);

        Assert.True(running.IsRunning);
        Assert.False(running.IsCompleted);

        Assert.True(completed.IsCompleted);
        Assert.False(completed.IsRunning);

        Assert.True(failed.IsFailed);
        Assert.True(waiting.IsWaiting);

        // Every state has its own glyph, so the row reads without relying on colour.
        var glyphs = new[] { running, completed, failed, waiting }.Select(item => item.StatusGlyph).ToList();
        Assert.Equal(glyphs.Count, glyphs.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void An_interrupted_run_settles_a_running_row_instead_of_leaving_it_spinning()
    {
        var item = Item(AgentActivityState.Started);
        Assert.True(item.IsRunning);

        item.SettleInterruptedRun();

        Assert.False(item.IsRunning);
        Assert.False(item.IsCompleted);
    }

    [Fact]
    public void Detail_and_edit_preview_are_mutually_exclusive()
    {
        // A live edit row shows the before/after preview; other rows show the detail
        // line. Both at once would print the change twice.
        var edit = Item(
            AgentActivityState.Started,
            detail: "@@ using System; ==> using System.Text;",
            kind: AgentActivityKind.Edit);

        Assert.True(edit.IsEditPreview);
        Assert.True(edit.HasEditPreview);
        Assert.False(edit.HasDetail);
        Assert.Equal("using System;", edit.EditBeforeText);
        Assert.Equal("using System.Text;", edit.EditAfterText);

        var prose = Item(AgentActivityState.Completed, detail: "Considering folder scope.");
        Assert.False(prose.IsEditPreview);
        Assert.True(prose.HasDetail);

        // The marker decides the rendering, whatever the state carries it. In practice
        // only the Started frame gets a preview, but if a marker ever arrives on another
        // frame the row must render the two panes rather than print "@@ a ==> b" raw.
        var markerOnAnotherFrame = Item(
            AgentActivityState.Completed,
            detail: "@@ a ==> b",
            kind: AgentActivityKind.Edit);
        Assert.True(markerOnAnotherFrame.IsEditPreview);
        Assert.False(markerOnAnotherFrame.HasDetail);
    }

    [Fact]
    public void A_blank_detail_never_renders_an_empty_row()
    {
        Assert.False(Item(AgentActivityState.Completed, detail: "   ").HasDetail);
        Assert.False(Item(AgentActivityState.Completed, detail: null).HasDetail);
    }

    [Fact]
    public void The_terminal_panel_is_hosted_by_the_chat_view_and_owns_its_own_controls()
    {
        var chatView = GuiSource.Xml("Views", "ChatView.xaml");

        // The view hosts the panel; the panel itself lives in its own control.
        // x:Name is in the x: namespace, which AttributeValue deliberately skips, so
        // it is read directly here.
        Assert.Contains(
            chatView.Descendants(),
            element => element.Name.LocalName == "TerminalPanelControl"
                && element.Attribute(GuiSource.Xaml + "Name")?.Value is { Length: > 0 });

        var terminal = GuiSource.NamedElements(GuiSource.Xml("Controls", "TerminalPanelControl.xaml"));
        Assert.True(terminal.ContainsKey("Panel"), "The terminal control must own its panel.");
        Assert.True(terminal.ContainsKey("TerminalScrollHost"), "It must own its scroll host.");

        // Presented as terminal output for assistive technology.
        Assert.Equal("TerminalPanel", GuiSource.AttributeValue(terminal["Panel"], "AutomationId"));

        // Every element with an automation id, not only the named ones: the clear
        // button carries an id but no x:Name.
        var ids = GuiSource.Xml("Controls", "TerminalPanelControl.xaml").Descendants()
            .SelectMany(element => element.Attributes())
            .Where(attribute => attribute.Name.LocalName.EndsWith(".AutomationId", StringComparison.Ordinal))
            .Select(attribute => attribute.Value)
            .ToList();
        Assert.Contains("TerminalClearButton", ids);
        Assert.Contains("TerminalCollapseButton", ids);
    }

    [Fact]
    public void A_replayed_row_puts_its_wall_clock_time_on_the_row()
    {
        // The label has to sit with the row's metadata, not replace it: duration,
        // diff counts and safe code are what explain the step, the clock is only
        // what lets a reader line it up against the durable log.
        var template = ActivityTemplate();

        Assert.True(
            Binds(template, "OccurredAtLabel"),
            "AgentActivityTemplate must bind OccurredAtLabel.");
    }

    [Fact]
    public void The_terminal_block_offers_a_copy_of_its_own_output()
    {
        var template = ActivityTemplate();
        Assert.Contains(
            template.Descendants(),
            element => GuiSource.AttributeValue(element, "Click") == "OnCopyTerminalOutputClick");

        // The button copies the panel's text through its Tag rather than reaching
        // for the row, so one handler serves every terminal row in the feed.
        var copyButton = Assert.Single(template.Descendants().Where(element =>
            GuiSource.AttributeValue(element, "Click") == "OnCopyTerminalOutputClick"));
        Assert.Equal("Button", copyButton.Name.LocalName);
        Assert.True(
            copyButton.Attributes().Any(attribute =>
                attribute.Value.Contains("x:Bind TerminalPanelText", StringComparison.Ordinal)),
            "The copy button must carry the panel text it copies.");
    }

    [Fact]
    public void The_transcript_offers_one_density_toggle_and_nothing_else_float()
    {
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        // AttributeValue deliberately skips the x: namespace, so the name is read directly.
        var toggle = document.Descendants()
            .FirstOrDefault(element => element.Attribute(GuiSource.Xaml + "Name")?.Value == "DensityToggleButton");
        Assert.True(toggle is not null, "DensityToggleButton must exist.");
        Assert.Equal("ToggleButton", toggle!.Name.LocalName);
        Assert.Equal("OnDensityToggleClick", GuiSource.AttributeValue(toggle, "Click"));

        // It floats over the transcript rather than taking a row of its own, so it
        // has to live inside the content grid and not inside the message list.
        var grid = GuiSource.Require(document, "ChatContentGrid");
        var list = GuiSource.Require(document, "ChatListView");
        Assert.True(GuiSource.IsDescendantOf(toggle, grid));
        Assert.False(GuiSource.IsDescendantOf(toggle, list));

        // The label names the current mode, not the click, and code keeps it in
        // step with the saved preference: a label that changed on click would
        // leave the reader unable to tell which mode is on.
        var label = Assert.Single(toggle.Descendants().Where(element =>
            element.Attribute(GuiSource.Xaml + "Name")?.Value == "DensityToggleLabel"));
        Assert.Equal("Roomy", GuiSource.AttributeValue(label, "Text"));

        var chrome = GuiSource.ViewText("ChatView");
        Assert.Contains("DensityToggleLabel.Text", chrome, StringComparison.Ordinal);

        // Every path that creates a row applies the saved density as it is built:
        // a replayed session opened in compact mode must not flash roomy rows.
        Assert.Contains(
            "ApplySavedDensity(item);",
            GuiSource.ViewText("ChatView"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_terminal_block_bounds_what_it_keeps()
    {
        var block = new TerminalOutputBlock("exec_1", "git status");

        // Feed it far more than the budget, one line at a time.
        for (var index = 0; index < 400; index++)
        {
            block.AppendText(new string('x', 1024) + "\n");
        }

        Assert.True(block.IsTruncated, "Overrunning the budget must mark the block truncated.");
        Assert.True(
            block.Text.Length <= TerminalOutputBlock.MaximumCharacters,
            $"Kept {block.Text.Length} characters, budget is {TerminalOutputBlock.MaximumCharacters}.");
        Assert.True(
            block.Text.Count(character => character == '\n') <= TerminalOutputBlock.MaximumLines,
            "Kept more lines than the block may show.");
        Assert.Equal("… output truncated", block.FooterText);
    }

    [Fact]
    public void The_terminal_block_completes_with_a_typed_state_and_wording()
    {
        var ok = new TerminalOutputBlock("exec_1", "build");
        ok.AppendText("done\n");
        ok.Complete(failed: false);

        Assert.True(ok.IsCompleted);
        Assert.False(ok.IsFailed);
        Assert.False(ok.IsTruncated);
        Assert.Equal(string.Empty, ok.FooterText);

        var bad = new TerminalOutputBlock("exec_2", "build");
        bad.Complete(failed: true);

        Assert.True(bad.IsCompleted);
        Assert.True(bad.IsFailed);
        Assert.NotEqual(ok.StateText, bad.StateText);
    }

    private static XElement ActivityTemplate()
    {
        var template = GuiSource.Xml("Views", "ChatView.xaml").Descendants()
            .FirstOrDefault(element => element.Attribute(GuiSource.Xaml + "Key")?.Value == "AgentActivityTemplate");

        Assert.True(template is not null, "AgentActivityTemplate must exist.");
        return template!;
    }

    /// <summary>
    /// Whether the subtree binds the member. <c>{x:Bind Member, Mode=OneWay}</c>
    /// arrives as an attribute literally named <c>Bind</c>, so the markup name is
    /// not the property name and the binding has to be read from the value.
    /// </summary>
    private static bool Binds(XElement root, string member) =>
        root.DescendantsAndSelf().Any(element =>
            element.Attributes().Any(attribute =>
                attribute.Value.Contains("x:Bind " + member, StringComparison.Ordinal)));

    private static AgentActivityItem Item(
        AgentActivityState state,
        string? detail = null,
        string? toolDisplay = null,
        AgentActivityKind kind = AgentActivityKind.Analyze) =>
        new(new AgentActivityModel(
            "act_11111111111111111111111111111111",
            kind,
            state,
            FileName: "ChatView.xaml.cs",
            LineStart: 1,
            LineEnd: 2,
            Symbol: null,
            Count: null,
            Added: null,
            Removed: null,
            DurationMs: null,
            SafeCode: "succeeded",
            ToolDisplay: toolDisplay,
            DurableSequence: null,
            TransientSequence: 1,
            Detail: detail));
}
