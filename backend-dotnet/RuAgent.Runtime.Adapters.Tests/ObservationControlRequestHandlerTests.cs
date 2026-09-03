using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Runtime.Adapters.Observation;
using RuAgent.Runtime.Adapters.Runtime;
using RuAgent.Runtime.Core.Contracts;
using Xunit;

namespace RuAgent.Runtime.Adapters.Tests;

public sealed class ObservationControlRequestHandlerTests
{
    [Fact]
    public async Task Status_is_explicitly_disabled_until_a_managed_capture_loop_exists()
    {
        var handler = Handler("Ollama");

        var response = await InvokeAsync(handler, "GET", "/observation/status", null);
        var body = Body(response);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("disabled", body["status"]!.GetValue<string>());
        Assert.False(body["active"]!.GetValue<bool>());
        Assert.Equal("disabled", body["engine"]!.GetValue<string>());
    }

    [Fact]
    public async Task Start_and_react_fail_closed_with_bounded_json_responses()
    {
        var handler = Handler("OpenCode Zen");

        var start = await InvokeAsync(
            handler,
            "POST",
            "/observation/start",
            "{\"interval_seconds\":1}");
        Assert.Equal(200, start.StatusCode);
        Assert.Equal("error", Body(start)["status"]!.GetValue<string>());

        var invalidReact = await InvokeAsync(
            handler,
            "POST",
            "/observation/react",
            "{\"observation_id\":\"not valid\"}");
        Assert.Equal(422, invalidReact.StatusCode);

        var react = await InvokeAsync(
            handler,
            "POST",
            "/observation/react",
            "{\"observation_id\":\"obs-1\",\"event_age_ms\":10,\"observation\":{\"scene\":\"A scene\",\"event\":\"An event\",\"confidence\":0.5,\"novelty\":0.5,\"comment_worthiness\":0.5,\"visible_text\":[]}}");
        Assert.Equal("skipped", Body(react)["status"]!.GetValue<string>());
        Assert.Equal("observation_runtime_unavailable", Body(react)["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task React_rejects_non_ascii_observation_ids_before_unavailable_short_circuit()
    {
        var handler = Handler("OpenCode Zen");

        var response = await InvokeAsync(
            handler,
            "POST",
            "/observation/react",
            "{\"observation_id\":\"obs-é\"}");

        Assert.Equal(422, response.StatusCode);
    }

    [Fact]
    public async Task React_treats_missing_visible_text_as_an_empty_list()
    {
        var handler = Handler("OpenCode Zen");

        var response = await InvokeAsync(
            handler,
            "POST",
            "/observation/react",
            "{\"observation_id\":\"obs-1\",\"event_age_ms\":10,\"observation\":{\"scene\":\"A scene\",\"event\":\"An event\",\"confidence\":0.5,\"novelty\":0.5,\"comment_worthiness\":0.5}}");

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("skipped", Body(response)["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task React_rejects_oversized_body_before_parsing()
    {
        var handler = Handler("OpenCode Zen");
        var error = await Assert.ThrowsAsync<PrivateRuntimeRequestException>(() => InvokeAsync(
            handler,
            "POST",
            "/observation/react",
            new string('x', 256 * 1024 + 1)));

        Assert.Equal("invalid_request", error.Code);
    }

    [Theory]
    [InlineData("confidence", 2.0)]
    [InlineData("novelty", -0.1)]
    [InlineData("comment_worthiness", 1.1)]
    public async Task React_rejects_out_of_range_observation_values(string field, double value)
    {
        var handler = Handler("OpenCode Zen");
        var payload = new JsonObject
        {
            ["observation_id"] = "obs-1",
            ["event_age_ms"] = 10,
            ["observation"] = new JsonObject
            {
                ["scene"] = "A scene",
                ["event"] = "An event",
                ["confidence"] = 0.5,
                ["novelty"] = 0.5,
                ["comment_worthiness"] = 0.5,
                ["visible_text"] = new JsonArray(),
            },
        };
        payload["observation"]![field] = value;

        var response = await InvokeAsync(handler, "POST", "/observation/react", payload.ToJsonString());

        Assert.Equal(422, response.StatusCode);
    }

    private static ObservationControlRequestHandler Handler(string backend) =>
        new(
            new ManagedRuntimeSettingsService(new RuntimeSettingsSnapshot(
                backend,
                "model",
                null,
                "Default",
                false)),
            voiceAvailable: true);

    private static Task<PrivateRuntimeResponse> InvokeAsync(
        ObservationControlRequestHandler handler,
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
}
