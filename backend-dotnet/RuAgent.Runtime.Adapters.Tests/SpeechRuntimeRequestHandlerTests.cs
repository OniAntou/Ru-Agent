using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Runtime.Adapters.Runtime;
using RuAgent.Runtime.Adapters.Speech;
using RuAgent.Runtime.Core.Contracts;
using Xunit;

namespace RuAgent.Runtime.Adapters.Tests;

public sealed class SpeechRuntimeRequestHandlerTests
{
    [Fact]
    public async Task Oversized_private_body_is_rejected_before_json_parse_or_provider_call()
    {
        var recognizer = new RecordingRecognizer();
        var handler = new SpeechRuntimeRequestHandler(recognizer);
        var payload = new JsonObject
        {
            ["audio_b64"] = Convert.ToBase64String(ValidWav()),
            ["padding"] = new string('x', 8 * 1024 * 1024),
        };

        var response = await handler.InvokeAsync(
            new PrivateRuntimeRequest(
                "POST",
                "/voice/transcribe",
                "application/json",
                Encoding.UTF8.GetBytes(payload.ToJsonString())),
            CancellationToken.None);

        Assert.Equal(422, response.StatusCode);
        Assert.False(recognizer.Called);
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

    private sealed class RecordingRecognizer : ISpeechRecognizer
    {
        public bool Called { get; private set; }

        public Task<string> RecognizeAsync(SpeechRequest request, CancellationToken cancellationToken)
        {
            Called = true;
            return Task.FromResult("unexpected");
        }
    }
}
