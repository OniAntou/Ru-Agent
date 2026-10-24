using System.Net;
using System.Text;
using RuAgent.Broker.Tools.Web;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class WebSearchRelevanceTests
{
    [Fact]
    public async Task Unrelated_fast_engine_does_not_win_against_relevant_results()
    {
        using var http = new HttpClient(new Handler());
        var policy = new WebPolicy((_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));
        var adapter = new WebSearchAdapter(http, policy, new WebContentSanitizer(),
            new Uri("https://www.bing.com/search"),
            fallbackEndpoints: [new Uri("https://html.duckduckgo.com/html/")]);
        var result = Assert.Single(await adapter.SearchAsync("giá vàng SJC hôm nay", default));
        Assert.Equal("https://example.com/gia-vang", result.Url.AbsoluteUri);
    }

    private sealed class Handler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = "<li class='b_algo'><h2><a href='https://duolingo.com/'>Learn languages</a></h2><p>Fun learning app</p></li>";
            if (request.RequestUri!.Host == "html.duckduckgo.com")
            {
                await Task.Delay(20, ct);
                body = "<a class='result__a' href='https://example.com/gia-vang'>Giá vàng SJC cập nhật</a>";
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };
        }
    }
}
