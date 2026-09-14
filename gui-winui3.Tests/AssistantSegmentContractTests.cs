using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Chronological bubble contract: segmented messages render prose slices and
/// activity rows in arrival order; legacy messages keep the old feed-above-text
/// layout. Guards the template against reordering regressions.
/// </summary>
public sealed class AssistantSegmentContractTests
{
    [Fact]
    public void Assistant_template_renders_segments_with_selector()
    {
        var xaml = File.ReadAllText(FindGuiSource("Views/ChatView.xaml"));

        Assert.Contains("AssistantSegmentSelector", xaml);
        Assert.Contains("AssistantTextSegmentTemplate", xaml);
        Assert.Contains("AssistantActivitySegmentTemplate", xaml);
        Assert.Contains("ItemTemplateSelector=\"{StaticResource AssistantSegmentSelector}\"", xaml);
    }

    [Fact]
    public void Legacy_feed_and_text_hide_when_segments_take_over()
    {
        var xaml = File.ReadAllText(FindGuiSource("Views/ChatView.xaml"));

        Assert.Contains("ShowLegacyActivityFeed", xaml);
        Assert.Contains("ShowLegacyText", xaml);
    }

    [Fact]
    public void Activity_segment_reuses_activity_template()
    {
        var xaml = File.ReadAllText(FindGuiSource("Views/ChatView.xaml"));

        Assert.Contains("ContentTemplate=\"{StaticResource AgentActivityTemplate}\"", xaml);
    }

    private static string FindGuiSource(string relative)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string sourceRoot = Path.Combine(directory.FullName, "gui-winui3");
            if (Directory.Exists(sourceRoot))
            {
                return Path.Combine(sourceRoot, relative);
            }
        }

        throw new DirectoryNotFoundException("gui-winui3 source root was not found.");
    }
}
