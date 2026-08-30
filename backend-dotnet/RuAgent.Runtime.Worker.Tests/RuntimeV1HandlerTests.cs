using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Protocol.Harness;
using RuAgent.Runtime.Adapters.Runtime;
using RuAgent.Runtime.Core.Contracts;
using RuAgent.Runtime.Worker;
using Xunit;

namespace RuAgent.Runtime.Worker.Tests;

public sealed class RuntimeV1HandlerTests
{
    [Fact]
    public async Task Runtime_load_returns_success_state_and_updates_settings()
    {
        var settings = new ManagedRuntimeSettingsService(
            new RuntimeSettingsSnapshot("OpenCode Zen", "opencode/free", null, "Default", false));
        var router = new ManagedWorkerV1RequestRouter(settings, new PrivateRuntimeRequestRouter(new StubHandler()));

        var replies = await CollectAsync(router.RouteAsync(
            WorkerV1Contracts.RuntimeLoad,
            new JsonObject
            {
                ["backend"] = "Ollama",
                ["ollama_model"] = "qwen3:8b",
                ["ollama_url"] = "http://127.0.0.1:11434/v1",
                ["api_variant"] = "Default",
                ["kilo_reasoning_enabled"] = false,
                ["voice_label"] = "Ái Vy (OmniVoice)",
                ["voice_speed"] = 1.25,
                ["auto_pace_enabled"] = true,
                ["auto_read_replies"] = false,
                ["show_subtitles"] = true,
                ["subtitle_color"] = "#ABCDEF",
                ["subtitle_size"] = "large",
                ["subtitle_duration"] = 3.5,
                ["subtitle_pos_mode"] = "manual",
                ["subtitle_pos_x"] = 12,
                ["subtitle_pos_y"] = 34,
            },
            CancellationToken.None));

        Assert.Single(replies);
        Assert.Equal(WorkerV1Contracts.RuntimeState, replies[0].Type);
        Assert.Equal("success", replies[0].Payload["status"]!.GetValue<string>());
        Assert.Equal("Ollama", settings.Current.Backend);
        Assert.Equal("Ái Vy (OmniVoice)", settings.Current.Voice.VoiceLabel);
        Assert.Equal(1.25, settings.Current.Voice.VoiceSpeed);
        Assert.True(settings.Current.Voice.AutoPaceEnabled);
        Assert.False(settings.Current.Voice.AutoReadReplies);
        Assert.True(settings.Current.Voice.ShowSubtitles);
        Assert.Equal(12, settings.Current.Voice.SubtitlePosX);
        Assert.Equal(34, settings.Current.Voice.SubtitlePosY);
    }

    [Fact]
    public async Task Runtime_request_returns_text_response_without_binary_frame()
    {
        var settings = new ManagedRuntimeSettingsService(
            new RuntimeSettingsSnapshot("OpenCode Zen", "opencode/free", null, "Default", false));
        var router = new ManagedWorkerV1RequestRouter(
            settings,
            new PrivateRuntimeRequestRouter(new StubHandler()));

        var replies = await CollectAsync(router.RouteAsync(
            WorkerV1Contracts.RuntimeRequest,
            new JsonObject
            {
                ["method"] = "GET",
                ["path_and_query"] = "/status",
                ["body"] = "",
            },
            CancellationToken.None));

        Assert.Single(replies);
        Assert.Equal(WorkerV1Contracts.RuntimeResponse, replies[0].Type);
        Assert.False(replies[0].Payload.ContainsKey("binary"));
        Assert.Equal("{\"status\":\"ok\"}", replies[0].Payload["body"]!.GetValue<string>());
        Assert.True(replies[0].Binary.IsEmpty);
    }

    [Fact]
    public async Task Runtime_request_returns_explicit_binary_announcement()
    {
        var settings = new ManagedRuntimeSettingsService(
            new RuntimeSettingsSnapshot("OpenCode Zen", "opencode/free", null, "Default", false));
        var router = new ManagedWorkerV1RequestRouter(
            settings,
            new PrivateRuntimeRequestRouter(new StubHandler
            {
                Response = new PrivateRuntimeResponse(200, "audio/wav", new byte[] { 1, 2 }, binary: true),
            }));

        var replies = await CollectAsync(router.RouteAsync(
            WorkerV1Contracts.RuntimeRequest,
            new JsonObject
            {
                ["method"] = "POST",
                ["path_and_query"] = "/voice/tts",
                ["content_type"] = "application/json",
                ["body"] = "{}",
            },
            CancellationToken.None));

        Assert.Single(replies);
        Assert.True(replies[0].Payload["binary"]!.GetValue<bool>());
        Assert.Equal(2, replies[0].Binary.Length);
    }

    [Fact]
    public async Task Runtime_load_rejects_secret_like_payload_keys()
    {
        var settings = new ManagedRuntimeSettingsService(
            new RuntimeSettingsSnapshot("OpenCode Zen", "opencode/free", null, "Default", false));
        var router = new ManagedWorkerV1RequestRouter(settings, new PrivateRuntimeRequestRouter(new StubHandler()));

        var replies = await CollectAsync(router.RouteAsync(
            WorkerV1Contracts.RuntimeLoad,
            new JsonObject
            {
                ["backend"] = "Ollama",
                ["ollama_model"] = "qwen3:8b",
                ["api_key"] = "must-not-cross-boundary",
            },
            CancellationToken.None));

        Assert.Single(replies);
        Assert.Equal(WorkerV1Contracts.Error, replies[0].Type);
        Assert.Equal("invalid_request", replies[0].Payload["code"]!.GetValue<string>());
        Assert.Equal("OpenCode Zen", settings.Current.Backend);
    }

    private static async Task<List<WorkerV1Reply>> CollectAsync(IAsyncEnumerable<WorkerV1Reply> replies)
    {
        var result = new List<WorkerV1Reply>();
        await foreach (var reply in replies)
        {
            result.Add(reply);
        }

        return result;
    }

    private sealed class StubHandler : IPrivateRuntimeRequestHandler
    {
        public PrivateRuntimeResponse Response { get; init; } = new(
            200,
            "application/json",
            Encoding.UTF8.GetBytes("{\"status\":\"ok\"}"),
            binary: false);

        public Task<PrivateRuntimeResponse> InvokeAsync(
            PrivateRuntimeRequest request,
            CancellationToken cancellationToken) => Task.FromResult(Response);
    }
}
