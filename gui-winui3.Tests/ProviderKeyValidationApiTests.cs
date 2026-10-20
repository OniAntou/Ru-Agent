using System.Net;
using System.Text.Json;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class ProviderKeyValidationApiTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK, "{\"status\":\"success\"}", "valid", 1)]
    [InlineData(HttpStatusCode.OK, "{\"status\":\"error\",\"code\":\"provider_key_rejected\"}", "invalid", 0)]
    [InlineData(HttpStatusCode.OK, "{\"status\":\"error\",\"code\":\"provider_key_unverified\"}", "unverified", 0)]
    [InlineData(HttpStatusCode.NotFound, "{}", "save_failed", 0)]
    public async Task Validated_key_uses_its_own_route_and_never_falls_back_to_settings(
        HttpStatusCode status, string body, string expected, int notifications)
    {
        using var handler = new Handler(status, body);
        using var api = new ApiService(new BackendSession(new Uri("http://127.0.0.1:59999/"), "synthetic"), handler);
        int saved = 0;
        api.SettingsSaved += (_, _) => saved++;
        var result = await api.ValidateAndSaveModelsDevKeyAsync("models.dev:synthetic", "synthetic-key", default);
        Assert.Equal(expected, result.Code);
        Assert.Equal(notifications, saved);
        Assert.Equal(1, handler.Calls);
    }

    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal("/settings/provider/key", request.RequestUri!.AbsolutePath);
            Assert.Equal(HttpMethod.Post, request.Method);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal(2, json.RootElement.EnumerateObject().Count());
            Assert.Equal("models.dev:synthetic", json.RootElement.GetProperty("models_dev_provider_id").GetString());
            Assert.Equal("synthetic-key", json.RootElement.GetProperty("models_dev_api_key").GetString());
            return new(status) { Content = new StringContent(body) };
        }
    }
}
