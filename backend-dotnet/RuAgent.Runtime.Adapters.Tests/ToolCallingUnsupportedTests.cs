using System.Net;
using System.Text.Json.Nodes;
using RuAgent.Runtime.Adapters.Models;
using Xunit;

namespace RuAgent.Runtime.Adapters.Tests;

/// <summary>A server that cannot take tools fails with a named code, never a tool-free retry (ADR 0034).</summary>
public sealed class ToolCallingUnsupportedTests
{
    [Theory]
    [InlineData("""{"error":{"message":"registry.ollama.ai/library/gemma:2b does not support tools","type":"api_error"}}""")]
    [InlineData("""{"error":"llama3 does not support tools"}""")]
    [InlineData("""{"error":{"message":"\"auto\" tool choice requires --enable-auto-tool-choice and --tool-call-parser to be set"}}""")]
    public async Task A_tool_refusal_from_a_self_hosted_server_is_named(string body)
    {
        var error = await Assert.ThrowsAsync<ModelProviderException>(() => GenerateAsync(body, nativeToolCalling: true));

        Assert.Equal("model_tools_unsupported", error.Code);
        Assert.False(error.Retryable);
        Assert.DoesNotContain("ollama", error.Message);
    }

    [Theory]
    [InlineData("""{"error":{"message":"invalid JSON in tool arguments"}}""", true)]
    [InlineData("""{"error":{"message":"llama3 does not support tools"}}""", false)]
    public async Task Other_errors_and_tool_free_requests_keep_the_generic_code(string body, bool nativeToolCalling)
    {
        var error = await Assert.ThrowsAsync<ModelProviderException>(() => GenerateAsync(body, nativeToolCalling));

        Assert.Equal("http_error", error.Code);
    }

    private static async Task GenerateAsync(string body, bool nativeToolCalling)
    {
        using var http = new HttpClient(new StaticHandler(body));
        var provider = new OpenAiCompatibleModelProvider(http);
        var request = new ModelRequest("test-model", "system", "hello", [], new JsonObject(),
            new Uri("http://localhost:11434/v1/chat/completions"), "", 8192, nativeToolCalling: nativeToolCalling);
        await foreach (var _ in provider.GenerateAsync(request, default)) { }
    }

    private sealed class StaticHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(body) });
    }
}
