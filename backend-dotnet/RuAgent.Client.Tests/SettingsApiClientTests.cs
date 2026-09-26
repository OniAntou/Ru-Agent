using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace RuAgent.Client.Tests;

public sealed class SettingsApiClientTests
{
    private const string Bearer = "bearer-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static PublicHostSession Session()
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var descriptor = new RuAgent.Protocol.Harness.RuntimeDescriptor(
            1, "http://127.0.0.1:8000/", Bearer, proc.Id,
            proc.StartTime.ToUniversalTime().ToString("O"), new string('a', 32),
            DateTimeOffset.UtcNow.ToString("O"));
        return PublicHostSession.FromDescriptor(descriptor);
    }

    private static SettingsApiClient Client(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        new(Session(), new HttpClient(new FakeHandler(handler)));

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task GetModelsDevReadsIdsAndNames()
    {
        var client = Client(request =>
        {
            Assert.EndsWith("/models-dev", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(Bearer, request.Headers.Authorization?.Parameter);
            return Json(HttpStatusCode.OK, """
                {"status":"ok","providers":[
                  {"id":"openrouter","name":"OpenRouter","base_url":"https://openrouter.ai/api/v1"},
                  {"id":"groq","name":"Groq","base_url":"https://api.groq.com/openai/v1"}]}
                """);
        });

        var providers = await client.GetModelsDevAsync(CancellationToken.None);

        Assert.Equal(2, providers.Count);
        Assert.Equal(("openrouter", "OpenRouter"), providers[0]);
        Assert.Equal(("groq", "Groq"), providers[1]);
    }

    [Fact]
    public async Task GetModelsDevDropsUnusableEntriesInsteadOfFailingTheList()
    {
        var client = Client(_ => Json(HttpStatusCode.OK, """
            {"status":"ok","providers":[
              {"id":"openrouter","name":"OpenRouter"},
              {"name":"No id at all"},
              {"id":"","name":"blank id"},
              {"id":"bad\u0001id","name":"control char"},
              {"id":"openrouter","name":"duplicate"},
              {"id":"noname"}]}
            """));

        var providers = await client.GetModelsDevAsync(CancellationToken.None);

        Assert.Equal(2, providers.Count);
        Assert.Equal("openrouter", providers[0].Id);
        Assert.Equal("noname", providers[1].Id);
        Assert.Equal("noname", providers[1].Name);
    }

    [Fact]
    public async Task GetModelsDevTruncatesAnUnboundedList()
    {
        var providers = Enumerable.Range(0, 1200).Select(i => new { id = "p" + i, name = "P" + i }).ToArray();
        var client = Client(_ => Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { status = "ok", providers })));

        var read = await client.GetModelsDevAsync(CancellationToken.None);

        Assert.Equal(1000, read.Count);
    }

    [Fact]
    public async Task GetModelsDevRejectsAnErrorEnvelope()
    {
        var client = Client(_ => Json(HttpStatusCode.OK, """{"status":"error","providers":[]}"""));

        var error = await Assert.ThrowsAsync<AgentApiException>(() => client.GetModelsDevAsync(CancellationToken.None));

        Assert.Equal("invalid_response", error.ErrorCode);
    }

    [Fact]
    public async Task GetModelsDevRejectsARejectedBearerSeparatelyFromAnErrorBody()
    {
        var client = Client(_ => Json(HttpStatusCode.Unauthorized, """{"status":"error"}"""));

        var error = await Assert.ThrowsAsync<AgentApiException>(() => client.GetModelsDevAsync(CancellationToken.None));

        Assert.Equal("unauthorized", error.ErrorCode);
    }

    [Fact]
    public async Task GetModelsDevRejectsUnparsableJson()
    {
        var client = Client(_ => Json(HttpStatusCode.OK, "{not json"));

        var error = await Assert.ThrowsAsync<AgentApiException>(() => client.GetModelsDevAsync(CancellationToken.None));

        Assert.Equal("invalid_response", error.ErrorCode);
    }

    [Fact]
    public async Task GetReasoningReadsTheSelectedModelOutOfCatalog()
    {
        var client = Client(request =>
        {
            Assert.EndsWith("/models?backend=models.dev%3Aopenrouter", request.RequestUri!.PathAndQuery, StringComparison.Ordinal);
            return Json(HttpStatusCode.OK, """
                {"status":"ok","backend":"models.dev:openrouter","identity":"abc123","selected_model":"gpt-5","selected_effort":"high",
                 "models":[{"id":"gpt-5","reasoning":{"state":"levels","options":["low","medium","high"]}}],
                 "catalog":[{"id":"gpt-5","reasoning":{"state":"levels","options":["low","medium","high"]}}]}
                """);
        });

        var snapshot = await client.GetReasoningAsync("models.dev:openrouter", CancellationToken.None);

        Assert.Equal("abc123", snapshot.Identity);
        Assert.Equal("gpt-5", snapshot.SelectedModel);
        Assert.Equal("high", snapshot.SelectedEffort);
        Assert.Equal("levels", snapshot.State);
        Assert.Equal(["low", "medium", "high"], snapshot.Options);
    }

    [Fact]
    public async Task GetReasoningFallsBackToTheModelsArrayWhenCatalogOmitsTheSelection()
    {
        // The host has been observed listing the selected model in only one of the two arrays.
        var client = Client(_ => Json(HttpStatusCode.OK, """
            {"status":"ok","identity":"id-1","selected_model":"gpt-5","selected_effort":"default",
             "models":[{"id":"other","reasoning":{"state":"levels","options":["low"]}},
                       {"id":"gpt-5","reasoning":{"state":"toggle","options":["enabled","disabled"]}}],
             "catalog":[{"id":"other","reasoning":{"state":"levels","options":["low"]}}]}
            """));

        var snapshot = await client.GetReasoningAsync("openai", CancellationToken.None);

        Assert.Equal("toggle", snapshot.State);
        Assert.Equal(["enabled", "disabled"], snapshot.Options);
    }

    [Fact]
    public async Task GetReasoningReportsUnknownWhenTheSelectionIsNotListedAnywhere()
    {
        var client = Client(_ => Json(HttpStatusCode.OK, """
            {"status":"ok","identity":"id-1","selected_model":"missing","selected_effort":"default",
             "models":[{"id":"other","reasoning":{"state":"levels","options":["low"]}}],"catalog":[]}
            """));

        var snapshot = await client.GetReasoningAsync("openai", CancellationToken.None);

        Assert.Equal("missing", snapshot.SelectedModel);
        Assert.Equal("default", snapshot.SelectedEffort);
        Assert.Equal("unknown", snapshot.State);
        Assert.Empty(snapshot.Options);
    }

    [Fact]
    public async Task GetReasoningBoundsTheOptionList()
    {
        var options = Enumerable.Range(0, 40).Select(i => "eff" + i).ToArray();
        var client = Client(_ => Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            status = "ok",
            identity = "id-1",
            selected_model = "m",
            selected_effort = "default",
            catalog = new[] { new { id = "m", reasoning = new { state = "levels", options } } },
            models = Array.Empty<object>(),
        })));

        var snapshot = await client.GetReasoningAsync("openai", CancellationToken.None);

        Assert.Equal(16, snapshot.Options.Count);
    }

    [Fact]
    public async Task GetReasoningRejectsAnHttpFailure()
    {
        var client = Client(_ => Json(HttpStatusCode.InternalServerError, """{"status":"error"}"""));

        var error = await Assert.ThrowsAsync<AgentApiException>(() => client.GetReasoningAsync("openai", CancellationToken.None));

        Assert.Equal("catalog_http_error", error.ErrorCode);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _func;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> func) => _func = func;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_func(request));
    }
}
