using System.Net;
using System.Text;
using Xunit;

namespace RuAgent.Client.Tests;

public sealed partial class AgentApiClientTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"items\":null}")]
    [InlineData("{\"items\":{}}")]
    public async Task Policy_list_rejects_missing_or_invalid_items_instead_of_claiming_no_rules(string json)
    {
        using var http = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") }));
        using var client = new AgentApiClient(Session(), http);
        var error = await Assert.ThrowsAsync<AgentApiException>(() => client.ListPoliciesAsync("global"));
        Assert.Equal("invalid_response", error.ErrorCode);
    }

    [Fact]
    public async Task Policy_list_accepts_an_explicit_empty_array()
    {
        using var http = new HttpClient(new FakeHandler(_ => Response(new { items = Array.Empty<object>() })));
        using var client = new AgentApiClient(Session(), http);
        Assert.Empty(await client.ListPoliciesAsync("global"));
    }
}
