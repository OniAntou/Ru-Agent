using System.Xml.Linq;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Chronological bubble contract: segmented messages render prose slices and
/// activity rows in arrival order, while a message without segments keeps the
/// legacy feed-above-text layout.
///
/// Asserted against the markup tree and the declared resources, so the test fails
/// when the template wiring actually breaks rather than when an attribute moves.
/// </summary>
public sealed class AssistantSegmentContractTests
{
    [Fact]
    public void Assistant_list_selects_a_template_per_segment()
    {
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        var keys = DeclaredKeys(document);

        Assert.Contains("AssistantSegmentSelector", keys);
        Assert.Contains("AssistantTextSegmentTemplate", keys);
        Assert.Contains("AssistantActivitySegmentTemplate", keys);

        // The list must actually point at the selector, otherwise the templates
        // above are dead markup.
        Assert.Contains(
            document.Descendants().Attributes(),
            attribute => attribute.Value.Contains(
                "AssistantSegmentSelector",
                StringComparison.Ordinal)
                && attribute.Name.LocalName is "ItemTemplateSelector");
    }

    [Fact]
    public void Legacy_feed_and_text_hide_once_a_message_has_segments()
    {
        // The exact behaviour, exercised on the model the markup binds to: the
        // legacy surfaces exist for a message without segments and yield as soon as
        // one arrives.
        var message = new ChatMessageItem
        {
            Role = "assistant",
            Content = "prose",
        };

        Assert.False(message.HasSegments);
        Assert.True(message.ShowLegacyText);

        message.Segments = new System.Collections.ObjectModel.ObservableCollection<AssistantSegment>
        {
            new TextSegment("prose"),
        };

        Assert.True(message.HasSegments);
        Assert.False(message.ShowLegacyText);
        Assert.False(message.ShowLegacyActivityFeed);
    }

    [Fact]
    public void The_legacy_feed_only_shows_when_activity_exists_and_segments_do_not()
    {
        var withActivity = new ChatMessageItem { Role = "assistant" };
        withActivity.AgentActivities = new System.Collections.ObjectModel.ObservableCollection<AgentActivityItem>();
        withActivity.AgentActivities.Add(new AgentActivityItem(new AgentActivityModel(
            "act_11111111111111111111111111111111",
            AgentActivityKind.Analyze,
            AgentActivityState.Completed,
            FileName: null,
            LineStart: null,
            LineEnd: null,
            Symbol: null,
            Count: null,
            Added: null,
            Removed: null,
            DurationMs: null,
            SafeCode: "succeeded",
            ToolDisplay: null,
            DurableSequence: null,
            TransientSequence: 1)));

        Assert.True(withActivity.ShowLegacyActivityFeed);

        withActivity.Segments = new System.Collections.ObjectModel.ObservableCollection<AssistantSegment>
        {
            new TextSegment("now segmented"),
        };

        Assert.False(withActivity.ShowLegacyActivityFeed);
    }

    [Fact]
    public void Activity_segment_reuses_the_shared_activity_template()
    {
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        var keys = DeclaredKeys(document);

        Assert.Contains("AgentActivityTemplate", keys);

        // One row renderer for the feed and the inline segment: duplicating it would
        // let the two presentations drift apart.
        Assert.Contains(
            document.Descendants().Attributes(),
            attribute => attribute.Name.LocalName == "ContentTemplate"
                && attribute.Value.Contains("AgentActivityTemplate", StringComparison.Ordinal));
    }

    private static HashSet<string> DeclaredKeys(XDocument document) =>
        document.Descendants()
            .Select(element => element.Attribute(GuiSource.Xaml + "Key")?.Value)
            .Where(key => key is not null)
            .Select(key => key!)
            .ToHashSet(StringComparer.Ordinal);
}
