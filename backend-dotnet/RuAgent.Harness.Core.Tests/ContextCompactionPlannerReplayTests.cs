using RuAgent.Harness.Core.Context;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

/// <summary>
/// A model that has to be sent its own reasoning back adds that reasoning to every assistant
/// step the request keeps. The planner measured what it kept and what it covered by the
/// entries alone, so a run of long reasoning looked small: it protected nearly all of it as
/// "recent" and stopped covering as soon as the entries fit. The compaction committed, the
/// request did not shrink, and the next step compacted again until the context limit ended
/// the run (a 300-step soak stopped at step ~55).
/// </summary>
public sealed class ContextCompactionPlannerReplayTests
{
    private const int Pairs = 40;

    private static ModelContextSnapshot History()
    {
        var entries = new List<ModelContextEntry>();
        for (var index = 0; index < Pairs; index++)
        {
            entries.Add(ModelContextEntry.Text($"user-{index}", index * 2 + 1, ModelContextRole.User, new string('u', 400)));
            entries.Add(ModelContextEntry.Assistant($"assistant-{index}", index * 2 + 2,
                new AssistantStep(new string('a', 400), [], AssistantFinishReason.Stop, 0)));
        }

        entries.Add(ModelContextEntry.Text("current", Pairs * 2 + 1, ModelContextRole.User, "current request"));
        var array = entries.ToArray();
        return new ModelContextSnapshot(array, ModelContextSnapshot.Canonicalize(array));
    }

    private static ContextCompactionPlan? Plan(ModelContextSnapshot snapshot, long replayedTokens)
    {
        var entryTokens = ModelContextSizeEstimator.EstimateTokens(snapshot);
        var request = entryTokens + replayedTokens;
        // The window is 40% of the request: the plan must cut well below what it holds.
        var usable = request * 4 / 10;
        var decision = new ContextBudgetDecision(request, usable * 2, usable, usable, usable * 62 / 100, true,
            ContextCompactionStrategy.Normal);
        return ContextCompactionPlanner.Plan(
            snapshot, decision, "compact-replay", ContextCompactionReason.Automatic,
            estimatedTokensOverride: request,
            retainRecentTokens: usable * 16 / 100,
            replayedTokens: replayedTokens);
    }

    private static int CoveredAssistantSteps(ContextCompactionPlan plan) =>
        plan.CoveredEntries.Count(entry => entry.Kind == ModelContextEntryKind.AssistantStep);

    [Fact]
    public void Passing_no_replayed_reasoning_changes_nothing()
    {
        var snapshot = History();
        var entryTokens = ModelContextSizeEstimator.EstimateTokens(snapshot);
        var usable = entryTokens / 2;
        var decision = new ContextBudgetDecision(entryTokens, usable * 2, usable, usable, usable * 62 / 100, true,
            ContextCompactionStrategy.Normal);

        var without = ContextCompactionPlanner.Plan(
            snapshot, decision, "compact-replay", ContextCompactionReason.Automatic,
            estimatedTokensOverride: entryTokens, retainRecentTokens: usable * 16 / 100);
        var withZero = ContextCompactionPlanner.Plan(
            snapshot, decision, "compact-replay", ContextCompactionReason.Automatic,
            estimatedTokensOverride: entryTokens, retainRecentTokens: usable * 16 / 100, replayedTokens: 0);

        Assert.NotNull(without);
        Assert.NotNull(withZero);
        Assert.Equal(
            without!.CoveredEntries.Select(entry => entry.SourceId),
            withZero!.CoveredEntries.Select(entry => entry.SourceId));
    }

    [Fact]
    public void Replayed_reasoning_makes_the_planner_cover_enough_steps_to_reach_the_target()
    {
        var snapshot = History();
        // Each of the 40 assistant steps replays about 12,500 tokens of reasoning (50 KB).
        const long replayed = Pairs * 12_500;

        var plan = Plan(snapshot, replayed);

        Assert.NotNull(plan);
        // The request is entries + 500,000 replayed; the target is 62% of 40% of that.
        // Covering a handful of steps cannot get there, so most of the 40 must be covered.
        Assert.InRange(CoveredAssistantSteps(plan!), 25, Pairs - 1);
        Assert.DoesNotContain(plan.CoveredEntries, entry => entry.SourceId == "current");
    }

    [Fact]
    public void The_most_recent_steps_are_kept_by_their_full_weight_not_the_entry_weight()
    {
        var snapshot = History();
        const long replayed = Pairs * 12_500;

        var plan = Plan(snapshot, replayed);

        Assert.NotNull(plan);
        // Recent history is protected up to 16% of the window measured with its reasoning:
        // at about 12,700 tokens a step that is a few steps, never the 30+ the entries alone
        // would have allowed.
        var keptSteps = Pairs - CoveredAssistantSteps(plan!);
        Assert.InRange(keptSteps, 1, 12);
    }

    [Fact]
    public void A_request_estimate_below_the_entry_estimate_still_allows_a_shrinking_plan()
    {
        // A calibrated request estimate can be smaller than the entries' own estimate. The
        // shrink check compared the projected entries against that request figure, so on a
        // long run of small steps every compaction was refused and history was later dropped
        // without a checkpoint.
        var snapshot = History();
        var entryTokens = ModelContextSizeEstimator.EstimateTokens(snapshot);
        var request = entryTokens / 2;
        var decision = new ContextBudgetDecision(request, entryTokens * 4, entryTokens * 2, entryTokens * 2,
            request / 2, true, ContextCompactionStrategy.Normal);

        var plan = ContextCompactionPlanner.Plan(
            snapshot, decision, "compact-window", ContextCompactionReason.Automatic,
            estimatedTokensOverride: request,
            retainRecentTokens: request / 4);

        Assert.NotNull(plan);
        Assert.True(plan!.AfterTokens < entryTokens);
        // A compaction intent refuses an "after" above its "before": both stay in one measure.
        Assert.InRange(plan.AfterTokens, 0, plan.BeforeTokens - 1);
        Assert.DoesNotContain(plan.CoveredEntries, entry => entry.SourceId == "current");
    }

    [Fact]
    public void More_replayed_reasoning_never_covers_fewer_steps()
    {
        var snapshot = History();
        var previous = -1;
        foreach (var replayed in new long[] { 100_000, 250_000, 500_000, 1_000_000 })
        {
            var plan = Plan(snapshot, replayed);
            var covered = plan is null ? 0 : CoveredAssistantSteps(plan);
            Assert.True(covered >= previous, $"replayed={replayed}: covered {covered} < previous {previous}");
            previous = covered;
        }
    }
}
