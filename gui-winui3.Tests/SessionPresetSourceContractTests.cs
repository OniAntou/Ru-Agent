using Xunit;

namespace RuApp.Tests;

/// <summary>
/// A session's permission preset is session-scoped and stored, so reopening it
/// must not silently narrow what the session may do.
/// </summary>
public sealed class SessionPresetSourceContractTests
{
    [Fact]
    public void Opening_a_session_does_not_force_the_preset_back_to_plan()
    {
        // This line read `AgentPresetBox.SelectedIndex = 0` on every session
        // open. Pinned to plan, it overrode the stored session preset and the
        // user's Settings choice, so a standard session came back read-only and
        // its first write was denied with `plan_preset_requires_approval` --
        // surfaced to the user as an unrelated `protocol failure`.
        string open = GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "OpenSessionAsync");

        Assert.DoesNotContain("AgentPresetBox.SelectedIndex = 0", open, StringComparison.Ordinal);
    }

    [Fact]
    public void The_default_still_comes_from_ResetAgentState_not_from_the_session_open()
    {
        // The removed line had a job -- set a default. The default belongs in
        // the reset that already owns the per-turn state, so removing the
        // override cannot leave the preset uninitialised.
        string reset = GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "ResetAgentState");

        Assert.Contains("_agentPreset = AgentPermissionPreset.Standard", reset, StringComparison.Ordinal);
    }
}
