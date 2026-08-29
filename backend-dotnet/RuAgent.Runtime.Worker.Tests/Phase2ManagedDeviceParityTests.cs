using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Protocol.Harness;
using RuAgent.Runtime.Adapters.Audio;
using RuAgent.Runtime.Adapters.Devices;
using RuAgent.Runtime.Adapters.Observation;
using RuAgent.Runtime.Adapters.Runtime;
using RuAgent.Runtime.Adapters.Speech;
using RuAgent.Runtime.Core.Contracts;
using RuAgent.Runtime.Core.Lifecycle;
using Xunit;

namespace RuAgent.Runtime.Worker.Tests;

/// <summary>
/// Deterministic Phase 2 parity evidence. The fixture records only contract
/// fields: no model, network, microphone, OmniVoice, VTube Studio, or Gemini
/// process participates in these tests.
/// </summary>
public sealed class Phase2ManagedDeviceParityTests
{
    [Fact]
    public void Fixture_covers_the_managed_device_contract_groups()
    {
        var fixture = LoadFixture();

        Assert.Equal(1, fixture["schema_version"]!.GetValue<int>());
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "protocol", "settings", "voice", "errors", "speech",
                "vts", "observation", "cancellation", "privacy",
            },
            ((JsonArray)fixture["cases"]!).Select(item => item!["group"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Runtime_settings_match_the_python_reference_shape()
    {
        var expected = Case("settings.load")["expected"]!;
        var service = new ManagedRuntimeSettingsService(new RuntimeSettingsSnapshot(
            "OpenCode Zen", "opencode/free", null, "Default", false));
        var input = Case("settings.load")["input"]!;

        await service.LoadAsync(
            new RuntimeSettingsInput(
                input["backend"]!.GetValue<string>(),
                input["model_name"]!.GetValue<string>(),
                input["base_url"]?.GetValue<string>(),
                input["api_variant"]!.GetValue<string>(),
                input["kilo_reasoning_enabled"]!.GetValue<bool>()),
            CancellationToken.None);

        Assert.Equal(expected["backend"]!.GetValue<string>(), service.Current.Backend);
        Assert.Equal(expected["model_name"]!.GetValue<string>(), service.Current.ModelName);
        Assert.Null(service.Current.BaseUrl);
        Assert.Equal(expected["api_variant"]!.GetValue<string>(), service.Current.ApiVariant);
        Assert.Equal(expected["kilo_reasoning_enabled"]!.GetValue<bool>(), service.Current.KiloReasoningEnabled);
    }

    [Fact]
    public async Task Voice_control_types_sequences_and_binary_order_match_fixture()
    {
        var expected = Case("voice.order")["expected"]!;
        await using var leases = new RuntimeLeaseCoordinator();
        var voice = new VoiceStreamCoordinator(new FakeVoiceProvider(), leases);
        var router = new ManagedWorkerV1RequestRouter(
            new ManagedRuntimeSettingsService(new RuntimeSettingsSnapshot(
                "OpenCode Zen", "opencode/free", null, "Default", false)),
            new PrivateRuntimeRequestRouter(new StubPrivateHandler()),
            voice);

        var replies = await CollectAsync(router.RouteAsync(
            WorkerV1Contracts.VoiceStart,
            new JsonObject
            {
                ["request_id"] = "phase2-voice",
                ["text"] = "Chào anh.",
                ["voice_label"] = "Ru (OmniVoice)",
                ["speed"] = expected["speed"]!.GetValue<double>(),
                ["voice_style_id"] = expected["voice_style_id"]!.GetValue<string>(),
            },
            CancellationToken.None));

        Assert.Equal(
            ((JsonArray)expected["types"]!).Select(item => item!.GetValue<string>()),
            replies.Select(reply => reply.Type));
        Assert.Equal(
            ((JsonArray)expected["event_types"]!).Select(item => item!.GetValue<string>()),
            replies.Select(reply => reply.Payload["type"]!.GetValue<string>()));
        Assert.Equal(
            ((JsonArray)expected["states"]!).Select(item => item!.GetValue<string>()),
            replies.Select(reply => reply.Payload["state"]?.GetValue<string>()
                ?? reply.Payload["type"]!.GetValue<string>()));
        Assert.Equal(
            ((JsonArray)expected["sequences"]!).Select(item => ReadLong(item!)),
            replies.Select(reply => ReadLong(reply.Payload["sequence"]!)));
        Assert.Equal(expected["audio_chunks"]!.GetValue<int>(), replies.Count(reply => reply.Binary.Length > 0));
        Assert.All(replies.Where(reply => reply.Binary.Length > 0), reply => Assert.True(reply.Payload["state"]!.GetValue<string>() == "audio_ready"));
    }

    [Fact]
    public async Task Invalid_voice_style_maps_to_the_stable_non_retryable_error()
    {
        var expected = Case("errors.invalid_voice_style")["expected"]!;
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
                ["request_id"] = "phase2-invalid-style",
                ["text"] = "Chào anh.",
                ["voice_style_id"] = "not-supported",
            },
            CancellationToken.None));

        var error = Assert.Single(replies);
        Assert.Equal(WorkerV1Contracts.Error, error.Type);
        Assert.Equal(expected["code"]!.GetValue<string>(), error.Payload["code"]!.GetValue<string>());
        Assert.Equal(expected["retryable"]!.GetValue<bool>(), error.Payload["retryable"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Speech_transcript_normalization_matches_fixture()
    {
        var expected = Case("speech.normalization")["expected"]!;
        var transport = new FakeSpeechTransport
        {
            Transcript = Case("speech.normalization")["input"]!["transcript"]!.GetValue<string>(),
        };
        var recognizer = new GoogleSpeechRecognizer(transport);

        var text = await recognizer.RecognizeAsync(
            new SpeechRequest(ValidWav(16_000, 1, new byte[] { 0, 0 }), 16_000, 1, "vi-VN"),
            CancellationToken.None);

        Assert.Equal(expected["text"]!.GetValue<string>(), text);
        Assert.Equal("vi-VN", transport.Language);
    }

    [Fact]
    public void Vts_code_sets_and_action_limit_match_fixture()
    {
        var expected = Case("vts.code_sets")["expected"]!;
        var catalog = new VtsCatalog(
            "phase2-model",
            "Ru model",
            ((JsonArray)expected["hotkey_codes"]!).Select(item => item!.GetValue<string>()),
            ((JsonArray)expected["expression_codes"]!).Select(item => item!.GetValue<string>()));

        Assert.Equal(
            ((JsonArray)expected["hotkey_codes"]!).Select(item => item!.GetValue<string>()),
            catalog.HotkeyCodes.OrderBy(code => code, StringComparer.Ordinal));
        Assert.Equal(
            ((JsonArray)expected["expression_codes"]!).Select(item => item!.GetValue<string>()),
            catalog.ExpressionCodes.OrderBy(code => code, StringComparer.Ordinal));
        Assert.Equal(
            ((JsonArray)expected["valid_action_codes"]!).Select(item => item!.GetValue<string>()),
            catalog.ValidateActionCodes(["VTS_01", "expr_01"]));
        Assert.Equal(RuntimeLimits.MaxVtsActionCodes, expected["max_action_codes"]!.GetValue<int>());
    }

    [Fact]
    public void Observation_bounds_and_redaction_match_fixture()
    {
        var expected = Case("observation.bounds")["expected"]!;
        var privateValue = "PRIVATE-OBSERVATION-KEY";
        var observationJson = new JsonObject
        {
            ["scene"] = $"api_key: {privateValue}",
            ["event"] = "Working",
            ["confidence"] = 9.0,
            ["novelty"] = -1.0,
            ["comment_worthiness"] = 0.5,
            ["visible_text"] = new JsonArray(Enumerable.Range(0, 10).Select(index => (JsonNode?)$"line-{index}").ToArray()),
        };
        var response = new JsonObject
        {
            ["candidates"] = new JsonArray(
                new JsonObject
                {
                    ["content"] = new JsonObject
                    {
                        ["parts"] = new JsonArray(
                            new JsonObject { ["text"] = observationJson.ToJsonString() }),
                    },
                }),
        };

        var observation = VisionObservationParser.Parse(response);

        Assert.Equal(expected["confidence"]!.GetValue<double>(), observation.Confidence);
        Assert.Equal(expected["novelty"]!.GetValue<double>(), observation.Novelty);
        Assert.Equal(expected["comment_worthiness"]!.GetValue<double>(), observation.CommentWorthiness);
        Assert.Equal(expected["visible_text_count"]!.GetValue<int>(), observation.VisibleText.Count);
        Assert.True(expected["redacted"]!.GetValue<bool>());
        Assert.DoesNotContain(privateValue, observation.Scene, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_does_not_complete_work_and_releases_the_lease()
    {
        var expected = Case("cancellation.before_dispatch")["expected"]!;
        await using var leases = new RuntimeLeaseCoordinator();
        await using var first = await leases.AcquireAsync(ResourceKind.GpuAudio, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var waiting = leases.AcquireAsync(ResourceKind.GpuAudio, cancellation.Token).AsTask();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(expected["completed"]!.GetValue<bool>());

        await first.DisposeAsync();
        await using var second = await leases.AcquireAsync(ResourceKind.GpuAudio, CancellationToken.None);
        Assert.True(expected["lease_released"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Privacy_scan_excludes_prompt_credentials_provider_body_and_audio()
    {
        var expected = Case("privacy.redaction")["expected"]!;
        var input = Case("privacy.redaction")["input"]!;
        var prompt = input["prompt"]!.GetValue<string>();
        var credential = input["credential"]!.GetValue<string>();

        var redacted = VisionObservationParser.Redact($"api_key: {credential}");
        Assert.Equal(expected["credential_visible"]!.GetValue<bool>(), redacted.Contains(credential, StringComparison.Ordinal));

        var provider = new ThrowingObservationProvider(prompt, credential);
        var response = await new ObservationRuntimeRequestHandler(provider).InvokeAsync(
            new PrivateRuntimeRequest(
                "POST",
                "/observation/analyze",
                "application/json",
                Encoding.UTF8.GetBytes(new JsonObject
                {
                    ["image_b64"] = Convert.ToBase64String([1, 2, 3]),
                    ["prompt"] = "Inspect",
                }.ToJsonString())),
            CancellationToken.None);
        var body = Encoding.UTF8.GetString(response.Body.Span);
        Assert.Equal(expected["prompt_visible"]!.GetValue<bool>(), body.Contains(prompt, StringComparison.Ordinal));
        Assert.Equal(expected["raw_provider_body_visible"]!.GetValue<bool>(), body.Contains(prompt, StringComparison.Ordinal));
        Assert.Equal(expected["raw_audio_visible"]!.GetValue<bool>(), body.Contains(credential, StringComparison.Ordinal));
    }

    private static JsonObject LoadFixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "fixtures", "python-csharp-phase2-device-baseline.json");
            if (File.Exists(candidate))
            {
                return JsonNode.Parse(File.ReadAllText(candidate))!.AsObject();
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("The Phase 2 device baseline fixture was not found above the test output directory.");
    }

    private static JsonObject Case(string id) =>
        ((JsonArray)LoadFixture()["cases"]!).Single(item => item!["id"]!.GetValue<string>() == id)!.AsObject();

    private static long ReadLong(JsonNode node)
    {
        var value = node.AsValue();
        return value.TryGetValue<long>(out var longValue)
            ? longValue
            : value.GetValue<int>();
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

    private static byte[] ValidWav(int sampleRate, short channels, byte[] data)
    {
        var blockAlign = checked((short)(channels * 2));
        var bytes = new byte[44 + data.Length];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);
        "WAVE"u8.CopyTo(bytes.AsSpan(8));
        "fmt "u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22), channels);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), sampleRate * blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32), blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34), 16);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), data.Length);
        data.CopyTo(bytes, 44);
        return bytes;
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
            yield return new VoiceChunk("audio", 42, new byte[] { 1, 2 });
            yield return new VoiceChunk("audio", 43, new byte[] { 3, 4 });
            yield return new VoiceChunk("ended", 44);
        }
    }

    private sealed class FakeSpeechTransport : IGoogleSpeechTransport
    {
        public string? Transcript { get; init; }
        public string? Language { get; private set; }

        public Task<string?> RecognizeAsync(
            ReadOnlyMemory<byte> wavBytes,
            string language,
            CancellationToken cancellationToken)
        {
            Language = language;
            return Task.FromResult(Transcript);
        }
    }

    private sealed class ThrowingObservationProvider(string prompt, string credential) : IObservationProvider
    {
        public Task<VisionObservation?> AnalyzeAsync(ObservationRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"provider body {prompt} {credential}");
    }

    private sealed class StubPrivateHandler : IPrivateRuntimeRequestHandler
    {
        public Task<PrivateRuntimeResponse> InvokeAsync(
            PrivateRuntimeRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PrivateRuntimeResponse(200, "application/json", Encoding.UTF8.GetBytes("{}")));
    }
}
