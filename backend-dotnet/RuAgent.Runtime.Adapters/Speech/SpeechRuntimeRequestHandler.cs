using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Runtime.Adapters.Runtime;
using RuAgent.Runtime.Core.Contracts;

namespace RuAgent.Runtime.Adapters.Speech;

public sealed class SpeechRuntimeRequestHandler : IPrivateRuntimeRequestHandler
{
    private const int MaxAudioBase64Chars = 7_500_000;
    // Keep the private handler bounded even when it is invoked directly by a
    // worker/router instead of through the public STT endpoint's body limiter.
    private const int MaxRequestBodyBytes = 8 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ISpeechRecognizer _recognizer;

    public SpeechRuntimeRequestHandler(ISpeechRecognizer recognizer)
    {
        _recognizer = recognizer ?? throw new ArgumentNullException(nameof(recognizer));
    }

    public async Task<PrivateRuntimeResponse> InvokeAsync(
        PrivateRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = request.PathAndQuery.Split('?', 2)[0];
        if (request.Method != "POST" || path != "/voice/transcribe")
        {
            throw new PrivateRuntimeRequestException("unsupported_route");
        }

        if (request.Body.Length > MaxRequestBodyBytes)
        {
            return InvalidRequest();
        }

        JsonObject payload;
        try
        {
            payload = JsonNode.Parse(StrictUtf8.GetString(request.Body.Span)) as JsonObject
                ?? throw new FormatException();
        }
        catch (Exception error) when (error is DecoderFallbackException or JsonException or FormatException)
        {
            return InvalidRequest();
        }

        if (!TryReadAudio(payload, out var wavBytes, out var sampleRate))
        {
            return InvalidRequest();
        }

        SpeechRequest speech;
        try
        {
            WavAudioValidator.Validate(wavBytes.Span, 16_000, 1);
            speech = new SpeechRequest(wavBytes, sampleRate, 1, "vi-VN");
        }
        catch (SpeechProviderException error) when (error.Code == "invalid_audio")
        {
            return InvalidRequest();
        }
        catch (ArgumentException)
        {
            return InvalidRequest();
        }

        try
        {
            var text = await _recognizer.RecognizeAsync(speech, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                return JsonResponse(
                    200,
                    new JsonObject
                    {
                        ["status"] = "error",
                        ["message"] = "Could not understand audio.",
                    });
            }

            return JsonResponse(
                200,
                new JsonObject
                {
                    ["status"] = "ok",
                    ["text"] = Normalize(text),
                });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SpeechProviderException error)
        {
            if (error.Code == "invalid_audio")
            {
                return InvalidRequest();
            }

            return JsonResponse(
                200,
                new JsonObject
                {
                    ["status"] = "error",
                    ["message"] = error.Code == "speech_no_match"
                        ? "Could not understand audio."
                        : error.Code == "speech_timeout"
                            ? "Speech recognition timed out."
                            : "Speech recognition failed.",
                });
        }
        catch
        {
            return JsonResponse(
                200,
                new JsonObject
                {
                    ["status"] = "error",
                    ["message"] = "Speech recognition failed.",
                });
        }
    }

    private static bool TryReadAudio(
        JsonObject payload,
        out ReadOnlyMemory<byte> wavBytes,
        out int sampleRate)
    {
        wavBytes = ReadOnlyMemory<byte>.Empty;
        sampleRate = 16_000;
        if (!payload.TryGetPropertyValue("audio_b64", out var audioNode)
            || audioNode is not JsonValue audioValue
            || !audioValue.TryGetValue<string>(out var audio)
            || string.IsNullOrWhiteSpace(audio)
            || audio.Length > MaxAudioBase64Chars
            || audio.Any(char.IsWhiteSpace))
        {
            return false;
        }

        if (payload.TryGetPropertyValue("sample_rate", out var rateNode)
            && rateNode is not null
            && (rateNode is not JsonValue rateValue || !rateValue.TryGetValue<int>(out sampleRate)))
        {
            return false;
        }

        if (sampleRate != 16_000)
        {
            return false;
        }

        try
        {
            var decoded = Convert.FromBase64String(audio);
            if (decoded.Length == 0 || decoded.Length > RuntimeLimits.MaxSpeechAudioBytes)
            {
                return false;
            }

            wavBytes = decoded;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Normalize(string text) => string.Join(
        " ",
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static PrivateRuntimeResponse InvalidRequest() => JsonResponse(
        422,
        new JsonObject { ["detail"] = "Invalid runtime request." });

    private static PrivateRuntimeResponse JsonResponse(int statusCode, JsonObject payload) =>
        new(
            statusCode,
            "application/json",
            StrictUtf8.GetBytes(payload.ToJsonString()));
}
