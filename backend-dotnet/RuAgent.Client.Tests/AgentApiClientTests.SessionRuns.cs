using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace RuAgent.Client.Tests;

public sealed partial class AgentApiClientTests
{
    [Fact]
    public async Task ListSessionRuns_returns_summaries_oldest_first_in_one_authenticated_request()
    {
        const string sessionId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string olderRunId = "run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string newerRunId = "run_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var older = JsonSerializer.SerializeToNode(RunSummary("OpenAI", "gpt-5"))!.AsObject();
        older["status"] = "completed";
        older["terminal_at"] = "2026-10-04T00:00:02Z";
        var newer = older.DeepClone().AsObject();
        newer["run_id"] = newerRunId;
        newer["status"] = "running";
        newer["terminal_at"] = null;
        newer["last_sequence"] = 7L;
        var body = new JsonObject
        {
            ["items"] = new JsonArray(newer, older),
            ["next_cursor"] = "older-page",
        }.ToJsonString();
        var requests = 0;
        var session = Session();
        var client = new AgentApiClient(session, new HttpClient(new FakeHandler(request =>
        {
            requests++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal($"/agent/v1/sessions/{sessionId}/runs?limit=100", request.RequestUri!.PathAndQuery);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(session.BearerToken, request.Headers.Authorization?.Parameter);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        })));

        var runs = await client.ListSessionRunsAsync(sessionId);

        Assert.Equal(1, requests);
        Assert.Equal(new[] { olderRunId, newerRunId }, runs.Select(run => run.RunId));
        Assert.Equal("completed", runs[0].Status);
        Assert.Equal("2026-10-04T00:00:02Z", runs[0].TerminalAt);
        Assert.Equal("running", runs[1].Status);
        Assert.Equal(7L, runs[1].LastSequence);
        Assert.Equal("OpenAI", runs[1].ProviderBackend);
        Assert.Equal("gpt-5", runs[1].ProviderModel);
        Assert.Equal(128_000L, runs[1].ContextWindowTokens);
    }

    [Fact]
    public async Task ListSessionRunIds_preserves_oldest_first_order_in_one_request()
    {
        var newer = JsonSerializer.SerializeToNode(RunSummary("OpenAI", "gpt-5"))!.AsObject();
        newer["run_id"] = "run_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var older = JsonSerializer.SerializeToNode(RunSummary("OpenAI", "gpt-5"))!;
        var requests = 0;
        var client = new AgentApiClient(Session(), new HttpClient(new FakeHandler(_ =>
        {
            requests++;
            return Response(new { items = new[] { newer, older }, next_cursor = (string?)null });
        })));

        var ids = await client.ListSessionRunIdsAsync("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        Assert.Equal(1, requests);
        Assert.Equal(new[] { "run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "run_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }, ids);
    }

    [Theory]
    [InlineData(false, 401, "{}", "unauthorized")]
    [InlineData(true, 403, "{}", "unauthorized")]
    [InlineData(false, 404, "{\"error_code\":\"session_not_found\"}", "session_not_found")]
    [InlineData(true, 500, "unavailable", "session_runs_failed")]
    public async Task Session_run_readers_preserve_http_errors(bool idsOnly, int status, string body, string errorCode)
    {
        var client = new AgentApiClient(Session(), new HttpClient(new FakeHandler(_ =>
            new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            })));

        var error = await Assert.ThrowsAsync<AgentApiException>(async () =>
        {
            if (idsOnly)
                await client.ListSessionRunIdsAsync("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            else
                await client.ListSessionRunsAsync("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        });

        Assert.Equal(errorCode, error.ErrorCode);
        Assert.Equal(status, error.StatusCode);
    }

    [Theory]
    [InlineData(false, "null")]
    [InlineData(true, "{}")]
    [InlineData(false, "{\"items\":null}")]
    [InlineData(true, "{\"items\":{}}")]
    [InlineData(false, "{")]
    public async Task Session_run_readers_reject_invalid_list_shapes(bool idsOnly, string body)
    {
        var client = new AgentApiClient(Session(), new HttpClient(new FakeHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            })));

        var error = await Assert.ThrowsAsync<AgentApiException>(async () =>
        {
            if (idsOnly)
                await client.ListSessionRunIdsAsync("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            else
                await client.ListSessionRunsAsync("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        });

        Assert.Equal("invalid_response", error.ErrorCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Session_run_readers_honor_cancellation(bool idsOnly)
    {
        var client = new AgentApiClient(Session(), new HttpClient(new FakeHandler(_ =>
            Response(new { items = Array.Empty<object>(), next_cursor = (string?)null }))));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (idsOnly)
                await client.ListSessionRunIdsAsync("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", cancellation.Token);
            else
                await client.ListSessionRunsAsync("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", cancellation.Token);
        });

    }
}
