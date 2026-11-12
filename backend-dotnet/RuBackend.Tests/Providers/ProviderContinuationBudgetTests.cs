using RuAgent.Harness.Core;
using RuAgent.Runtime.Adapters.Models;
using RuBackend.Providers;
using Xunit;

namespace RuBackend.Tests.Providers;

/// <summary>
/// The reasoning a model replays with each retained assistant turn is part of the history, so
/// it answers to the history's budget. It used to answer to one sixteen times smaller: a
/// ten-step run on a reasoning model at high effort carried 532,315 bytes of it, crossed
/// 512 KiB, and failed with a code that blamed the prompt.
/// </summary>
public sealed class ProviderContinuationBudgetTests
{
    [Fact]
    public void The_continuation_budget_is_the_message_history_budget()
    {
        Assert.Equal(ModelInputLimits.MaxHistoryBytes, SqliteProviderContinuationReader.MaximumHistoryBytes);
    }

    [Fact]
    public void The_run_that_failed_in_practice_now_fits()
    {
        // Ten steps of reasoning (sizes illustrative; the largest 81,040 and the 532,315 total are the real run): over the
        // old 512 KiB (524,288), nowhere near the history budget.
        int[] stepBytes = [81_040, 50_000, 60_000, 45_000, 52_000, 48_000, 44_000, 51_000, 49_275, 52_000];
        Assert.Equal(532_315, stepBytes.Sum());
        Assert.True(stepBytes.Sum() > 512 * 1024);

        var total = 0;
        foreach (var bytes in stepBytes) total = SqliteProviderContinuationReader.AddWithinBudget(total, bytes);

        Assert.Equal(532_315, total);
    }

    [Fact]
    public void A_total_exactly_at_the_budget_is_accepted_and_one_byte_more_is_not()
    {
        var limit = SqliteProviderContinuationReader.MaximumHistoryBytes;

        Assert.Equal(limit, SqliteProviderContinuationReader.AddWithinBudget(limit - 10, 10));
        var error = Assert.Throws<ProviderCatalogException>(
            () => SqliteProviderContinuationReader.AddWithinBudget(limit - 10, 11));
        Assert.Equal("model_history_too_large", error.Code);
    }

    [Fact]
    public void A_catalog_failure_carries_its_own_code_into_the_run()
    {
        // Core classifies a step failure by this interface; without it the failure reached the
        // run as prompt_contract_invalid.
        var error = new ProviderCatalogException("model_history_too_large");

        var stable = Assert.IsAssignableFrom<IStableStepError>(error);
        Assert.Equal("model_history_too_large", stable.StepErrorCode);
    }
}
