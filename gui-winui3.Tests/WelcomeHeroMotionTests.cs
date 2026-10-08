using System;
using System.Linq;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The empty-state hero's content and motion contract: a workspace readout,
/// three real starter prompts, and bounded reduced-motion-aware transitions.
/// </summary>
public sealed class WelcomeHeroMotionTests
{
    [Fact]
    public void The_welcome_hero_carries_the_workspace_line_and_starter_prompts()
    {
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        foreach (var name in new[]
        {
            "WelcomeHeroPanel", "WelcomeWorkspaceRow", "WelcomeWorkspaceText",
            "WelcomeTitleText", "WelcomeSubtitleText", "SuggestionPanel",
        })
        {
            GuiSource.Require(document, name);
        }

        var suggestionPanel = GuiSource.Require(document, "SuggestionPanel");
        var chips = suggestionPanel.Descendants()
            .Where(element => GuiSource.AttributeValue(element, "Click") == "OnSuggestionClick")
            .ToList();

        Assert.Equal(3, chips.Count);
        foreach (var chip in chips)
        {
            Assert.False(string.IsNullOrWhiteSpace(GuiSource.AttributeValue(chip, "Tag")));
        }
    }

    [Fact]
    public void The_hero_motion_is_bounded_and_reduced_motion_aware()
    {
        var code = GuiSource.ViewText("ChatView");
        string hero = GuiSource.MethodBody(code, "UpdateWelcomeHero");
        Assert.Contains("UiTiming.AnimationsEnabled", hero, StringComparison.Ordinal);
        Assert.Contains("PlayWelcomeHeroEntrance", hero, StringComparison.Ordinal);

        string entrance = GuiSource.MethodBody(code, "PlayWelcomeHeroEntrance");
        Assert.Contains("BeginTime", entrance, StringComparison.Ordinal);

        Assert.Contains(
            "AnimationsEnabled",
            GuiSource.MethodBody(GuiSource.Text("Controls", "WelcomeOverlay.cs"), "SetStatusText"),
            StringComparison.Ordinal);
    }
}
