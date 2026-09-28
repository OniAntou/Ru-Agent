using System.Linq;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Where a new activity row lands in the feed, and what the run says when it fails.
/// </summary>
/// <remarks>
/// Both of these are decisions that live in imperative view code, which cannot be
/// exercised here: a WinUI control cannot be constructed outside a running XAML
/// application, so asserting them behaviourally would mean asserting nothing. The
/// contract is therefore pinned on the class's source, the way the other ChatView
/// contracts in this suite are — which catches the regression, at the cost of being a
/// text match rather than a behavioural one.
/// </remarks>
public sealed class AgentActivityFeedOrderContractTests
{
    [Fact]
    public void A_new_activity_row_is_appended_and_never_walked_backwards()
    {
        string feed = GuiSource.ViewText("ChatView");

        // The rule that used to live here walked a Thinking row backwards to just below
        // the previous Thinking row, so "a settled Thinking row stays above the tool rows
        // that followed it". Nothing could benefit from it: the harness publishes one
        // Thinking activity per model step keyed by that step's id, when the step starts,
        // before any of the step's tools, so a step's own row is always inserted first
        // and the walk-back could only ever fire for a *later* step. It then put that
        // step's failure above the previous step's work, so a run that failed on step 2
        // read as though it had failed before the tool it had just run.
        Assert.DoesNotContain(
            "AgentActivities.Insert(",
            feed,
            System.StringComparison.Ordinal);

        Assert.DoesNotContain(
            "insertAt",
            feed,
            System.StringComparison.Ordinal);

        Assert.Contains(
            "AgentActivities.Add(item);",
            feed,
            System.StringComparison.Ordinal);
    }

    [Fact]
    public void The_run_summary_no_longer_appends_a_failure_card()
    {
        // The feed already says what happened: a run that fails on a model step leaves
        // a settled row reading "Failed: <reason>" among the rows above it. The card
        // repeated that as a separate block lower on the screen, and again after a
        // reload, so the same fact appeared twice and the second copy read as a newer
        // event than the work it explained.
        string feed = GuiSource.ViewText("ChatView");

        Assert.DoesNotContain(
            "TerminalFailureCardPolicy",
            feed,
            System.StringComparison.Ordinal);

        Assert.DoesNotContain(
            "Type **Continue** to resume",
            feed,
            System.StringComparison.Ordinal);
    }

    [Fact]
    public void The_provider_setup_card_survives_because_it_carries_an_action()
    {
        // Not the same thing as the run failure card, and deliberately untouched: it is
        // the only path that gets an unauthenticated user to the screen where a key is
        // added, and it carries a button to get there.
        string feed = GuiSource.ViewText("ChatView");

        Assert.Contains("ShowProviderSetupCard", feed, System.StringComparison.Ordinal);
        Assert.Contains(
            "AgentFailurePresentation.OpenProviderSettingsAction",
            feed,
            System.StringComparison.Ordinal);
    }
}
