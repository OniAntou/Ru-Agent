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
}
