using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class RetainedContextMeterPresenterTests
{
    private const string RunId = "run_12345678901234567890123456789012";

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
    public void New_run_clears_old_reported_reading_and_unknown_protocol_is_not_guessed()
    {
        var presenter = new RetainedContextMeterPresenter();
        presenter.ScopeToRun(Run(), "session-1");
        Assert.True(presenter.ObserveUsage(RunId, "session-1", 1, "openai_responses", Usage(900)));
        Assert.Null(RetainedContextMeterPresenter.ResolvePromptTokens("vendor_unknown", Usage(400)));

        presenter.ScopeToRun(Run(RunId + "2"), "session-1");

        Assert.Equal(0, presenter.Measure([("new run", 0, 0)], null, 0).ReportedPromptTokens);
        Assert.Equal("old transcript".Length + "new run".Length,
            presenter.Measure([("old transcript", 0, 0), ("new run", 0, 0)], null, 0).Characters);
        Assert.True(presenter.ObserveUsage(RunId + "2", "session-1", 1, "openai_responses", Usage(350)));
        Assert.Equal(350, presenter.Measure([("old transcript", 0, 0), ("new run", 0, 0)], null, 0).UsedTokens);
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

    private static AgentRunModel Run(string? runId = null) => new(
        runId ?? RunId, "session-1", "workspace-1", "running", null, "standard", 1, "sha", 1,
        0, "2026-10-05", "2026-10-05", null, null, null, "identity",
        ProviderBackend: "openai", ProviderModel: "model", ProviderProtocol: "openai_responses",
        ContextWindowTokens: 100_000);

    private static AgentModelUsage Usage(int inputTokens) =>
        new(inputTokens, 300, 9_000, 500, 200, "model");
}
