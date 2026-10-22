using System.Text;
using System.Text.Json;
using RuAgent.Harness.Core;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed class ContextTokenBreakdownTests
{
    [Theory]
    [InlineData("{\"system_prompt\":-1,\"tool_definitions\":2,\"messages\":3,\"estimator\":\"request-bytes-v1\"}")]
    [InlineData("{\"system_prompt\":2147483647,\"tool_definitions\":2,\"messages\":3,\"estimator\":\"request-bytes-v1\"}")]
    [InlineData("{\"system_prompt\":1,\"system_prompt\":2,\"tool_definitions\":2,\"messages\":3,\"estimator\":\"request-bytes-v1\"}")]
    [InlineData("{\"system_prompt\":1,\"tool_definitions\":2,\"messages\":3,\"estimator\":\"unknown\"}")]
    public void Invalid_optional_counts_leave_the_provider_report_intact(string breakdown)
    {
        using var input = JsonDocument.Parse("{\"input_tokens\":123,\"context_tokens\":" + breakdown + "}");
        var usage = ModelUsage.Read(input.RootElement)!;
        Assert.Equal(123, usage.InputTokens);
        Assert.Null(usage.ContextTokens);
    }

    [Fact]
    public void Request_breakdown_survives_durable_usage_without_changing_provider_counts()
    {
        using var input = JsonDocument.Parse("""{"input_tokens":123,"context_tokens":{"system_prompt":12,"tool_definitions":34,"messages":56,"estimator":"request-bytes-v1"}}""");
        var usage = ModelUsage.Read(input.RootElement)!;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) usage.WriteTo(writer);
        using var output = JsonDocument.Parse(stream.ToArray());
        Assert.Equal(123, output.RootElement.GetProperty("input_tokens").GetInt32());
        Assert.True(output.RootElement.TryGetProperty("context_tokens", out var breakdown));
        Assert.Equal(12, breakdown.GetProperty("system_prompt").GetInt32());
        Assert.Equal(34, breakdown.GetProperty("tool_definitions").GetInt32());
        Assert.Equal(56, breakdown.GetProperty("messages").GetInt32());
    }
}
