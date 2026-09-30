using System.Net;
using System.Text;
using RuAgent.Client;
using Xunit;

namespace RuAgent.Client.Tests;

/// <summary>
/// The client end of session interchange, exercised against a stub host: what the envelope
/// says, and what happens when the host refuses. The refusals matter more than the happy
/// path here — the host's sentence is the useful part of a failed export, and a client that
/// replaced it with "request failed" would throw that away.
/// </summary>
public sealed class SessionInterchangeApiClientTests
{
    private static PublicHostSession Session()
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var descriptor = new RuAgent.Protocol.Harness.RuntimeDescriptor(
            1,
            "http://127.0.0.1:8000/",
            new string('e', 64),
            proc.Id,
            proc.StartTime.ToUniversalTime().ToString("O"),
            new string('a', 32),
            DateTimeOffset.UtcNow.ToString("O"));
        return PublicHostSession.FromDescriptor(descriptor);
    }

    [Fact]
    public async Task Export_reads_the_files_and_where_they_belong()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            {"status":"ok","harness":"claude-code","display_name":"Claude Code",
             "session":{"id":"s1","title":"Bring me across","messages":2,
                        "working_directory":"D:\\LLM","created_at":"2026-09-20T09:30:00+00:00"},
             "files":[{"name":"11111111-1111-7111-8111-111111111111.jsonl","content":"{\"a\":1}"}]}
            """);
        var client = new SessionInterchangeApiClient(Session(), new HttpClient(handler));

        var result = await client.ExportAsync("s1", "claude-code");

        Assert.Equal("claude-code", result.Harness);
        Assert.Equal("Claude Code", result.DisplayName);
        Assert.Equal(@"D:\LLM", result.WorkingDirectory);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 9, 30, 0, TimeSpan.Zero), result.CreatedAt);

        var file = Assert.Single(result.Files);
        Assert.Equal("11111111-1111-7111-8111-111111111111.jsonl", file.Name);
        Assert.Equal("{\"a\":1}", file.Content);

        // The format is asked for on the query string, which is how the host knows where the
        // session is going before it writes anything.
        Assert.Contains("/sessions/s1/export?harness=claude-code", handler.LastRequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_carries_the_hosts_own_words_when_it_refuses()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK,
            """{"status":"error","message":"Antigravity sessions cannot be written: protobuf payloads."}""");
        var client = new SessionInterchangeApiClient(Session(), new HttpClient(handler));

        var error = await Assert.ThrowsAsync<AgentApiException>(() => client.ExportAsync("s1", "antigravity"));

        Assert.Contains("protobuf", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_refuses_an_envelope_that_carries_no_files()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"status":"ok","harness":"codex","session":{"id":"s1"}}""");
        var client = new SessionInterchangeApiClient(Session(), new HttpClient(handler));

        var error = await Assert.ThrowsAsync<AgentApiException>(() => client.ExportAsync("s1", "codex"));

        // A success that produced nothing is still a failure: the caller has to write files.
        Assert.Equal("session_export_empty", error.ErrorCode);
    }

    [Fact]
    public async Task Import_reads_the_new_session_and_what_stayed_behind()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK,
            """{"status":"ok","harness":"codex","session":{"id":"s9","title":"Imported","messages":4},"dropped_entries":3}""");
        var client = new SessionInterchangeApiClient(Session(), new HttpClient(handler));

        var result = await client.ImportAsync("codex", "{\"line\":1}");

        Assert.Equal("s9", result.SessionId);
        Assert.Equal("Imported", result.Title);
        Assert.Equal(4, result.Messages);
        Assert.Equal(3, result.DroppedEntries);

        // The file's own bytes travel unchanged inside the envelope, so the host reads what
        // the other agent actually wrote rather than something this client re-encoded.
        using var posted = System.Text.Json.JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("codex", posted.RootElement.GetProperty("harness").GetString());
        Assert.Equal("{\"line\":1}", posted.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Import_raises_the_hosts_refusal_rather_than_its_own()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK,
            """{"status":"error","message":"This payload has neither a session_meta line nor a response_item line, so it is not a Codex rollout."}""");
        var client = new SessionInterchangeApiClient(Session(), new HttpClient(handler));

        var error = await Assert.ThrowsAsync<AgentApiException>(() => client.ImportAsync("codex", "{}"));

        Assert.Contains("Codex rollout", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_surfaces_the_import_too_large_413_instead_of_only_the_status()
    {
        var handler = new StubHandler(
            HttpStatusCode.RequestEntityTooLarge,
            """{"status":"error","message":"import_too_large: session files are limited to 16 MiB."}""");
        var client = new SessionInterchangeApiClient(Session(), new HttpClient(handler));

        var error = await Assert.ThrowsAsync<AgentApiException>(() => client.ImportAsync("claude-code", "{}"));

        Assert.Equal(413, error.StatusCode);
        Assert.Contains("import_too_large", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejected_bearer_is_reported_as_a_rejected_bearer()
    {
        var handler = new StubHandler(HttpStatusCode.Forbidden, string.Empty);
        var client = new SessionInterchangeApiClient(Session(), new HttpClient(handler));

        var error = await Assert.ThrowsAsync<AgentApiException>(() => client.ExportAsync("s1", "codex"));

        Assert.Equal("unauthorized", error.ErrorCode);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public Uri? LastRequestUri { get; private set; }

        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
