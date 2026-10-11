using System.Net;
using System.Text;
using RuAgent.Broker.Configuration;
using RuAgent.Broker.Tools.Web;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class WebContentQualityTests
{
    [Theory]
    [InlineData("https://html.duckduckgo.com/html/")]
    [InlineData("https://www.mojeek.com/search")]
    [InlineData("https://www.bing.com/search")]
    public async Task Engine_navigation_is_not_a_search_result(string endpoint)
    {
        using var http = Client("<form id='challenge-form'>Verify you are human</form><a href='https://example.com/'>here</a>");
        var adapter = Search(http, endpoint);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => adapter.SearchAsync("coding documentation", CancellationToken.None));
        Assert.Equal("web_search_parse_failed", error.Message);
    }

    [Fact]
    public async Task Bing_uses_heading_and_decodes_target_instead_of_breadcrumb()
    {
        var target = "https://docs.python.org/3/library/sqlite3.html";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(target)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        using var http = Client($"<li class='b_algo'><a href='https://www.python.org/'>python.org</a><h2><a href='https://www.bing.com/ck/a?u=a1{encoded}&amp;ntb=1'>sqlite3 transactions</a></h2><div><p>Rollback a transaction.</p></div></li>");
        var result = Assert.Single(await Search(http, "https://www.bing.com/search").SearchAsync("sqlite rollback", CancellationToken.None));
        Assert.Equal(target, result.Url.AbsoluteUri);
        Assert.Equal("sqlite3 transactions", result.Title);
        Assert.Equal("Rollback a transaction.", result.Snippet);
    }

    [Fact]
    public async Task Json_primitive_is_a_bounded_parse_failure()
    {
        using var http = Client("42", "application/json");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Search(http, "https://example.com/search").SearchAsync("docs", CancellationToken.None));
        Assert.Equal("web_search_parse_failed", error.Message);
    }

    [Fact]
    public async Task Plain_text_preserves_code_and_generic_type_syntax()
    {
        const string source = "Task<T> Run()\n{\n    return value;\n}";
        using var http = Client(source, "text/plain");
        var result = await new WebFetchAdapter(http, Policy(), new WebContentSanitizer()).FetchAsync(new Uri("https://example.com/code.cs"), CancellationToken.None);
        Assert.Contains(source, result.SanitizedText);
    }

    [Fact]
    public async Task Html_keeps_main_content_and_preformatted_code()
    {
        using var http = Client("<title>Async guide</title><nav>MENU NOISE</nav><main><h1>Async</h1><p>Await a task.</p><pre><code>Task&lt;T&gt; Run()\n{\n    return value;\n}</code></pre><script>BAD SCRIPT</script></main><footer>FOOTER NOISE</footer>");
        var result = await new WebFetchAdapter(http, Policy(), new WebContentSanitizer()).FetchAsync(new Uri("https://example.com/docs"), CancellationToken.None);
        Assert.Equal("Async guide", result.Source.Title);
        Assert.Contains("Task<T> Run()\n{\n    return value;\n}", result.SanitizedText);
        Assert.DoesNotContain("MENU NOISE", result.SanitizedText);
        Assert.DoesNotContain("FOOTER NOISE", result.SanitizedText);
        Assert.DoesNotContain("BAD SCRIPT", result.SanitizedText);
    }

    [Fact]
    public async Task Default_download_accepts_large_document_but_output_remains_bounded()
    {
        using var http = Client("<main><p>DOCUMENT BODY</p></main><script>" + new string('x', 300_000) + "</script>");
        var result = await new WebFetchAdapter(http, Policy(), new WebContentSanitizer()).FetchAsync(new Uri("https://example.com/docs"), CancellationToken.None);
        Assert.Contains("DOCUMENT BODY", result.SanitizedText);
        Assert.True(result.SanitizedText.Length < 33_000);
        Assert.True(WebOptions.DefaultMaxResponseBytes >= 1_048_576);
    }

    [Fact]
    public async Task Challenge_cannot_beat_a_slower_valid_engine()
    {
        using var http = new HttpClient(new RaceHandler());
        var adapter = new WebSearchAdapter(http, Policy(), new WebContentSanitizer(),
            new Uri("https://html.duckduckgo.com/html/"),
            fallbackEndpoints: [new Uri("https://www.bing.com/search")]);
        var result = Assert.Single(await adapter.SearchAsync("coding docs", CancellationToken.None));
        Assert.Equal("https://example.com/docs", result.Url.AbsoluteUri);
    }

    [Fact]
    public async Task Search_keeps_case_sensitive_paths_distinct()
    {
        using var http = Client("<a class='result__a' href='https://example.com/Task'>Task</a><a class='result__a' href='https://example.com/task'>task</a>");
        var results = await Search(http, "https://html.duckduckgo.com/html/").SearchAsync("task", CancellationToken.None);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task Google_reads_direct_result_headings()
    {
        using var http = Client("<a href='https://example.com/settings'>Settings</a><a href='https://example.com/docs'><h3>Docs</h3></a>");
        var result = Assert.Single(await Search(http, "https://www.google.com/search").SearchAsync("docs", CancellationToken.None));
        Assert.Equal("Docs", result.Title);
        Assert.Equal("https://example.com/docs", result.Url.AbsoluteUri);
    }

    [Fact]
    public async Task Fetch_honors_charset_and_preserves_leading_indentation()
    {
        using var http = new HttpClient(new EncodingHandler());
        var result = await new WebFetchAdapter(http, Policy(), new WebContentSanitizer()).FetchAsync(new Uri("https://example.com/text"), CancellationToken.None);
        Assert.Contains("content:     café\n    next", result.SanitizedText);
    }

    [Fact]
    public async Task Fetch_preserves_relative_links_and_article_headers()
    {
        using var http = Client("<article><header><h1>Guide</h1></header><p>See <a href='../reference'>the reference</a>.</p></article>");
        var result = await new WebFetchAdapter(http, Policy(), new WebContentSanitizer()).FetchAsync(new Uri("https://example.com/docs/page"), CancellationToken.None);
        Assert.Contains("Guide", result.SanitizedText);
        Assert.Contains("[the reference](https://example.com/reference)", result.SanitizedText);
    }

    [Theory]
    [InlineData("<form id='challenge-form'>Verify</form>", "web_page_blocked")]
    [InlineData("<html><script>loadApplication()</script></html>", "web_content_empty")]
    public async Task Fetch_unreadable_pages_have_named_failures(string html, string code)
    {
        using var http = Client(html);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new WebFetchAdapter(http, Policy(), new WebContentSanitizer()).FetchAsync(new Uri("https://example.com/"), CancellationToken.None));
        Assert.Equal(code, error.Message);
    }

    [Fact]
    public async Task Fetch_truncates_multibyte_text_with_an_explicit_marker()
    {
        using var http = Client(new string('\u754c', 100_000), "text/plain");
        var result = await new WebFetchAdapter(http, Policy(), new WebContentSanitizer()).FetchAsync(new Uri("https://example.com/text"), CancellationToken.None);
        Assert.Contains("...[truncated]", result.SanitizedText);
        Assert.True(Encoding.UTF8.GetByteCount(result.SanitizedText) < 256 * 1024);
    }

    [Fact]
    public async Task Fetch_pages_cover_the_whole_document_without_gaps()
    {
        const string source = "first line\n    second <T> line\nlast line";
        using var http = Client(source, "text/plain");
        var adapter = new WebFetchAdapter(http, Policy(), new WebContentSanitizer());
        var reconstructed = new StringBuilder();
        var offset = 0;
        do
        {
            var result = await adapter.FetchAsync(new Uri("https://example.com/code"), CancellationToken.None, offset, 7);
            var body = result.SanitizedText.Split("content: ", 2)[1].Split("\n[/untrusted_web_reference]", 2)[0];
            if (result.NextOffset is not null) body = body[..^"...[truncated]".Length];
            reconstructed.Append(body);
            if (result.NextOffset is null) break;
            Assert.True(result.NextOffset > offset);
            offset = result.NextOffset.Value;
        } while (offset <= source.Length);
        Assert.Equal(source, reconstructed.ToString());
    }

    [Fact]
    public async Task Fetch_rejects_offset_past_document_end()
    {
        using var http = Client("short", "text/plain");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new WebFetchAdapter(http, Policy(), new WebContentSanitizer()).FetchAsync(new Uri("https://example.com/"), CancellationToken.None, 100));
        Assert.Equal("web_offset_out_of_range", error.Message);
    }

    private sealed class EncodingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("    café\n    next", Encoding.Latin1, "text/plain") });
    }

    private sealed class RaceHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = "<form id='challenge-form'></form><a href='https://duckduckgo.com/'>here</a>";
            if (request.RequestUri!.Host == "www.bing.com")
            {
                await Task.Delay(20, cancellationToken);
                body = "<li class='b_algo'><h2><a href='https://example.com/docs'>Coding docs</a></h2></li>";
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };
        }
    }

    private static WebPolicy Policy() => new((_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));
    private static WebSearchAdapter Search(HttpClient http, string endpoint) => new(http, Policy(), new WebContentSanitizer(), new Uri(endpoint));
    private static HttpClient Client(string body, string contentType = "text/html") => new(new Handler(body, contentType));
    private sealed class Handler(string body, string contentType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, contentType) });
    }
}
