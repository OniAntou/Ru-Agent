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
    [InlineData(HttpStatusCode.OK, "{\"status\":\"success\",\"code\":\"provider_key_unchecked\"}", "unchecked", 1)]
    [InlineData(HttpStatusCode.OK, "{\"status\":\"error\",\"code\":\"provider_key_malformed\"}", "malformed", 0)]
    [InlineData(HttpStatusCode.OK, "{\"status\":\"error\",\"code\":\"provider_key_too_long\"}", "too_long", 0)]
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

    [Theory]
    [InlineData("{\"status\":\"success\"}", true, null)]
    [InlineData("{\"status\":\"success\",\"code\":\"provider_key_unchecked\"}", true, "provider_key_unchecked")]
    [InlineData("{\"status\":\"error\",\"code\":\"provider_key_rejected\"}", false, "provider_key_rejected")]
    public async Task A_custom_provider_saves_its_definition_then_checks_its_key_alone(
        string keyReply, bool success, string? code)
    {
        var calls = new List<(string Path, JsonDocument Body)>();
        using var handler = new RoutingHandler(async request =>
        {
            calls.Add((request.RequestUri!.AbsolutePath, JsonDocument.Parse(await request.Content!.ReadAsStringAsync())));
            return request.RequestUri!.AbsolutePath == "/settings/provider/key" ? keyReply : "{\"status\":\"success\"}";
        });
        using var api = new ApiService(new BackendSession(new Uri("http://127.0.0.1:59999/"), "synthetic"), handler);

        var result = await api.SaveCustomProvidersAsync([], "lab", "synthetic-key");

        Assert.Equal(success, result.Success);
        Assert.Equal(code, result.Code);
        Assert.Equal(["/settings", "/settings/provider/key"], calls.Select(call => call.Path));
        Assert.False(calls[0].Body.RootElement.TryGetProperty("custom_provider_api_key", out _));
        var key = calls[1].Body.RootElement;
        Assert.Equal(2, key.EnumerateObject().Count());
        Assert.Equal("lab", key.GetProperty("custom_provider_id").GetString());
        Assert.Equal("synthetic-key", key.GetProperty("custom_provider_api_key").GetString());
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, Task<string>> reply) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            new(HttpStatusCode.OK) { Content = new StringContent(await reply(request)) };
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
