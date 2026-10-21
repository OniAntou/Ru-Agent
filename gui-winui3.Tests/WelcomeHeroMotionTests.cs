using System;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The empty-state hero's content and motion contract: the workspace readout,
/// the greeting, and bounded reduced-motion-aware transitions.
/// </summary>
public sealed class WelcomeHeroMotionTests
{
    [Fact]
    public void The_welcome_hero_keeps_the_workspace_line_title_and_a_setup_link_only_when_needed()
    {
        // The subtitle and the three starter chips were removed by owner
        // direction: the empty state keeps the workspace readout and the
        // greeting. The one later addition (owner direction, 2026-10-22) is the
        // provider-setup link, shown only before any model is chosen.
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        foreach (var name in new[]
        {
            "WelcomeHeroPanel", "WelcomeWorkspaceRow", "WelcomeWorkspaceText", "WelcomeTitleText", "WelcomeSetupRow",
        })
        {
            GuiSource.Require(document, name);
        }

        Assert.Equal(
            "{x:Bind ModelSelection.NeedsProviderSetup, Mode=OneWay}",
            GuiSource.AttributeValue(GuiSource.Require(document, "WelcomeSetupRow"), "Visibility"));
        Assert.Contains(
            "ProviderSettingsRequested",
            GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "OnWelcomeSetupClick"),
            StringComparison.Ordinal);

        var named = GuiSource.NamedElements(document);
        Assert.DoesNotContain("WelcomeSubtitleText", named.Keys);
        Assert.DoesNotContain("SuggestionPanel", named.Keys);
        Assert.DoesNotContain("OnSuggestionClick", GuiSource.ViewText("ChatView"), StringComparison.Ordinal);
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
