extern alias Broker;

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using RuBackend.Harness.Broker;
using Xunit;
using WebOptions = Broker::RuAgent.Broker.Configuration.WebOptions;

namespace RuBackend.Tests.Harness;

public sealed class AgentWebToolExecutorTests
{
    [Fact]
    public async Task Search_returns_bounded_citations_and_does_not_call_more_than_once()
    {
        var handler = new RecordingHandler(_ => HtmlResponse(string.Join(
            string.Empty,
            Enumerable.Range(1, 12).Select(index =>
                $"<a href=\"https://8.8.4.4/result-{index}\">Result {index}</a>"))));
        using var http = new HttpClient(handler);
        var executor = CreateExecutor(http);

        var output = await executor.ExecuteAsync(
            "web.search",
            new JsonObject { ["query"] = "ru" },
            CancellationToken.None);

        Assert.True(output["untrusted"]!.GetValue<bool>());
        Assert.True(output["citations"]!.AsArray().Count <= 10);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Fetch_returns_untrusted_sanitized_content()
    {
        var handler = new RecordingHandler(_ => HtmlResponse(
            "<html><title>Example</title><script>ignore-this-script</script><body>safe text</body></html>"));
        using var http = new HttpClient(handler);
        var executor = CreateExecutor(http);

        var output = await executor.ExecuteAsync(
            "web.fetch",
            new JsonObject { ["url"] = "https://8.8.8.8/page" },
            CancellationToken.None);

        var content = output["content"]!.GetValue<string>();
        Assert.True(output["untrusted"]!.GetValue<bool>());
        Assert.Contains("[untrusted_web_reference]", content, StringComparison.Ordinal);
        Assert.Contains("safe text", content, StringComparison.Ordinal);
        Assert.DoesNotContain("ignore-this-script", content, StringComparison.Ordinal);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Invalid_tool_and_arguments_are_rejected_before_http()
    {
        var handler = new RecordingHandler(_ => HtmlResponse("<p>unused</p>"));
        using var http = new HttpClient(handler);
        var executor = CreateExecutor(http);

        await Assert.ThrowsAsync<ArgumentException>(() => executor.ExecuteAsync(
            "web.unknown",
            new JsonObject(),
            CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => executor.ExecuteAsync(
            "web.search",
            new JsonObject(),
            CancellationToken.None));

        Assert.Equal(0, handler.RequestCount);
    }

    private static AgentWebToolExecutor CreateExecutor(HttpClient http) =>
        new(
            new WebOptions(
                enabled: true,
                searchEndpoint: new Uri("https://8.8.8.8/html/")),
            http);

    private static HttpResponseMessage HtmlResponse(string html) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html"),
        };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(responder(request));
        }
    }
}
