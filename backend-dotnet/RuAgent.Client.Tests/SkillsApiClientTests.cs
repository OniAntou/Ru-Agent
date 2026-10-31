using System.Net;
using System.Text;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Client.Tests;

public sealed class SkillsApiClientTests
{
    private static PublicHostSession Session()
    {
        var process = System.Diagnostics.Process.GetCurrentProcess();
        return PublicHostSession.FromDescriptor(new RuntimeDescriptor(
            1, "http://127.0.0.1:8000/", new string('e', 64), process.Id,
            process.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O")));
    }

    [Fact]
    public async Task List_escapes_workspace_and_query_and_attaches_bearer()
    {
        var handler = new StubHandler("""
            {"items":[{"id":"review","name":"Review","description":"Review code","source_path":"D:\\skills\\review","resource_base":"D:\\skills\\review","revision":"abc","enabled":true,"model_invocable":true,"user_invocable":true,"requirements":["git"],"compatibility":"Ru"}],"diagnostics":[],"truncated":false,"total":1}
            """);
        using var http = new HttpClient(handler);
        var client = new SkillsApiClient(Session(), http);

        var response = await client.ListAsync("D:\\My Project", "code review", refresh: true);

        var item = Assert.Single(response.Items);
        Assert.Equal("review", item.Id);
        Assert.Equal(new[] { "git" }, item.Requirements);
        Assert.Contains("workspace_path=D%3A%5CMy%20Project", handler.Uri!.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("query=code%20review", handler.Uri.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("refresh=true", handler.Uri.Query, StringComparison.Ordinal);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal(new string('e', 64), handler.AuthorizationParameter);
    }

    [Fact]
    public async Task Update_posts_only_declared_workspace_preference_fields()
    {
        var handler = new StubHandler("""{"items":[],"diagnostics":[],"truncated":false,"total":0}""");
        using var http = new HttpClient(handler);
        var client = new SkillsApiClient(Session(), http);

        await client.UpdateAsync(new SkillSettingsRequest("D:\\LLM", "set_enabled", Id: "review", Enabled: false));

        Assert.EndsWith("agent/v1/skills/settings", handler.Uri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("D:\\LLM", handler.Body!.RootElement.GetProperty("workspace_path").GetString());
        Assert.Equal("set_enabled", handler.Body.RootElement.GetProperty("action").GetString());
        Assert.Equal("review", handler.Body.RootElement.GetProperty("id").GetString());
        Assert.False(handler.Body.RootElement.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Install_package_posts_explicit_pinned_source_and_returns_reviewable_receipt()
    {
        var handler = new StubHandler("""
            {"receipt":{"action":"installed","id":"demo","name":"Demo","description":"Package","source":"https://github.com/acme/repo/tree/0123456789abcdef0123456789abcdef01234567/skills/demo","commit":"0123456789abcdef0123456789abcdef01234567","packageSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","fileCount":2,"expandedBytes":34,"installPath":"C:\\Users\\test\\AppData\\Local\\Ru\\skills\\demo"},"catalog":{"items":[],"diagnostics":[],"truncated":false,"total":0}}
            """);
        using var http = new HttpClient(handler);
        using var client = new SkillsApiClient(Session(), http);

        var response = await client.InstallPackageAsync(new SkillPackageInstallRequest(
            "D:\\LLM", "https://github.com/acme/repo/tree/0123456789abcdef0123456789abcdef01234567/skills/demo", "install"));

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.EndsWith("agent/v1/skills/packages", handler.Uri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal(new string('e', 64), handler.AuthorizationParameter);
        Assert.Equal("install", handler.Body!.RootElement.GetProperty("action").GetString());
        Assert.Equal("https://github.com/acme/repo/tree/0123456789abcdef0123456789abcdef01234567/skills/demo",
            handler.Body.RootElement.GetProperty("source_url").GetString());
        Assert.Equal("demo", response.Receipt.Id);
        Assert.Equal(new string('a', 64), response.Receipt.PackageSha256);
    }

    [Theory]
    [InlineData("{\"items\":[null],\"diagnostics\":[],\"total\":1,\"truncated\":false}")]
    [InlineData("{\"items\":[],\"diagnostics\":[null],\"total\":0,\"truncated\":false}")]
    public async Task Malformed_catalog_items_produce_a_named_client_failure(string response)
    {
        using var http = new HttpClient(new StubHandler(response));
        using var client = new SkillsApiClient(Session(), http);
        var error = await Assert.ThrowsAsync<AgentApiException>(() => client.ListAsync("D:\\project"));
        Assert.Equal("invalid_response", error.ErrorCode);
    }

    [Fact]
    public async Task Oversized_catalog_is_refused_before_httpclient_buffers_its_body()
    {
        using var http = new HttpClient(new OversizedHandler());
        using var client = new SkillsApiClient(Session(), http);
        var error = await Assert.ThrowsAsync<AgentApiException>(() => client.ListAsync("D:\\project"));
        Assert.Equal("response_too_large", error.ErrorCode);
    }

    private sealed class OversizedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnbufferableContent() });
    }

    private sealed class UnbufferableContent : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        { length = BoundedHttpResponseReader.MaximumBytes + 1L; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("The unbounded HTTP body must not be buffered.");
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        public System.Text.Json.JsonDocument? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Method = request.Method;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            if (request.Content is not null)
            {
                var content = await request.Content.ReadAsStringAsync(cancellationToken);
                Body = System.Text.Json.JsonDocument.Parse(content);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
