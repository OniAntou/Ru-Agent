using System.Linq;
using RuAgent.Protocol.Harness;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>The conversation header: session title and a read-only permission label.</summary>
public sealed class ChatHeaderTests
{
    [Theory]
    [InlineData(null, "New Chat")]
    [InlineData("   ", "New Chat")]
    [InlineData("  Review the CLI  ", "Review the CLI")]
    public void The_title_falls_back_for_an_untitled_session(string? title, string expected)
    {
        Assert.Equal(expected, ChatHeaderPresentation.Title(title));
    }

    [Theory]
    [InlineData(AgentPermissionPreset.Plan, "Plan", "RuInfoBrush")]
    [InlineData(AgentPermissionPreset.Standard, "Standard", "RuSuccessBrush")]
    [InlineData(AgentPermissionPreset.FullAccess, "Full Access", "RuWarningBrush")]
    public void The_permission_label_uses_the_Settings_names_and_says_where_to_change_it(
        AgentPermissionPreset preset, string label, string dotBrush)
    {
        var permission = ChatHeaderPresentation.Permission(preset);
        Assert.Equal(label, permission.Label);
        Assert.Equal(dotBrush, permission.DotBrushKey);
        Assert.Contains("Settings", permission.ToolTip);
        Assert.Contains("Ctrl+K", permission.ToolTip);
    }

    [Theory]
    [InlineData(@"D:\LLM", "LLM")]
    [InlineData(@"D:\Work\ru-agent\", "ru-agent")]
    [InlineData(@"D:\", "D:")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void The_breadcrumb_starts_at_the_workspace_folder_name(string? workspace, string expected)
    {
        Assert.Equal(expected, ChatHeaderPresentation.WorkspaceName(workspace));
    }

    [Fact]
    public void The_header_sits_above_the_transcript_and_offers_no_control()
    {
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        var header = GuiSource.Require(document, "ChatHeader");
        var root = GuiSource.Require(document, "RootGrid");
        Assert.Same(root, header.Parent);
        Assert.Equal("Auto,*,Auto", root.Attribute("RowDefinitions")?.Value);
        Assert.Equal("0", header.Attribute("Grid.Row")?.Value);
        Assert.Equal("1", GuiSource.Require(document, "ReadingColumn").Attribute("Grid.Row")?.Value);
        Assert.Equal("2", GuiSource.Require(document, "InputContainer").Attribute("Grid.Row")?.Value);

        GuiSource.Require(document, "ChatHeaderTitle");
        GuiSource.Require(document, "ChatHeaderWorkspace");
        GuiSource.Require(document, "ChatHeaderRunStatus");
        var permission = GuiSource.Require(document, "ChatHeaderPermission");
        Assert.True(GuiSource.IsDescendantOf(permission, header));
        var controlNames = new[] { "Button", "ToggleButton", "ComboBox", "DropDownButton", "SplitButton", "ToggleSwitch", "MenuFlyout" };
        Assert.DoesNotContain(header.DescendantsAndSelf(), element => controlNames.Contains(element.Name.LocalName));
        Assert.DoesNotContain(header.DescendantsAndSelf(), element => element.Attributes().Any(attribute =>
            attribute.Name.LocalName is "Click" or "Tapped" or "PointerPressed"));
    }

    [Fact]
    public void The_header_follows_the_transcript_and_the_preset()
    {
        var code = GuiSource.Text("Views", "ChatView.Header.cs");
        Assert.Contains("ChatHeader.Visibility = ChatContentGrid.Visibility", code);
        Assert.Contains("nameof(SessionPresetViewModel.Preset)", code);
        Assert.Contains("nameof(AgentRunPanelViewModel.IsRunning)", code);
        // The label carries a risk-coloured dot, not a lock glyph.
        Assert.DoesNotContain("E72E", GuiSource.Text("Services", "ChatHeaderPresentation.cs"));
        Assert.DoesNotContain("&#xE72E;", GuiSource.Text("Views", "ChatView.xaml"));
        Assert.Contains("UpdateChatHeaderTitle();", GuiSource.MethodBody(GuiSource.Text("Views", "ChatView.Tasks.cs"), "UpdateSessionTitle"));
    }
}
