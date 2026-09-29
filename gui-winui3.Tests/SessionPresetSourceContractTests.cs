using Xunit;

namespace RuApp.Tests;

/// <summary>
/// A session's permission preset is session-scoped and user-owned, so neither
/// opening a session nor resetting the turn may narrow it silently.
/// </summary>
public sealed class SessionPresetSourceContractTests
{
    // Anchored on the declaration rather than the name. MethodBody matches the
    // first textual occurrence, and every method asserted here is also called
    // earlier in its own file; slicing to end of file instead would reach into
    // unrelated members, so the slice stops at the next one.
    private static string Body(string file, string declaration)
    {
        string code = GuiSource.Text("Views", file);
        int start = code.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{declaration} not found in {file}");
        int next = code.IndexOf("\n        private ", start + declaration.Length, StringComparison.Ordinal);
        int nextPublic = code.IndexOf("\n        public ", start + declaration.Length, StringComparison.Ordinal);
        if (nextPublic >= 0 && (next < 0 || nextPublic < next))
        {
            next = nextPublic;
        }

        return next < 0 ? code[start..] : code[start..next];
    }

    [Fact]
    public void Neither_the_session_open_nor_the_reset_narrows_the_preset()
    {
        // Both lines were the same defect in two places. OpenSessionAsync forced
        // the box to plan, and ResetAgentState forced the field back to
        // standard: a Full Access session reopened in the GUI came back
        // Standard, a Standard one came back Plan, and neither said so on
        // screen. The plan downgrade also reached the user as an unrelated
        // `protocol failure` instead of `plan_preset_requires_approval`.
        Assert.DoesNotContain(
            "AgentPresetBox.SelectedIndex = 0",
            Body("ChatView.SessionHistory.cs", "public async Task OpenSessionAsync"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "_agentPreset = AgentPermissionPreset.Standard",
            Body("ChatView.AgentRun.cs", "private void ResetAgentState"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Standard_still_starts_as_the_default()
    {
        // The removed reset had a job -- it set a default. That default now lives
        // where a default belongs: the field initialiser and the XAML
        // SelectedIndex, so nothing has to rewrite it on the way in.
        Assert.Contains(
            "private AgentPermissionPreset _agentPreset = AgentPermissionPreset.Standard;",
            GuiSource.ViewText("ChatView"),
            StringComparison.Ordinal);
        Assert.Equal(
            "1",
            GuiSource.AttributeValue(
                GuiSource.Require(GuiSource.Xml("Views", "ChatView.xaml"), "AgentPresetBox"),
                "SelectedIndex"));
    }

    [Fact]
    public void A_transient_composer_message_does_not_outlive_the_action_it_described()
    {
        // "Submitting approval…" went into the composer-local channel, which
        // UpdateAgentInlineState replays over the run's own status on every
        // refresh. Nothing cleared it, so a finished turn kept reading as if an
        // approval were still being submitted.
        Assert.Contains(
            "ClearComposerStatus()",
            Body("ChatView.SessionHistory.cs", "private async Task RunAgentActionAsync"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_status_before_a_turn_starts_is_not_written_into_the_composer_channel()
    {
        // "Preparing action…" described the run, not the composer, and the
        // composer channel is the one that outlives every refresh.
        Assert.DoesNotContain(
            "ShowAgentInlineStatus(\"Preparing action",
            Body("ChatView.AgentRun.cs", "private async Task<bool> SendAgentTurnAsync"),
            StringComparison.Ordinal);
    }
}
