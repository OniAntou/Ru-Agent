using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Runtime.Adapters.Devices;
using RuAgent.Runtime.Adapters.Runtime;
using RuAgent.Runtime.Core.Contracts;
using Xunit;

namespace RuAgent.Runtime.Adapters.Tests;

public sealed class VtubeStudioRuntimeRequestHandlerTests
{
    [Fact]
    public async Task Connect_status_and_catalog_action_share_managed_state()
    {
        var client = new FakeVtubeStudioClient();
        var handler = new VtubeStudioRuntimeRequestHandler(client);

        var before = await InvokeAsync(handler, "GET", "/vts/status", null);
        Assert.False(Body(before)["connected"]!.GetValue<bool>());

        var connect = await InvokeAsync(handler, "POST", "/vts/connect", null);
        Assert.Equal("ok", Body(connect)["status"]!.GetValue<string>());
        Assert.True(Body(connect)["connected"]!.GetValue<bool>());

        var status = await InvokeAsync(handler, "GET", "/vts/status", null);
        Assert.Equal("Ru", Body(status)["model_name"]!.GetValue<string>());
        Assert.Equal(["expr_01"], Body(status)["expressions"]!.AsArray().Select(node => node!.GetValue<string>()));

        var action = await InvokeAsync(
            handler,
            "POST",
            "/vts/action",
            new JsonObject { ["codes"] = "vts_01, expr_01" }.ToJsonString());
        Assert.Equal("ok", Body(action)["status"]!.GetValue<string>());
        Assert.Equal(["vts_01", "expr_01"], client.LastCodes);
    }

    [Fact]
    public async Task Config_and_gui_state_are_bounded_and_persisted_in_handler()
    {
        var handler = new VtubeStudioRuntimeRequestHandler(new FakeVtubeStudioClient());

        var config = await InvokeAsync(
            handler,
            "POST",
            "/vts/config",
            new JsonObject
            {
                ["sway_amplitude_idle"] = 18.0,
                ["micro_action_min_delay"] = 10.0,
                ["micro_action_max_delay"] = 20.0,
            }.ToJsonString());
        Assert.Equal("ok", Body(config)["status"]!.GetValue<string>());
        Assert.Equal(18.0, Body(config)["config"]!["sway_amplitude_idle"]!.GetValue<double>());

        var invalid = await InvokeAsync(
            handler,
            "POST",
            "/vts/config",
            new JsonObject { ["sway_amplitude_idle"] = 999.0 }.ToJsonString());
        Assert.Equal("error", Body(invalid)["status"]!.GetValue<string>());

        var mouse = await InvokeAsync(
            handler,
            "POST",
            "/vts/mouse-look",
            new JsonObject { ["enabled"] = true, ["sensitivity"] = 1.5 }.ToJsonString());
        Assert.Equal("ok", Body(mouse)["status"]!.GetValue<string>());
        var mouseRead = await InvokeAsync(handler, "GET", "/vts/mouse-look", null);
        Assert.True(Body(mouseRead)["mouse_look"]!["enabled"]!.GetValue<bool>());
        Assert.Equal(1.5, Body(mouseRead)["mouse_look"]!["sensitivity"]!.GetValue<double>());
    }

    [Fact]
    public async Task Overlay_enable_fails_closed_when_no_managed_overlay_process_exists()
    {
        var handler = new VtubeStudioRuntimeRequestHandler(new FakeVtubeStudioClient());

        var response = await InvokeAsync(
            handler,
            "POST",
            "/vts/overlay",
            "{\"enabled\":true}");

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("error", Body(response)["status"]!.GetValue<string>());
        Assert.Equal("overlay_unavailable", Body(response)["reason"]!.GetValue<string>());

        var state = await InvokeAsync(handler, "GET", "/vts/overlay", null);
        Assert.False(Body(state)["overlay"]!["enabled"]!.GetValue<bool>());
        Assert.False(Body(state)["overlay"]!["running"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Connect_failure_does_not_publish_provider_exception_text()
    {
        const string secret = "vts-secret-from-provider";
        var handler = new VtubeStudioRuntimeRequestHandler(
            new FakeVtubeStudioClient
            {
                ReadError = new VtubeStudioProviderException(secret, retryable: false),
            });

        var connect = await InvokeAsync(handler, "POST", "/vts/connect", null);
        var status = await InvokeAsync(handler, "GET", "/vts/status", null);

        Assert.Equal("error", Body(connect)["status"]!.GetValue<string>());
        Assert.Equal("vts_unavailable", Body(status)["connect_error"]!.GetValue<string>());
        Assert.DoesNotContain(secret, Encoding.UTF8.GetString(status.Body.Span), StringComparison.Ordinal);
    }

    private static Task<PrivateRuntimeResponse> InvokeAsync(
        VtubeStudioRuntimeRequestHandler handler,
        string method,
        string path,
        string? body) => handler.InvokeAsync(
            new PrivateRuntimeRequest(
                method,
                path,
                body is null ? null : "application/json",
                body is null ? ReadOnlyMemory<byte>.Empty : Encoding.UTF8.GetBytes(body)),
            CancellationToken.None);

    private static JsonObject Body(PrivateRuntimeResponse response) =>
        JsonNode.Parse(Encoding.UTF8.GetString(response.Body.Span))!.AsObject();

    private sealed class FakeVtubeStudioClient : IVtubeStudioClient
    {
        public IReadOnlyList<string> LastCodes { get; private set; } = [];

        public Exception? ReadError { get; init; }

        public Task<VtsCatalog> ReadCatalogAsync(CancellationToken cancellationToken)
        {
            if (ReadError is not null)
            {
                throw ReadError;
            }

            return Task.FromResult(new VtsCatalog("model-1", "Ru", ["vts_01"], ["expr_01"]));
        }

        public Task TriggerAsync(IReadOnlyList<string> catalogCodes, CancellationToken cancellationToken)
        {
            LastCodes = catalogCodes.ToArray();
            return Task.CompletedTask;
        }
    }
}
