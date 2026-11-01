using System.Text.Json;
using RuAgent.Client;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class ChatTokenUsageTests
{
    private static readonly CatalogModelDetails Rates = new(10000, 1, 2, .5, 3);

    [Theory]
    [InlineData(true, 1000)]
    [InlineData(false, 600)]
    public void Cache_buckets_are_disjoint_for_inclusive_and_exclusive_provider_accounting(bool inclusive, int input)
    {
        var report = ChatTokenUsage.Parse(JsonSerializer.Serialize(new { input_tokens = input, output_tokens = 100,
            total_tokens = 1100, cache_read_input_tokens = 400, cache_write_input_tokens = 0, input_tokens_include_cache = inclusive }));
        Assert.Equal(600, report.UncachedInput); Assert.Equal(1000, report.Prompt("anthropic_messages"));
        Assert.Equal(1100, report.ResolvedTotal); Assert.Equal(.001m, report.Price(Rates));
    }

    [Fact]
    public void Legacy_cache_accounting_is_not_guessed_or_priced()
    {
        var report = ChatTokenUsage.Parse("""{"input_tokens":1000,"output_tokens":100,"total_tokens":1100,"cache_read_input_tokens":400,"cache_write_input_tokens":0}""");
        Assert.Null(report.UncachedInput); Assert.Null(report.Price(Rates));
        Assert.Equal(1100, report.ResolvedTotal);
    }

    [Theory]
    [InlineData("{\"input_tokens\":-1}")]
    [InlineData("{\"input_tokens\":\"100\"}")]
    [InlineData("{\"input_tokens\":2147483648}")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"input_tokens\":-1,\"input_tokens\":10}")]
    [InlineData("{\"input_tokens\":10,\"cache_read_input_tokens\":11,\"input_tokens_include_cache\":true}")]
    [InlineData("{\"input_tokens\":10,\"input_tokens_include_cache\":\"true\"}")]
    public void Invalid_reports_are_named_errors_not_zero_usage(string json)
    {
        var error = Assert.Throws<AgentApiException>(() => ChatTokenUsage.Parse(json));
        Assert.Equal("usage_report_invalid", error.ErrorCode);
    }

    [Fact]
    public void Partial_usage_and_missing_cache_rates_remain_unpriced()
    {
        var report = ChatTokenUsage.Parse("""{"input_tokens":100,"output_tokens":10,"cache_read_input_tokens":20,"input_tokens_include_cache":true}""");
        Assert.Equal(80, report.UncachedInput); Assert.Equal(0, report.WriteBucket);
        Assert.Null(report.Price(Rates with { CacheReadCost = null }));
        Assert.Null((report with { Output = null }).Price(Rates));
        Assert.Null((report with { CacheRead = null }).Price(Rates));
        Assert.Null(report.Price(Rates with { InputCost = double.NaN }));
    }

    [Fact]
    public void Explicit_zero_reports_keep_zero_without_inventing_missing_reports()
    {
        var report = ChatTokenUsage.Parse("""{"input_tokens":0,"output_tokens":0,"total_tokens":0,"cache_read_input_tokens":0,"cache_write_input_tokens":0,"input_tokens_include_cache":true}""");
        Assert.Equal(0, report.ResolvedTotal); Assert.Equal(0m, report.Price(Rates));
        var empty = ChatUsagePresentation.Usage(new("", [], 0));
        Assert.Contains("Estimated cost: unknown", empty);
        Assert.Contains("Billed tokens: unknown", empty);
    }

    [Fact]
    public void Draft_is_a_separate_estimate_and_counts_text_and_images()
    {
        Assert.Equal(4099, ChatUsagePresentation.DraftEstimate("hello", 3, 1));
        Assert.Equal(0, ChatUsagePresentation.DraftEstimate("", 0, 0));
    }
}
