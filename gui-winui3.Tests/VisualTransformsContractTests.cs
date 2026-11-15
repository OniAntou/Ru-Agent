using Xunit;

namespace RuApp.Tests;

/// <summary>
/// TransformToVisual throws ArgumentException (E_INVALIDARG) when the two elements are
/// not in one connected tree. Thrown from a SizeChanged/LayoutUpdated handler or a
/// layout-driven timer, XAML fails the process fast: Ru crashed fifty times this way
/// with nothing in its own log. Code on those paths goes through VisualTransforms.
/// </summary>
public sealed class VisualTransformsContractTests
{
    [Theory]
    [InlineData("Views/SettingsView.Motion.cs")]
    [InlineData("Views/ChatView.Scrolling.cs")]
    [InlineData("Views/ChatView.SlashCommands.cs")]
    [InlineData("Views/ChatView.ConversationWidth.cs")]
    [InlineData("Controls/AgentSidePanelControl.Tabs.cs")]
    [InlineData("Controls/DocumentPreviewControl.xaml.cs")]
    [InlineData("MainWindow.SidePanel.cs")]
    public void Layout_driven_code_never_calls_TransformToVisual_directly(string file)
    {
        var source = GuiSource.Text(file.Split('/'));
        Assert.DoesNotContain(".TransformToVisual(", source, StringComparison.Ordinal);
        Assert.Contains("VisualTransforms.TryTransform", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_helper_turns_only_the_detached_tree_failure_into_a_skipped_pass()
    {
        var helper = GuiSource.Text("Services", "VisualTransforms.cs");
        Assert.Contains("catch (ArgumentException)", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("catch (Exception", helper, StringComparison.Ordinal);
        Assert.Contains("MaximumTracedFailures", helper, StringComparison.Ordinal);
    }
}
