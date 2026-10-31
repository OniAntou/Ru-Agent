using System.Xml.Linq;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>The Customize page: skills and plugins, reached from under New Chat.</summary>
public sealed class CustomizePageContractTests
{
    [Fact]
    public void Customize_sits_directly_under_New_Chat_and_routes_to_its_page()
    {
        var document = GuiSource.Xml("MainWindow.xaml");
        var newChat = GuiSource.Require(document, "NavNewChat");
        var customize = GuiSource.Require(document, "NavCustomize");
        Assert.Same(newChat.Parent, customize.Parent);
        Assert.Same(newChat, customize.ElementsBeforeSelf().Last());
        Assert.Equal("customize", customize.Attribute("Tag")?.Value);
        Assert.Equal("OnNavClick", customize.Attribute("Click")?.Value);
        Assert.Equal(ShellDestination.Customize, ShellRoutePolicy.Resolve("customize"));
        Assert.True(ShellRoutePolicy.IsCustomizeActive("customize"));
        Assert.False(ShellRoutePolicy.IsCustomizeActive("chat"));
    }

    [Fact]
    public void The_page_has_a_skills_tab_and_a_plugins_tab_holding_computer_use()
    {
        var document = GuiSource.Xml("Views", "CustomizeView.xaml");
        Assert.Equal("Skills", GuiSource.Require(document, "SkillsTab").Attribute("Content")?.Value);
        Assert.Equal("Plugins", GuiSource.Require(document, "PluginsTab").Attribute("Content")?.Value);
        GuiSource.Require(document, "SkillsList");
        GuiSource.Require(document, "SearchBox");
        var computerUse = GuiSource.Require(document, "ComputerUseToggle");
        Assert.True(computerUse.Ancestors().Any(element => (string?)element.Attribute(GuiSource.Xaml + "Name") == "PluginsPanel"));
        Assert.Equal("OnComputerUseToggled", computerUse.Attribute("Toggled")?.Value);
    }

    [Fact]
    public void Shell_motion_moves_elements_by_translation_never_by_their_layout_offset()
    {
        // Animating Offset replaced the element's layout position: the content frame,
        // laid out right of the sidebar, was drawn from the window's left edge.
        var motion = GuiSource.Text("Services", "UiMotion.cs");
        Assert.DoesNotContain("\"Offset\"", motion, StringComparison.Ordinal);
        Assert.DoesNotContain(".Offset =", motion, StringComparison.Ordinal);
        Assert.Contains("SetIsTranslationEnabled(element, true)", motion, StringComparison.Ordinal);
        Assert.Contains("StartAnimation(TranslationProperty", motion, StringComparison.Ordinal);
    }

    [Fact]
    public void Computer_use_moved_out_of_Settings()
    {
        var settings = GuiSource.Xml("Views", "SettingsView.xaml");
        Assert.DoesNotContain(settings.Descendants(), element => (string?)element.Attribute(GuiSource.Xaml + "Name") == "ComputerUseToggle");
    }

    [Fact]
    public void Skills_command_and_Use_in_chat_cross_between_the_page_and_the_composer()
    {
        var chat = GuiSource.ViewText("ChatView");
        Assert.Contains("CustomizeRequested?.Invoke", chat, StringComparison.Ordinal);
        Assert.DoesNotContain("OnSkillsButtonClick", chat, StringComparison.Ordinal);
        Assert.Contains("public void InsertSkillCommand(string skillId)", chat, StringComparison.Ordinal);
    }
}
