using System.Linq;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentSidePanelPolicyTests
{
    [Fact]
    public void Defaults_are_the_card_with_both_tabs_open()
    {
        var policy = new AgentSidePanelPolicy();

        Assert.Equal(AgentSidePanelMode.Card, policy.GetMode("s1"));
        Assert.Equal(new[] { SidePanelTabKind.Changes, SidePanelTabKind.Tasks }, policy.GetOpenTabs("s1"));
        Assert.Equal(SidePanelTabKind.Changes, policy.GetActiveTab("s1"));
        Assert.False(policy.IsFullscreen("s1"));
    }

    [Fact]
    public void Expand_and_collapse_round_trip()
    {
        var policy = new AgentSidePanelPolicy();

        policy.ExpandToPanel("s1");
        Assert.Equal(AgentSidePanelMode.Panel, policy.GetMode("s1"));

        policy.CollapseToCard("s1");
        Assert.Equal(AgentSidePanelMode.Card, policy.GetMode("s1"));
    }

    [Fact]
    public void Toggle_hides_and_restores_the_previous_mode()
    {
        var policy = new AgentSidePanelPolicy();

        policy.ToggleVisibility("s1");
        Assert.Equal(AgentSidePanelMode.Hidden, policy.GetMode("s1"));
        policy.ToggleVisibility("s1");
        Assert.Equal(AgentSidePanelMode.Card, policy.GetMode("s1"));

        policy.ExpandToPanel("s1");
        policy.ToggleVisibility("s1");
        Assert.Equal(AgentSidePanelMode.Hidden, policy.GetMode("s1"));
        policy.ToggleVisibility("s1");
        Assert.Equal(AgentSidePanelMode.Panel, policy.GetMode("s1"));
    }

    [Fact]
    public void Hiding_from_fullscreen_restores_fullscreen()
    {
        var policy = new AgentSidePanelPolicy();
        policy.ExpandToPanel("s1");
        policy.ToggleFullscreen("s1");

        policy.ToggleVisibility("s1");
        policy.ToggleVisibility("s1");

        Assert.Equal(AgentSidePanelMode.Panel, policy.GetMode("s1"));
        Assert.True(policy.IsFullscreen("s1"));
    }

    [Fact]
    public void Closing_the_active_tab_selects_the_first_open_tab()
    {
        var policy = new AgentSidePanelPolicy();

        policy.CloseTab("s1", SidePanelTabKind.Changes);

        Assert.Equal(new[] { SidePanelTabKind.Tasks }, policy.GetOpenTabs("s1"));
        Assert.Equal(SidePanelTabKind.Tasks, policy.GetActiveTab("s1"));
    }

    [Fact]
    public void Closing_the_last_tab_collapses_to_the_card()
    {
        var policy = new AgentSidePanelPolicy();
        policy.ExpandToPanel("s1");

        policy.CloseTab("s1", SidePanelTabKind.Changes);
        policy.CloseTab("s1", SidePanelTabKind.Tasks);

        Assert.Empty(policy.GetOpenTabs("s1"));
        Assert.Equal(AgentSidePanelMode.Card, policy.GetMode("s1"));
    }

    [Fact]
    public void Expanding_with_no_open_tabs_seeds_changes()
    {
        var policy = new AgentSidePanelPolicy();
        policy.CloseTab("s1", SidePanelTabKind.Changes);
        policy.CloseTab("s1", SidePanelTabKind.Tasks);

        policy.ExpandToPanel("s1");

        Assert.Equal(new[] { SidePanelTabKind.Changes }, policy.GetOpenTabs("s1"));
        Assert.Equal(SidePanelTabKind.Changes, policy.GetActiveTab("s1"));
    }

    [Fact]
    public void Opening_a_closed_tab_adds_and_activates_it()
    {
        var policy = new AgentSidePanelPolicy();
        policy.CloseTab("s1", SidePanelTabKind.Tasks);

        policy.OpenTab("s1", SidePanelTabKind.Tasks);

        Assert.Contains(SidePanelTabKind.Tasks, policy.GetOpenTabs("s1"));
        Assert.Equal(SidePanelTabKind.Tasks, policy.GetActiveTab("s1"));
    }

    [Fact]
    public void Closing_resets_fullscreen()
    {
        var policy = new AgentSidePanelPolicy();
        policy.ExpandToPanel("s1");
        policy.ToggleFullscreen("s1");

        policy.CollapseToCard("s1");

        Assert.False(policy.IsFullscreen("s1"));
    }

    [Fact]
    public void Width_clamp_handles_bounds_and_non_finite_values()
    {
        Assert.Equal(280, AgentSidePanelPolicy.ClampWidth(10));
        Assert.Equal(560, AgentSidePanelPolicy.ClampWidth(9_999));
        Assert.Equal(420, AgentSidePanelPolicy.ClampWidth(420));
        Assert.Equal(AgentSidePanelPolicy.DefaultWidth, AgentSidePanelPolicy.ClampWidth(double.NaN));
        Assert.Equal(AgentSidePanelPolicy.DefaultWidth, AgentSidePanelPolicy.ClampWidth(double.PositiveInfinity));
    }

    [Fact]
    public void Retention_evicts_the_least_recently_touched_session()
    {
        var policy = new AgentSidePanelPolicy();
        for (var index = 0; index < AgentSidePanelPolicy.MaxRetainedSessions; index++)
        {
            policy.ExpandToPanel($"s{index}");
        }

        policy.ExpandToPanel("overflow");

        Assert.Equal(AgentSidePanelMode.Card, policy.GetMode("s0"));
        Assert.Equal(AgentSidePanelMode.Panel, policy.GetMode("s1"));
        Assert.Equal(AgentSidePanelMode.Panel, policy.GetMode("overflow"));
    }

    [Fact]
    public void Blank_session_ids_are_ignored()
    {
        var policy = new AgentSidePanelPolicy();

        policy.ExpandToPanel(null);
        policy.ExpandToPanel("  ");

        Assert.Equal(AgentSidePanelMode.Card, policy.GetMode(null));
        Assert.Equal(AgentSidePanelMode.Card, policy.GetMode("  "));
    }

    [Fact]
    public void Removing_a_session_drops_its_state()
    {
        var policy = new AgentSidePanelPolicy();
        policy.ExpandToPanel("s1");

        policy.Remove("s1");

        Assert.Equal(AgentSidePanelMode.Card, policy.GetMode("s1"));
    }
}
