using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Protocol.Harness;
using RuAgent.Runtime.Adapters.Audio;
using RuAgent.Runtime.Adapters.Runtime;
using RuAgent.Runtime.Core.Contracts;
using RuAgent.Runtime.Core.Lifecycle;
using RuAgent.Runtime.Worker;
using Xunit;

namespace RuAgent.Runtime.Worker.Tests;

public sealed class VoiceV1HandlerTests
{
    [Fact]
    public async Task Voice_start_emits_started_audio_and_done_in_order()
    {
        await using var leases = new RuntimeLeaseCoordinator();
        var voice = new VoiceStreamCoordinator(
            new FakeVoiceProvider(),
            leases);
        var router = new ManagedWorkerV1RequestRouter(
            new ManagedRuntimeSettingsService(new RuntimeSettingsSnapshot(
                "OpenCode Zen", "opencode/free", null, "Default", false)),
            new PrivateRuntimeRequestRouter(new StubPrivateHandler()),
            voice);

        var replies = new List<WorkerV1Reply>();
        await foreach (var reply in router.RouteAsync(
            WorkerV1Contracts.VoiceStart,
            new JsonObject
            {
                ["request_id"] = "voice-1",
                ["text"] = "Chào anh.",
                ["voice_label"] = "Ru (OmniVoice)",
                ["speed"] = 1.0,
                ["voice_style_id"] = "original",
            },
            CancellationToken.None))
        {
            replies.Add(reply);
        }

        Assert.Equal(
            [WorkerV1Contracts.VoiceControl, WorkerV1Contracts.VoiceControl, WorkerV1Contracts.VoiceDone],
            replies.Select(reply => reply.Type));
        Assert.Equal("started", replies[0].Payload["type"]!.GetValue<string>());
        Assert.True(replies[1].Payload["binary"]?.GetValue<bool>() ?? true);
        Assert.Equal(new byte[] { 1, 2 }, replies[1].Binary.ToArray());
        Assert.Equal("done", replies[2].Payload["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task Voice_start_returns_bounded_error_for_invalid_style()
    {
        await using var leases = new RuntimeLeaseCoordinator();
        var router = new ManagedWorkerV1RequestRouter(
            new ManagedRuntimeSettingsService(new RuntimeSettingsSnapshot(
                "OpenCode Zen", "opencode/free", null, "Default", false)),
            new PrivateRuntimeRequestRouter(new StubPrivateHandler()),
            new VoiceStreamCoordinator(new FakeVoiceProvider(), leases));

        var replies = await CollectAsync(router.RouteAsync(
            WorkerV1Contracts.VoiceStart,
            new JsonObject
            {
                ["request_id"] = "voice-2",
                ["text"] = "Chào anh.",
                ["voice_style_id"] = "pitch-shift",
            },
            CancellationToken.None));

        Assert.Single(replies);
        Assert.Equal(WorkerV1Contracts.Error, replies[0].Type);
        Assert.Equal("invalid_request", replies[0].Payload["code"]!.GetValue<string>());
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

    private sealed class FakeVoiceProvider : IVoiceProvider
    {
        public Task<VoiceAudio> SynthesizeAsync(VoiceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new VoiceAudio(new byte[] { 1, 2 }, 24_000, 1));

        public async IAsyncEnumerable<VoiceChunk> StreamAsync(
            VoiceRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new VoiceChunk("audio", 0, new byte[] { 1, 2 });
            yield return new VoiceChunk("ended", 1);
        }
    }

    private sealed class StubPrivateHandler : IPrivateRuntimeRequestHandler
    {
        public Task<PrivateRuntimeResponse> InvokeAsync(
            PrivateRuntimeRequest request,
            CancellationToken cancellationToken) => Task.FromResult(new PrivateRuntimeResponse(
                200,
                "application/json",
                Encoding.UTF8.GetBytes("{}")));
    }
}
