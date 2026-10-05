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

    [Fact]
    public async Task Planner_compacts_completed_multi_call_groups_as_a_unit()
    {
        using var firstArguments = JsonDocument.Parse("{}");
        using var secondArguments = JsonDocument.Parse("{}");
        var calls = new[]
        {
            new ModelToolCall("provider-call-a", "files.read", "1.0.0", firstArguments.RootElement),
            new ModelToolCall("provider-call-b", "files.read", "1.0.0", secondArguments.RootElement),
        };
        var entries = new[]
        {
            ModelContextEntry.Text("old", 1, ModelContextRole.User, new string('h', 2_000)),
            ModelContextEntry.Assistant("assistant-batch", 2,
                new AssistantStep("checking two files", calls, AssistantFinishReason.ToolCalls, 20)),
            ModelContextEntry.ToolResult("receipt-a", 3, Receipt("a", "provider-call-a"), new string('a', 1_000)),
            ModelContextEntry.ToolResult("receipt-b", 4, Receipt("b", "provider-call-b"), new string('b', 1_000)),
            ModelContextEntry.Text("current", 5, ModelContextRole.User, "keep current prompt"),
        };
        var snapshot = await AssembleAsync(entries);
        var before = ModelContextSizeEstimator.EstimateTokens(snapshot);
        var decision = new ContextBudgetDecision(before, 1, 1, 1, 1, true, ContextCompactionStrategy.Normal);

        var plan = ContextCompactionPlanner.Plan(snapshot, decision,
            "compact-completed-batch", ContextCompactionReason.Automatic);

        Assert.NotNull(plan);
        var groupIds = new HashSet<string>(StringComparer.Ordinal) { "assistant-batch", "receipt-a", "receipt-b" };
        Assert.Equal(groupIds, plan!.CoveredEntries.Where(entry => groupIds.Contains(entry.SourceId))
            .Select(entry => entry.SourceId).ToHashSet(StringComparer.Ordinal));
        Assert.Equal(groupIds, plan.CoveredEntries.Where(entry => groupIds.Contains(entry.SourceId))
            .Select(entry => entry.SourceId).ToHashSet(StringComparer.Ordinal));
        Assert.DoesNotContain(plan.CoveredEntries, entry => entry.SourceId == "current");
        Assert.Contains(plan.RemainingEntries, entry => entry.SourceId == "current");
        Assert.Contains(plan.CoveredRanges, range => range.SourceId == "assistant-batch" && range.StartSequence == 2 && range.EndSequence == 2);
        Assert.Contains(plan.CoveredRanges, range => range.SourceId == "receipt-a" && range.StartSequence == 3 && range.EndSequence == 3);
        Assert.Contains(plan.CoveredRanges, range => range.SourceId == "receipt-b" && range.StartSequence == 4 && range.EndSequence == 4);
    }

    [Fact]
    public async Task Planner_does_not_reuse_later_receipt_for_an_unresolved_repeated_call_id()
    {
        using var arguments = JsonDocument.Parse("{}");
        var repeatedCall = new ModelToolCall("reused-provider-id", "files.read", "1.0.0", arguments.RootElement);
        var entries = new[]
        {
            ModelContextEntry.Text("old", 1, ModelContextRole.User, new string('h', 2_000)),
            ModelContextEntry.Assistant("first-batch", 2,
                new AssistantStep("first", [repeatedCall], AssistantFinishReason.ToolCalls, 20)),
            ModelContextEntry.Assistant("second-batch", 3,
                new AssistantStep("second", [repeatedCall], AssistantFinishReason.ToolCalls, 20)),
            ModelContextEntry.ToolResult("second-receipt", 4, Receipt("c", "reused-provider-id"), "second result"),
            ModelContextEntry.Text("current", 5, ModelContextRole.User, "keep current prompt"),
        };
        var snapshot = await AssembleAsync(entries);
        var before = ModelContextSizeEstimator.EstimateTokens(snapshot);
        var decision = new ContextBudgetDecision(before, 1, 1, 1, 1, true, ContextCompactionStrategy.Normal);

        var plan = ContextCompactionPlanner.Plan(snapshot, decision,
            "compact-repeated-call-id", ContextCompactionReason.Automatic);

        Assert.NotNull(plan);
        Assert.DoesNotContain(plan!.CoveredEntries, entry => entry.SourceId == "first-batch");
        Assert.DoesNotContain(plan.CoveredEntries, entry => entry.SourceId == "second-batch");
        Assert.DoesNotContain(plan.CoveredEntries, entry => entry.SourceId == "second-receipt");
        Assert.Contains(plan.RemainingEntries, entry => entry.SourceId == "first-batch");
        Assert.Contains(plan.RemainingEntries, entry => entry.SourceId == "second-batch");
        Assert.Contains(plan.RemainingEntries, entry => entry.SourceId == "second-receipt");
    }

    [Fact]
    public async Task Planner_compacts_separate_completed_batches_with_repeated_call_id()
    {
        using var arguments = JsonDocument.Parse("{}");
        var repeatedCall = new ModelToolCall("reused-provider-id", "files.read", "1.0.0", arguments.RootElement);
        var entries = new[]
        {
            ModelContextEntry.Text("old", 1, ModelContextRole.User, new string('h', 2_000)),
            ModelContextEntry.Assistant("first-batch", 2,
                new AssistantStep("first", [repeatedCall], AssistantFinishReason.ToolCalls, 20)),
            ModelContextEntry.ToolResult("first-receipt", 3, Receipt("d", "reused-provider-id"), "first result"),
            ModelContextEntry.Assistant("second-batch", 4,
                new AssistantStep("second", [repeatedCall], AssistantFinishReason.ToolCalls, 20)),
            ModelContextEntry.ToolResult("second-receipt", 5, Receipt("e", "reused-provider-id"), "second result"),
            ModelContextEntry.Assistant("final", 6,
                new AssistantStep("done", [], AssistantFinishReason.Stop, 0)),
            ModelContextEntry.Text("current", 7, ModelContextRole.User, "keep current prompt"),
        };
        var snapshot = await AssembleAsync(entries);
        var before = ModelContextSizeEstimator.EstimateTokens(snapshot);
        var decision = new ContextBudgetDecision(before, 1, 1, 1, 1, true, ContextCompactionStrategy.Normal);

        var plan = ContextCompactionPlanner.Plan(snapshot, decision,
            "compact-valid-repeated-call-id", ContextCompactionReason.Automatic);

        Assert.NotNull(plan);
        var expected = new HashSet<string>(StringComparer.Ordinal)
            { "first-batch", "first-receipt", "second-batch", "second-receipt" };
        Assert.Equal(expected, plan!.CoveredEntries.Where(entry => expected.Contains(entry.SourceId))
            .Select(entry => entry.SourceId).ToHashSet(StringComparer.Ordinal));
        Assert.Contains(plan.RemainingEntries, entry => entry.SourceId == "current");
    }

    private static async Task<ModelContextSnapshot> AssembleAsync(params ModelContextEntry[] entries)
    {
        var source = new FixedSource(entries);
        return await new AgentContextAssembler(source).AssembleAsync(RunId, CancellationToken.None);
    }

    private static ToolExecutionReceipt Receipt(string suffix, string providerCallId) =>
        new(
            new ExecutionId($"exec_{suffix}{new string('0', 31)}"),
            new ToolCallId($"call_{suffix}{new string('0', 31)}"),
            ToolExecutionOutcome.Succeeded,
            "ok",
            null,
            false,
            providerCallId: providerCallId);

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
