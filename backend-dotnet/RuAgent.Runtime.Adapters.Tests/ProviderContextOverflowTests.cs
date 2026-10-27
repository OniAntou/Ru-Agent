using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Runtime.Adapters.Models;
using Xunit;

namespace RuAgent.Runtime.Adapters.Tests;

public sealed partial class ProviderQuotaTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Anthropic_context_rejection_uses_the_shared_code(bool streamed)
    {
        var body = """{"type":"error","error":{"type":"invalid_request_error","message":"prompt is too long: synthetic details"}}""";
        using var http = new HttpClient(new Handler(() => new HttpResponseMessage(streamed ? HttpStatusCode.OK : HttpStatusCode.BadRequest)
        {
            Content = streamed ? new StringContent($"event: error\ndata: {body}\n\n", Encoding.UTF8, "text/event-stream") : new StringContent(body),
        }));
        var provider = new AnthropicMessagesModelProvider(http);
        var request = new ModelRequest("test-model", "system", "hello", [], new JsonObject(),
            new Uri("https://provider.invalid/v1/messages"), "synthetic", 8192);
        var error = await Assert.ThrowsAsync<ModelProviderException>(async () =>
        {
            await foreach (var _ in provider.GenerateAsync(request, default)) { }
        });
        Assert.Equal("context_window_exceeded", error.Code);
        Assert.False(error.Retryable);
        Assert.DoesNotContain("synthetic details", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Http_context_overflow_preserves_a_specific_nonretryable_code(bool responses)
    {
        var content = new StringContent("""{"error":{"code":"context_length_exceeded","message":"synthetic private details"}}""");
        var error = await Assert.ThrowsAsync<ModelProviderException>(() => Generate(responses, content, status: HttpStatusCode.BadRequest));
        Assert.Equal("context_window_exceeded", error.Code);
        Assert.False(error.Retryable); // Orchestration must shrink, never retry the same request blindly.
        Assert.DoesNotContain("private details", error.Message);
    }

    [Theory]
    [InlineData(false, "error")]
    [InlineData(true, "error")]
    [InlineData(true, "response.failed")]
    public async Task Streamed_context_overflow_has_the_same_code(bool responses, string eventType)
    {
        var errorBody = new JsonObject { ["code"] = "context_length_exceeded" };
        var body = eventType == "response.failed"
            ? new JsonObject { ["type"] = eventType, ["response"] = new JsonObject { ["error"] = errorBody } }
            : new JsonObject { ["type"] = eventType, ["error"] = errorBody };
        var content = new StringContent($"event: {eventType}\ndata: {body.ToJsonString()}\n\n", Encoding.UTF8, "text/event-stream");
        var error = await Assert.ThrowsAsync<ModelProviderException>(() => Generate(responses, content, status: HttpStatusCode.OK));
        Assert.Equal("context_window_exceeded", error.Code);
        Assert.False(error.Retryable);
    }

    [Theory]
    [InlineData("{\"error\":{\"message\":\"prompt is too long: 12000 tokens\"}}", "context_window_exceeded")]
    [InlineData("{\"error\":{\"message\":\"invalid JSON in tool arguments\"}}", "http_error")]
    [InlineData("not JSON: context_length_exceeded", "http_error")]
    public async Task Only_recognized_bounded_error_shapes_trigger_context_recovery(string body, string expected)
    {
        var error = await Assert.ThrowsAsync<ModelProviderException>(() => Generate(false, new StringContent(body), status: HttpStatusCode.BadRequest));
        Assert.Equal(expected, error.Code);
    }
}
