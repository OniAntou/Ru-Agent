using RuAgent.Client;
using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class ChatModelUsageTests
{
    [Theory]
    [InlineData(false, "provider", "model", false)]
    [InlineData(true, "provider", "", false)]
    [InlineData(true, null, "model", true)]
    public async Task Missing_display_or_selection_skips_catalog_and_clears_stale_rates(
        bool hasHeader, string? backend, string model, bool retained)
    {
        var reads = 0;
        var resolver = new ChatModelUsage((_, _, _) =>
        {
            reads++;
            return Task.FromResult<CatalogModelDetails?>(null);
        });
        var usage = new TuiUsageTracker { ContextLimit = 1000, InputRate = 1, OutputRate = 2 };

        await resolver.RefreshAsync(usage, new(backend, model, retained), hasHeader, CancellationToken.None);

        Assert.Equal(0, reads);
        Assert.Null(usage.ContextLimit);
        Assert.Null(usage.InputRate);
        Assert.Null(usage.OutputRate);
    }

    [Theory]
    [InlineData(false, null, 2000L)]
    [InlineData(true, "chat_completions", 1000L)]
    [InlineData(true, "openai_responses", 1000L)]
    [InlineData(true, "unknown", null)]
    public async Task Retained_context_uses_snapshot_while_rates_use_the_same_model(
        bool retained, string? protocol, long? expectedContext)
    {
        var resolver = new ChatModelUsage((backend, model, _) =>
        {
            Assert.Equal("provider", backend);
            Assert.Equal("model", model);
            return Task.FromResult<CatalogModelDetails?>(new(2000, 1, 2, null, null));
        });
        var usage = new TuiUsageTracker();

        await resolver.RefreshAsync(usage, new("provider", "model", retained, 1000, protocol),
            true, CancellationToken.None);

        Assert.Equal(expectedContext, usage.ContextLimit);
        Assert.Equal(1d, usage.InputRate);
        Assert.Equal(2d, usage.OutputRate);
    }

    [Fact]
    public async Task Cancellation_reaches_the_catalog_and_is_not_swallowed()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var resolver = new ChatModelUsage((_, _, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            return Task.FromCanceled<CatalogModelDetails?>(token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.RefreshAsync(
            new TuiUsageTracker(), new("provider", "model"), true, cancellation.Token));
    }

    [Fact]
    public async Task Catalog_failure_preserves_retained_context_without_inventing_rates()
    {
        var resolver = new ChatModelUsage((_, _, _) =>
            Task.FromException<CatalogModelDetails?>(new HttpRequestException("catalog unavailable")));
        var usage = new TuiUsageTracker();

        await resolver.RefreshAsync(usage, new("provider", "model", true, 1000, "chat_completions"),
            true, CancellationToken.None);

        Assert.Equal(1000L, usage.ContextLimit);
        Assert.Null(usage.InputRate);
        Assert.Null(usage.OutputRate);
    }
}
