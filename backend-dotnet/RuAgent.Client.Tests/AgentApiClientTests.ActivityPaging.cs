using Xunit;

namespace RuAgent.Client.Tests;

public sealed partial class AgentApiClientTests
{
    private const string PagedRunId = "run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task The_newest_activity_page_sends_no_cursor_and_reads_the_next_one()
    {
        string? query = null;
        var client = new AgentApiClient(Session(), new HttpClient(new FakeHandler(request =>
        {
            query = request.RequestUri!.PathAndQuery;
            return Response(new
            {
                activities = Array.Empty<object>(),
                transcript = Array.Empty<object>(),
                has_more = true,
                next_before_sequence = 812L,
                older_count = 340,
            });
        })));

        var page = await client.ListRunActivityReplayAsync(PagedRunId);

        Assert.Equal($"/agent/v1/runs/{PagedRunId}/activities", query);
        Assert.True(page.HasMore);
        Assert.Equal(812L, page.NextBeforeSequence);
        Assert.Equal(340, page.OlderCount);
    }

    [Fact]
    public async Task An_older_page_sends_its_cursor_and_limit()
    {
        string? query = null;
        var client = new AgentApiClient(Session(), new HttpClient(new FakeHandler(request =>
        {
            query = request.RequestUri!.PathAndQuery;
            return Response(new { activities = Array.Empty<object>(), transcript = Array.Empty<object>(), has_more = false });
        })));

        var page = await client.ListRunActivityReplayAsync(PagedRunId, beforeSequence: 812, limit: 200);

        Assert.Equal($"/agent/v1/runs/{PagedRunId}/activities?before_sequence=812&limit=200", query);
        Assert.False(page.HasMore);
        Assert.Null(page.NextBeforeSequence);
    }

    [Theory]
    [InlineData("{\"activities\":[],\"transcript\":[]}")]
    [InlineData("{\"activities\":[],\"has_more\":true}")]
    [InlineData("{\"activities\":[],\"has_more\":true,\"next_before_sequence\":0,\"older_count\":5}")]
    public async Task A_page_without_a_usable_cursor_is_the_last_one(string body)
    {
        var client = new AgentApiClient(Session(), new HttpClient(new FakeHandler(_ =>
            new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            })));

        var page = await client.ListRunActivityReplayAsync(PagedRunId);

        Assert.False(page.HasMore);
        Assert.Null(page.NextBeforeSequence);
        Assert.Equal(0, page.OlderCount);
    }
}
