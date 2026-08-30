using System.Text.Json.Nodes;
using RuAgent.Runtime.Adapters.Runtime;
using RuAgent.Runtime.Core.Contracts;
using Xunit;

namespace RuAgent.Runtime.Adapters.Tests;

public sealed class ManagedRuntimeStatusRequestHandlerTests
{
    [Fact]
    public async Task Status_and_models_report_the_active_typed_settings()
    {
        var settings = new ManagedRuntimeSettingsService(new RuntimeSettingsSnapshot(
            "Ollama",
            "qwen3:8b",
            new Uri("http://127.0.0.1:11434/v1"),
            "Default",
            false));
        var handler = new ManagedRuntimeStatusRequestHandler(settings);

        var status = await InvokeAsync(handler, "GET", "/status", null);
        Assert.Equal(200, status.StatusCode);
        Assert.Equal("ok", Body(status)["status"]!.GetValue<string>());
        Assert.Equal("Ollama", Body(status)["backend"]!.GetValue<string>());

        var models = await InvokeAsync(handler, "GET", "/models?backend=Ollama", null);
        Assert.Equal(200, models.StatusCode);
        Assert.Equal(["qwen3:8b"], Body(models)["models"]!.AsArray().Select(node => node!.GetValue<string>()));
    }

    [Fact]
    public async Task Models_for_another_backend_return_a_bounded_error_without_network()
    {
        var settings = new ManagedRuntimeSettingsService(new RuntimeSettingsSnapshot(
            "Ollama", "qwen3:8b", null, "Default", false));
        var handler = new ManagedRuntimeStatusRequestHandler(settings);

        var response = await InvokeAsync(handler, "GET", "/models?backend=OpenCode%20Zen", null);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("error", Body(response)["status"]!.GetValue<string>());
        Assert.DoesNotContain("api", Body(response).ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ollama")]
    [InlineData("lmstudio")]
    [InlineData("lm studio")]
    [InlineData("custom")]
    [InlineData("opencode")]
    [InlineData("kilo")]
    public async Task Models_accept_the_same_backend_aliases_as_the_compatibility_worker(string alias)
    {
        var expectedBackend = alias switch
        {
            "ollama" => "Ollama",
            "lmstudio" or "lm studio" => "LM Studio",
            "custom" => "Custom OpenAI",
            "opencode" => "OpenCode Zen",
            "kilo" => "Kilo Gateway",
            _ => throw new ArgumentOutOfRangeException(nameof(alias)),
        };
        var settings = new ManagedRuntimeSettingsService(new RuntimeSettingsSnapshot(
            expectedBackend, "active-model", null, "Default", false));
        var handler = new ManagedRuntimeStatusRequestHandler(settings);

        var response = await InvokeAsync(handler, "GET", $"/models?backend={Uri.EscapeDataString(alias)}", null);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("ok", Body(response)["status"]!.GetValue<string>());
        Assert.Equal(["active-model"], Body(response)["models"]!.AsArray().Select(node => node!.GetValue<string>()));
    }

    [Fact]
    public async Task Models_reject_an_unsupported_backend_with_a_stable_message()
    {
        var settings = new ManagedRuntimeSettingsService(new RuntimeSettingsSnapshot(
            "Ollama", "active-model", null, "Default", false));
        var handler = new ManagedRuntimeStatusRequestHandler(settings);

        var response = await InvokeAsync(handler, "GET", "/models?backend=unsupported", null);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("error", Body(response)["status"]!.GetValue<string>());
        Assert.Equal("Unsupported model backend.", Body(response)["message"]!.GetValue<string>());
    }

    private static Task<PrivateRuntimeResponse> InvokeAsync(
        ManagedRuntimeStatusRequestHandler handler,
        string method,
        string path,
        string? body) => handler.InvokeAsync(
            new PrivateRuntimeRequest(
                method,
                path,
                body is null ? null : "application/json",
                body is null ? ReadOnlyMemory<byte>.Empty : System.Text.Encoding.UTF8.GetBytes(body)),
            CancellationToken.None);

    private static JsonObject Body(PrivateRuntimeResponse response) =>
        JsonNode.Parse(System.Text.Encoding.UTF8.GetString(response.Body.Span))!.AsObject();
}
