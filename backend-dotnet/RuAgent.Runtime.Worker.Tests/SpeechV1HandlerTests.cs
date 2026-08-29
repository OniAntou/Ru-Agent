using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Protocol.Harness;
using RuAgent.Runtime.Adapters.Runtime;
using RuAgent.Runtime.Adapters.Speech;
using RuAgent.Runtime.Core.Contracts;
using RuAgent.Runtime.Worker;
using Xunit;

namespace RuAgent.Runtime.Worker.Tests;

public sealed class SpeechV1HandlerTests
{
    [Fact]
    public async Task Runtime_request_transcribe_returns_normalized_text_without_audio()
    {
        var router = Router(new FakeRecognizer { Transcript = "  xin   chào  " });
        var body = new JsonObject
        {
            ["audio_b64"] = Convert.ToBase64String(ValidWav()),
            ["sample_rate"] = 16_000,
        }.ToJsonString();

        var replies = await CollectAsync(router.RouteAsync(
            WorkerV1Contracts.RuntimeRequest,
            RuntimeRequestPayload(body),
            CancellationToken.None));

        Assert.Single(replies);
        Assert.Equal(WorkerV1Contracts.RuntimeResponse, replies[0].Type);
        Assert.Equal(200, replies[0].Payload["status_code"]!.GetValue<int>());
        var response = JsonNode.Parse(replies[0].Payload["body"]!.GetValue<string>())!.AsObject();
        Assert.Equal("ok", response["status"]!.GetValue<string>());
        Assert.Equal("xin chào", response["text"]!.GetValue<string>());
        Assert.DoesNotContain("audio_b64", replies[0].Payload.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runtime_request_transcribe_rejects_invalid_wav_and_provider_details()
    {
        var router = Router(new FakeRecognizer { Failure = new SpeechProviderException(
            "speech_failed",
            retryable: true,
            new InvalidOperationException("provider secret body")) });
        var invalidBody = new JsonObject
        {
            ["audio_b64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("not-wav")),
        }.ToJsonString();

        var invalidReplies = await CollectAsync(router.RouteAsync(
            WorkerV1Contracts.RuntimeRequest,
            RuntimeRequestPayload(invalidBody),
            CancellationToken.None));
        Assert.Single(invalidReplies);
        Assert.Equal(WorkerV1Contracts.RuntimeResponse, invalidReplies[0].Type);
        Assert.Equal(422, invalidReplies[0].Payload["status_code"]!.GetValue<int>());

        var validBody = new JsonObject
        {
            ["audio_b64"] = Convert.ToBase64String(ValidWav()),
        }.ToJsonString();
        var providerReplies = await CollectAsync(router.RouteAsync(
            WorkerV1Contracts.RuntimeRequest,
            RuntimeRequestPayload(validBody),
            CancellationToken.None));
        Assert.Single(providerReplies);
        Assert.Equal(WorkerV1Contracts.RuntimeResponse, providerReplies[0].Type);
        Assert.DoesNotContain("provider secret body", providerReplies[0].Payload.ToJsonString(), StringComparison.Ordinal);
    }

    private static ManagedWorkerV1RequestRouter Router(ISpeechRecognizer recognizer) =>
        new(
            new ManagedRuntimeSettingsService(new RuntimeSettingsSnapshot(
                "OpenCode Zen", "opencode/free", null, "Default", false)),
            new PrivateRuntimeRequestRouter(new StubPrivateHandler()),
            speech: recognizer);

    private static JsonObject RuntimeRequestPayload(string body) => new()
    {
        ["method"] = "POST",
        ["path_and_query"] = "/voice/transcribe",
        ["content_type"] = "application/json",
        ["body"] = body,
    };

    private static async Task<List<WorkerV1Reply>> CollectAsync(IAsyncEnumerable<WorkerV1Reply> replies)
    {
        var result = new List<WorkerV1Reply>();
        await foreach (var reply in replies)
        {
            result.Add(reply);
        }

        return result;
    }

    private static byte[] ValidWav()
    {
        var data = new byte[46];
        "RIFF"u8.CopyTo(data);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), data.Length - 8);
        "WAVEfmt "u8.CopyTo(data.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24), 16_000);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28), 32_000);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(34), 16);
        "data"u8.CopyTo(data.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(40), 2);
        return data;
    }

    private sealed class FakeRecognizer : ISpeechRecognizer
    {
        public string? Transcript { get; init; }
        public SpeechProviderException? Failure { get; init; }

        public Task<string> RecognizeAsync(SpeechRequest request, CancellationToken cancellationToken)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            return Task.FromResult(Transcript ?? string.Empty);
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
