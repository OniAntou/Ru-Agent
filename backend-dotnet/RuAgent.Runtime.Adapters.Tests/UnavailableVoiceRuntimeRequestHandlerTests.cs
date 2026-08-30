using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Runtime.Adapters.Audio;
using RuAgent.Runtime.Adapters.Runtime;
using Xunit;

namespace RuAgent.Runtime.Adapters.Tests;

public sealed class UnavailableVoiceRuntimeRequestHandlerTests
{
    [Fact]
    public async Task Status_is_concrete_and_marks_voice_unavailable()
    {
        var handler = new UnavailableVoiceRuntimeRequestHandler();
        var response = await handler.InvokeAsync(
            new PrivateRuntimeRequest("GET", "/voice/status", null, ReadOnlyMemory<byte>.Empty),
            CancellationToken.None);

        Assert.Equal(200, response.StatusCode);
        var body = JsonNode.Parse(Encoding.UTF8.GetString(response.Body.Span))!.AsObject();
        Assert.Equal("ok", body["status"]!.GetValue<string>());
        Assert.False(body["available"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Synthesis_reports_missing_text_without_exposing_provider_details()
    {
        var handler = new UnavailableVoiceRuntimeRequestHandler();
        var response = await handler.InvokeAsync(
            new PrivateRuntimeRequest("POST", "/voice/tts", "application/json", Encoding.UTF8.GetBytes("{}")),
            CancellationToken.None);

        Assert.Equal(200, response.StatusCode);
        var body = JsonNode.Parse(Encoding.UTF8.GetString(response.Body.Span))!.AsObject();
        Assert.Equal("error", body["status"]!.GetValue<string>());
        Assert.Equal("Voice text is required.", body["message"]!.GetValue<string>());
        Assert.DoesNotContain("provider", body.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Latency_rejects_malformed_or_unknown_payload_fields()
    {
        var handler = new UnavailableVoiceRuntimeRequestHandler();
        var response = await handler.InvokeAsync(
            new PrivateRuntimeRequest(
                "POST",
                "/voice/latency",
                "application/json",
                Encoding.UTF8.GetBytes("{\"outcome\":\"completed\",\"unknown\":1}")),
            CancellationToken.None);

        Assert.Equal(422, response.StatusCode);
    }

    [Fact]
    public async Task Lifecycle_routes_validate_json_before_reporting_unavailable()
    {
        var handler = new UnavailableVoiceRuntimeRequestHandler();
        var error = await Assert.ThrowsAsync<PrivateRuntimeRequestException>(() => handler.InvokeAsync(
            new PrivateRuntimeRequest(
                "POST",
                "/voice/warmup",
                "application/json",
                Encoding.UTF8.GetBytes("not-json")),
            CancellationToken.None));

        Assert.Equal("invalid_request", error.Code);
    }
}
