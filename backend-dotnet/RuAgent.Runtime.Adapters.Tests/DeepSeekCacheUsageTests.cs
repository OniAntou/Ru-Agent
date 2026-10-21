using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Runtime.Adapters.Models;
using Xunit;

namespace RuAgent.Runtime.Adapters.Tests;

public sealed class DeepSeekCacheUsageTests
{
    [Theory]
    [InlineData("\"prompt_tokens\":1000,\"prompt_cache_hit_tokens\":1001")]
    [InlineData("\"prompt_tokens\":1000,\"prompt_cache_hit_tokens\":998,\"prompt_cache_miss_tokens\":3")]
    [InlineData("\"prompt_cache_hit_tokens\":2147483647,\"prompt_cache_miss_tokens\":1")]
    [InlineData("\"prompt_cache_hit_tokens\":998,\"prompt_tokens_details\":{\"cached_tokens\":997}")]
    public async Task Contradictory_cache_usage_is_rejected(string fields)
    {
        using var http = new HttpClient(new Handler(fields));
        var request = new ModelRequest("fixture", "system", "task", [], new JsonObject(),
            new Uri("https://provider.invalid/v1/chat/completions"), "synthetic");
        await Assert.ThrowsAsync<ModelProviderException>(() => new OpenAiCompatibleModelProvider(http).GenerateAsync(request, default).ToListAsync().AsTask());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Private_reasoning_is_only_serialized_for_a_declared_replay_format(bool enabled)
    {
        using var http = new HttpClient();
        var turns = new[] { new ModelTurn("assistant", "working", reasoningContent: "private full reasoning") };
        var request = new ModelRequest("fixture", "system", "task", turns, new JsonObject(),
            new Uri("https://provider.invalid/v1/chat/completions"), "synthetic", replayChatReasoning: enabled);
        var json = JsonNode.Parse(new OpenAiCompatibleModelProvider(http).ProjectRequestBody(request))!;
        var assistant = json["messages"]![1]!.AsObject();
        Assert.Equal(enabled, assistant.ContainsKey("reasoning_content"));
    }

    [Theory]
    [InlineData("\"prompt_tokens\":1000,\"prompt_cache_hit_tokens\":998,\"prompt_cache_miss_tokens\":2", 1000, 998)]
    [InlineData("\"prompt_cache_hit_tokens\":998,\"prompt_cache_miss_tokens\":2", 1000, 998)]
    [InlineData("\"prompt_tokens\":1000,\"prompt_tokens_details\":{\"cached_tokens\":998}", 1000, 998)]
    public async Task Cache_reports_preserve_total_input_and_exact_cache_reads(string fields, int input, int cached)
    {
        using var http = new HttpClient(new Handler(fields));
        var provider = new OpenAiCompatibleModelProvider(http);
        var request = new ModelRequest("fixture", "system", "task", [], new JsonObject(),
            new Uri("https://provider.invalid/v1/chat/completions"), "synthetic");
        var deltas = await provider.GenerateAsync(request, default).ToListAsync();
        var usage = Assert.Single(deltas, delta => delta.Usage is not null).Usage!;
        Assert.Equal(input, usage.InputTokens);
        Assert.Equal(cached, usage.CacheReadInputTokens);
        using var stream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream)) usage.WriteTo(writer);
        var json = JsonNode.Parse(stream.ToArray())!;
        Assert.True(json["input_tokens_include_cache"]!.GetValue<bool>());
    }

    private sealed class Handler(string fields) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: {\"choices\":[],\"usage\":{" + fields
                    + ",\"completion_tokens\":1}}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
            });
    }
}
