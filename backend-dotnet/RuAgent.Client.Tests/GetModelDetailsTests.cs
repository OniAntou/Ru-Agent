using System.Net;
using Xunit;

namespace RuAgent.Client.Tests;

public sealed class GetModelDetailsTests
{
    [Fact]
    public async Task ReadsContextAndRatesForTheRequestedModel()
    {
        var client = SettingsApiClientTests.Client(request =>
        {
            Assert.Contains("/models", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            Assert.Contains("backend=models.dev%3Azen", request.RequestUri.Query, StringComparison.Ordinal);
            return SettingsApiClientTests.Json(HttpStatusCode.OK, """
                {"status":"ok","catalog":[
                  {"id":"other","supported":true,"context_length":8000},
                  {"id":"vendor/ctx","supported":true,"context_length":131072,
                   "cost":{"input":1.5,"output":6,"cache_read":0.15}}
                ]}
                """);
        });

        var details = await client.GetModelDetailsAsync("models.dev:zen", "vendor/ctx", CancellationToken.None);

        Assert.NotNull(details);
        Assert.Equal(131072, details!.ContextLength);
        Assert.Equal(1.5, details.InputCost);
        Assert.Equal(6, details.OutputCost);
        Assert.Equal(0.15, details.CacheReadCost);
        Assert.Null(details.CacheWriteCost);
    }

    [Fact]
    public async Task UnpublishedFieldsStayNull()
    {
        var client = SettingsApiClientTests.Client(_ => SettingsApiClientTests.Json(HttpStatusCode.OK, """
            {"status":"ok","catalog":[{"id":"vendor/plain","supported":true}]}
            """));

        var details = await client.GetModelDetailsAsync(null, "vendor/plain", CancellationToken.None);

        Assert.NotNull(details);
        Assert.Null(details!.ContextLength);
        Assert.Null(details.InputCost);
        Assert.Null(details.OutputCost);
    }

    [Fact]
    public async Task AMissingModelReturnsNull()
    {
        var client = SettingsApiClientTests.Client(_ => SettingsApiClientTests.Json(HttpStatusCode.OK, """
            {"status":"ok","catalog":[{"id":"vendor/other","supported":true}]}
            """));

        var details = await client.GetModelDetailsAsync(null, "vendor/absent", CancellationToken.None);

        Assert.Null(details);
    }
}
