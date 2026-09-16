using System.Reflection;
using System.Xml.Linq;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Models;
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
        Assert.True(Binds(template, "HasDetail"));
        Assert.True(Binds(template, "HasEditPreview"));
        Assert.True(Binds(template, "EditBeforeText"));
        Assert.True(Binds(template, "EditAfterText"));
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

        // An edit row that already completed is no longer a live preview, so its
        // detail line shows again.
        var completedEdit = Item(
            AgentActivityState.Completed,
            detail: "@@ a ==> b",
            kind: AgentActivityKind.Edit);
        Assert.False(completedEdit.IsEditPreview);
        Assert.True(completedEdit.HasDetail);
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
