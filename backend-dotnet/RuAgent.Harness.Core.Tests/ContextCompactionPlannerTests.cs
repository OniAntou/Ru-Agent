using System.Text.Json;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Reconciliation;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed class ContextCompactionPlannerTests
{
    private static readonly RunId RunId = new("run_33333333333333333333333333333333");

    [Fact]
    public async Task Planner_preserves_protected_entries_and_is_deterministic()
    {
        var entries = new List<ModelContextEntry>
        {
            ModelContextEntry.Text("system", 1, ModelContextRole.System, "system instructions"),
            ModelContextEntry.Text("base:memory", 2, ModelContextRole.User, "durable memory"),
            ModelContextEntry.Text("old-a", 3, ModelContextRole.User, new string('a', 1_200)),
            ModelContextEntry.Text("old-b", 4, ModelContextRole.User, new string('b', 1_200)),
            ModelContextEntry.Text("old-c", 5, ModelContextRole.User, new string('c', 1_200)),
            ModelContextEntry.Text("current", 6, ModelContextRole.User, "current request"),
            ModelContextEntry.Assistant(
                "unfinished",
                7,
                new AssistantStep(
                    "tool call pending",
                    [new ModelToolCall("provider-call-1", "files.read", "1.0.0", JsonDocument.Parse("{}").RootElement)],
                    AssistantFinishReason.ToolCalls,
                    8)),
        };
        var snapshot = await AssembleAsync(entries.ToArray());
        var before = ModelContextSizeEstimator.EstimateTokens(snapshot);
        var decision = new ContextBudgetPolicy(new ContextBudgetPolicyOptions(0, 0))
            .Evaluate(before, 1_000, ContextCompactionStrategy.Normal);

        var first = ContextCompactionPlanner.Plan(
            snapshot,
            decision,
            "compact-33333333333333333333333333333333",
            ContextCompactionReason.Automatic);
        var second = ContextCompactionPlanner.Plan(
            snapshot,
            decision,
            "compact-33333333333333333333333333333333",
            ContextCompactionReason.Automatic);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.SummaryText, second.SummaryText);
        Assert.Equal(first.BeforeTokens, second.BeforeTokens);
        Assert.Equal(first.AfterTokens, second.AfterTokens);
        Assert.Equal(first.InputHash, second.InputHash);
        Assert.Equal(
            first.CoveredEntries.Select(entry => entry.SourceId),
            second.CoveredEntries.Select(entry => entry.SourceId));
        Assert.DoesNotContain(first!.CoveredEntries, entry =>
            entry.SourceId is "system" or "base:memory" or "current" or "unfinished");
        Assert.Contains(first.RemainingEntries, entry => entry.SourceId == "system");
        Assert.Contains(first.RemainingEntries, entry => entry.SourceId == "base:memory");
        Assert.Contains(first.RemainingEntries, entry => entry.SourceId == "current");
        Assert.Contains(first.RemainingEntries, entry => entry.SourceId == "unfinished");
        Assert.True(first.AfterTokens < first.BeforeTokens);
        Assert.Equal(64, first.InputHash.Length);
        Assert.All(first.InputHash, character => Assert.True(char.IsAsciiHexDigit(character)));
        Assert.Contains(first.ProjectedEntries, entry => entry.Kind == ModelContextEntryKind.Summary);
    }

    [Fact]
    public async Task Planner_returns_no_plan_when_context_is_at_or_below_target()
    {
        var snapshot = await AssembleAsync(
            ModelContextEntry.Text("current", 1, ModelContextRole.User, "small"));
        var estimate = ModelContextSizeEstimator.EstimateTokens(snapshot);
        var decision = new ContextBudgetPolicy(new ContextBudgetPolicyOptions(0, 0))
            .Evaluate(estimate, estimate + 100, ContextCompactionStrategy.Normal);

        var plan = ContextCompactionPlanner.Plan(
            snapshot,
            decision,
            "compact-44444444444444444444444444444444",
            ContextCompactionReason.Manual);

        Assert.Null(plan);
    }

    [Fact]
    public async Task Planner_preserves_current_run_prompt_when_base_history_has_higher_sequence()
    {
        var baseEntries = new List<ModelContextEntry>();
        for (var turn = 0; turn < 8; turn++)
        {
            var sequence = turn * 2L + 1;
            baseEntries.Add(ModelContextEntry.Text(
                $"base:turn:{turn}:user", sequence, ModelContextRole.User, new string('p', 200)));
            baseEntries.Add(ModelContextEntry.Assistant(
                $"base:turn:{turn}:assistant", sequence + 1,
                new AssistantStep(new string('r', 200), [], AssistantFinishReason.Stop, 0)));
        }

        const string currentSource = "event:evt_00000000000000000000000000000001";
        var runEntries = new List<ModelContextEntry>
        {
            ModelContextEntry.Text(currentSource, 1, ModelContextRole.User, new string('c', 500)),
        };
        for (var turn = 0; turn < 6; turn++)
        {
            runEntries.Add(ModelContextEntry.Assistant(
                $"event:evt_{turn + 2:00000000000000000000000000000000}",
                turn + 2,
                new AssistantStep(new string('r', 300), [], AssistantFinishReason.Stop, 0)));
        }
        var snapshot = await new AgentContextAssembler(new FixedMergedSource(
                baseEntries.ToArray(),
                runEntries.ToArray()))
            .AssembleAsync(RunId, CancellationToken.None);
        var before = ModelContextSizeEstimator.EstimateTokens(snapshot);
        var decision = new ContextBudgetDecision(
            before, 100, 100, 20, 1, true, ContextCompactionStrategy.Normal);

        var plan = ContextCompactionPlanner.Plan(
            snapshot, decision, "compact-current-run-prompt", ContextCompactionReason.Automatic);

        Assert.NotNull(plan);
        Assert.DoesNotContain(plan!.CoveredEntries, entry => entry.SourceId == currentSource);
        Assert.Contains(plan.RemainingEntries, entry => entry.SourceId == currentSource);
        Assert.True(plan.AfterTokens < plan.BeforeTokens);
    }

    private static async Task<ModelContextSnapshot> AssembleAsync(params ModelContextEntry[] entries)
    {
        var source = new FixedSource(entries);
        return await new AgentContextAssembler(source).AssembleAsync(RunId, CancellationToken.None);
    }

    private sealed class FixedSource(ModelContextEntry[] entries) : IModelContextSource
    {
        public Task<ModelContextSourceSnapshot> LoadAsync(
            RunId runId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ModelContextSourceSnapshot(Array.Empty<ModelContextEntry>(), entries));
    }

    private sealed class FixedMergedSource(
        ModelContextEntry[] sessionBase,
        ModelContextEntry[] runLocal) : IModelContextSource
    {
        public Task<ModelContextSourceSnapshot> LoadAsync(
            RunId runId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ModelContextSourceSnapshot(sessionBase, runLocal));
    }
}
