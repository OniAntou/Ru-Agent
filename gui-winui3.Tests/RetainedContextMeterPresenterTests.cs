using RuAgent.Client;
using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class RetainedContextMeterPresenterTests
{
    private const string RunId = "run_12345678901234567890123456789012";

    [Fact]
    public void Reported_usage_is_distinct_from_a_prepared_estimate()
    {
        var presenter = new RetainedContextMeterPresenter();
        presenter.ScopeToRun(Run(), "session-1");
        Assert.False(presenter.HasReportedUsage);

        Assert.True(presenter.ObserveEstimate(RunId, "session-1", new AgentUsageEstimateModel(900, 10_000, "step", 5)));
        Assert.False(presenter.HasReportedUsage);

        Assert.True(presenter.ObserveUsage(RunId, "session-1", 6, "openai_responses", Usage(900)));
        Assert.True(presenter.HasReportedUsage);
    }

    [Fact]
    public void Compaction_drops_old_visible_text_and_rejects_late_usage()
    {
        var presenter = new RetainedContextMeterPresenter();
        presenter.ScopeToRun(Run(), "session-1");
        Assert.True(presenter.ObserveUsage(RunId, "session-1", 10, "openai_responses", Usage(8_000)));

        Assert.True(presenter.ObserveCompaction(RunId, "session-1", 11, 2));
        Assert.False(presenter.ObserveUsage(RunId, "session-1", 10, "openai_responses", Usage(8_000)));
        Assert.True(presenter.ObserveUsage(RunId, "session-1", 12, "openai_responses", Usage(700)));
        Assert.False(presenter.ObserveUsage(RunId, "session-1", 11, "openai_responses", Usage(8_000)));

        var measured = presenter.Measure(
            [("old transcript", 0, 0), ("older exchange", 0, 0), ("after compaction", 0, 0)],
            "draft", 1);

        Assert.Equal("after compaction".Length + "draft".Length, measured.Characters);
        Assert.Equal(1, measured.Images);
        Assert.Equal(700, measured.ReportedPromptTokens);
        Assert.Equal(700 + ModelContextSpecs.EstimateTokens("draft".Length, 1), measured.UsedTokens);
    }

    [Fact]
    public void A_new_prompt_keeps_the_last_reading_until_its_own_request_reports()
    {
        // Regression: each prompt starts a run, and scoping to it cleared the reading,
        // so a 340k context dropped to the visible-message estimate on every prompt.
        var presenter = new RetainedContextMeterPresenter();
        presenter.ScopeToRun(Run(), "session-1");
        Assert.True(presenter.ObserveUsage(RunId, "session-1", 1, "openai_responses", Usage(900)));
        Assert.Null(RetainedContextMeterPresenter.ResolvePromptTokens("vendor_unknown", Usage(400)));

        // Between runs nothing is current; that gap keeps the reading too.
        presenter.ScopeToRun(null, "session-1");
        presenter.ScopeToRun(Run(RunId + "2"), "session-1");

        Assert.Equal(900, presenter.Measure([("old transcript", 0, 0), ("new run", 0, 0)], null, 0).UsedTokens);
        Assert.True(presenter.ObserveUsage(RunId + "2", "session-1", 1, "openai_responses", Usage(1_350)));
        Assert.Equal(1_350, presenter.Measure([("old transcript", 0, 0), ("new run", 0, 0)], null, 0).UsedTokens);

        // Another conversation never inherits it.
        presenter.ScopeToRun(null, "session-2");
        Assert.Equal(0, presenter.Measure([("other chat", 0, 0)], null, 0).ReportedPromptTokens);
    }

    [Fact]
    public void Text_attachments_add_characters_while_only_images_add_image_allowance()
    {
        var presenter = new RetainedContextMeterPresenter();
        presenter.ScopeToRun(Run(), "session-1");

        var measured = presenter.Measure([("prompt", 0, 12)], "draft", 1, 8);

        Assert.Equal("prompt".Length + 12 + "draft".Length + 8, measured.Characters);
        Assert.Equal(1, measured.Images);
    }

    [Fact]
    public void Provider_cache_fields_are_normalized_by_exact_protocol()
    {
        var usage = new AgentModelUsage(400, 100, 500, 200, 50);

        Assert.Equal(400, RetainedContextMeterPresenter.ResolvePromptTokens("openai_responses", usage));
        Assert.Equal(650, RetainedContextMeterPresenter.ResolvePromptTokens("anthropic_messages", usage));
    }

    [Fact]
    public void Missing_and_zero_input_counts_remain_distinct_and_protocol_specific()
    {
        Assert.Equal(0, RetainedContextMeterPresenter.ResolvePromptTokens(
            "chat_completions", new AgentModelUsage(0, 80, 80, 40, null)));
        Assert.Equal(60, RetainedContextMeterPresenter.ResolvePromptTokens(
            "anthropic_messages", new AgentModelUsage(null, 30, null, 40, 20)));
        Assert.Null(RetainedContextMeterPresenter.ResolvePromptTokens(
            "unknown", new AgentModelUsage(null, 30, null, 40, 20)));
    }

    [Fact]
    public void Anthropic_prompt_count_saturates_at_the_supported_integer_bound()
    {
        var usage = new AgentModelUsage(int.MaxValue, 0, null, int.MaxValue, int.MaxValue);

        Assert.Equal(int.MaxValue, RetainedContextMeterPresenter.ResolvePromptTokens("anthropic_messages", usage));
    }

    [Fact]
    public void An_estimate_for_an_already_reported_step_cannot_replace_real_usage()
    {
        var presenter = new RetainedContextMeterPresenter();
        presenter.ScopeToRun(Run(), "session-1");
        presenter.ObserveEstimate(RunId, "session-1", new AgentUsageEstimateModel(80_000, 100_000, "step-a", 5));
        presenter.ObserveUsage(RunId, "session-1", 10, "openai_responses", Usage(50_000));
        Assert.False(presenter.ObserveEstimate(RunId, "session-1", new AgentUsageEstimateModel(80_000, 100_000, "step-a", 6)));
        Assert.Equal(50_000, presenter.Measure([], null, 0).UsedTokens);
        Assert.True(presenter.HasReportedUsage);
    }

    [Fact]
    public void Compaction_does_not_allow_a_buffered_estimate_to_restore_the_previous_step()
    {
        var presenter = new RetainedContextMeterPresenter();
        presenter.ScopeToRun(Run(), "session-1");
        presenter.ObserveEstimate(RunId, "session-1", new AgentUsageEstimateModel(80_000, 100_000, "step-a", 5));
        presenter.ObserveCompaction(RunId, "session-1", 10, 0);
        Assert.False(presenter.ObserveEstimate(RunId, "session-1", new AgentUsageEstimateModel(80_000, 100_000, "step-a", 6)));
    }

    [Fact]
    public void A_large_prepared_estimate_plus_draft_saturates_without_integer_overflow()
    {
        var presenter = new RetainedContextMeterPresenter();
        presenter.ScopeToRun(Run(), "session-1");
        presenter.ObserveEstimate(RunId, "session-1", new(long.MaxValue, long.MaxValue, "a", 1));
        Assert.Equal(int.MaxValue, presenter.Measure([], "draft", 0).UsedTokens);
    }

    private static AgentRunModel Run(string? runId = null) => new(
        runId ?? RunId, "session-1", "workspace-1", "running", null, "standard", 1, "sha", 1,
        0, "2026-10-05", "2026-10-05", null, null, null, "identity",
        ProviderBackend: "openai", ProviderModel: "model", ProviderProtocol: "openai_responses",
        ContextWindowTokens: 100_000);

    private static AgentModelUsage Usage(int inputTokens) =>
        new(inputTokens, 300, 9_000, 500, 200, "model");
}
