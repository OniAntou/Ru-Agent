using System.Text.Json;
using RuBackend.Runtime;

namespace RuBackend.Speech;

public static class SttEndpoints
{
    private const int MaxAudioBase64Characters = 7_500_000;
    private const int MaxRequestBodyBytes = 8 * 1024 * 1024;
    private const int DefaultSampleRate = 16_000;
    private const int MinimumSampleRate = 8_000;
    private const int MaximumSampleRate = 192_000;

    public static void Map(WebApplication app)
    {
        app.MapPost("/voice/transcribe", HandleAsync);
    }

    private static async Task HandleAsync(
        HttpContext context,
        IRuntimeWorkerClient workerClient,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var requestBody = await RuntimeEndpointRelay.ReadBoundedBodyAsync(
            context.Request,
            MaxRequestBodyBytes,
            cancellationToken).ConfigureAwait(false);
        if (requestBody is null)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await context.Response.WriteAsJsonAsync(
                new { detail = "Request body too large" },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var validation = Validate(requestBody);
        switch (validation)
        {
            case SttValidation.InvalidRequest:
            case SttValidation.OutOfRangeSampleRate:
                await WriteJsonAsync(
                    context,
                    StatusCodes.Status422UnprocessableEntity,
                    new { detail = "Invalid STT request." },
                    cancellationToken).ConfigureAwait(false);
                return;
            case SttValidation.EmptyAudio:
                await WriteJsonAsync(
                    context,
                    StatusCodes.Status200OK,
                    new { status = "error", message = "audio is required" },
                    cancellationToken).ConfigureAwait(false);
                return;
            case SttValidation.InvalidAudio:
                await WriteJsonAsync(
                    context,
                    StatusCodes.Status200OK,
                    new { status = "error", message = "invalid audio payload" },
                    cancellationToken).ConfigureAwait(false);
                return;
        }

        try
        {
            var response = await workerClient.SendAsync(
                new RuntimeWorkerRequest(
                    HttpMethod.Post,
                    "/voice/transcribe",
                    context.Request.ContentType,
                    requestBody),
                cancellationToken).ConfigureAwait(false);
            await RuntimeEndpointRelay.WriteWorkerResponseAsync(context, response, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            loggerFactory
                .CreateLogger("RuBackend.Stt")
                .LogWarning(
                    "STT runtime worker unavailable (exception_type={ExceptionType}).",
                    exception.GetType().Name);
            await WriteJsonAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                new { status = "error", message = "Speech recognition is temporarily unavailable." },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static SttValidation Validate(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("audio_b64", out var audioElement) ||
                audioElement.ValueKind != JsonValueKind.String)
            {
                return SttValidation.InvalidRequest;
            }

            var audioBase64 = audioElement.GetString() ?? string.Empty;
            if (audioBase64.Length > MaxAudioBase64Characters)
            {
                return SttValidation.InvalidRequest;
            }

            if (root.TryGetProperty("sample_rate", out var rateElement) &&
                (rateElement.ValueKind != JsonValueKind.Number || !rateElement.TryGetInt32(out _)))
            {
                return SttValidation.InvalidRequest;
            }

            var sampleRate = root.TryGetProperty("sample_rate", out rateElement)
                ? rateElement.GetInt32()
                : DefaultSampleRate;
            if (sampleRate < MinimumSampleRate || sampleRate > MaximumSampleRate)
            {
                return SttValidation.OutOfRangeSampleRate;
            }

            if (string.IsNullOrWhiteSpace(audioBase64))
            {
                return SttValidation.EmptyAudio;
            }

            if (audioBase64.Any(char.IsWhiteSpace))
            {
                return SttValidation.InvalidAudio;
            }

            try
            {
                return Convert.FromBase64String(audioBase64).Length == 0
                    ? SttValidation.EmptyAudio
                    : SttValidation.Valid;
            }
            catch (FormatException)
            {
                return SttValidation.InvalidAudio;
            }
        }
        catch (JsonException)
        {
            return SttValidation.InvalidRequest;
        }
    }

    private static async Task WriteJsonAsync<T>(
        HttpContext context,
        int statusCode,
        T payload,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    private enum SttValidation
    {
        Valid,
        InvalidRequest,
        OutOfRangeSampleRate,
        EmptyAudio,
        InvalidAudio,
    }
}
