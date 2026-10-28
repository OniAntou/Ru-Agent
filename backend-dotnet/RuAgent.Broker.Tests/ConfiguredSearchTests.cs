using System.Net;
using System.Text;
using RuAgent.Broker.Configuration;
using RuAgent.Broker.Tools.Web;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class ConfiguredSearchTests
{
    [Fact]
    public async Task JsonBackendNeverRacesHtmlFallbacksAndPreservesStructuredSnippet()
    {
        var handler = new Handler("application/json", """{"results":[{"title":"Result","url":"https://example.com/a","content":"structured snippet"}]}""");
        using var http = new HttpClient(handler);
        var adapter = Create(http);
        var result = Assert.Single(await adapter.SearchAsync("query", default));
        Assert.Equal("structured snippet", result.Snippet);
        Assert.Equal(1, handler.Requests);
        Assert.Contains("format=json", handler.Uri!.Query, StringComparison.Ordinal);
        Assert.Contains("q=query", handler.Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HtmlChallengeCannotSilentlyReplaceConfiguredJsonResults()
    {
        var handler = new Handler("text/html", "<a href='https://example.com/a'>query result</a>");
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Create(http).SearchAsync("query", default));
        Assert.Equal("web_search_json_required", error.Message);
        Assert.Equal(1, handler.Requests);
    }

    private static WebSearchAdapter Create(HttpClient http) => new(http,
        new WebPolicy((_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") })), new WebContentSanitizer(),
        new Uri("https://search.example.com/search?format=json"),
        fallbackEndpoints: WebOptions.DefaultFallbackEndpoints, searchMode: WebSearchMode.ConfiguredJson);

    private sealed class Handler(string mediaType, string body) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public Uri? Uri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            Uri = request.RequestUri;
            Assert.Equal("search.example.com", request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mediaType) });
        }
    }
}
