using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Runtime.Adapters.Observation;
using RuAgent.Runtime.Core.Contracts;
using RuAgent.Runtime.Core.Lifecycle;
using Xunit;

namespace RuAgent.Runtime.Adapters.Tests;

public sealed class GeminiVisionProviderTests
{
    [Fact]
    public async Task Provider_falls_back_across_models_and_keeps_key_out_of_payload()
    {
        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("provider secret body", Encoding.UTF8, "text/plain"),
            },
            JsonResponse("gemini-3.1-flash-lite", 0.91));
        var provider = new GeminiVisionProvider(
            new HttpClient(handler),
            new GeminiVisionOptions("secret-key")
            {
                Models = ["gemini-3.1-flash-lite", "gemini-3.5-flash"],
            });

        var observation = await provider.AnalyzeAsync(
            new ObservationRequest(new byte[] { 0xFF, 0xD8, 0xFF }, "Inspect this screen", "gemini-3.1-flash-lite"),
            CancellationToken.None);

        Assert.Equal("Editor", observation!.Scene);
        Assert.Equal(2, handler.Requests.Count);
        Assert.True(handler.Requests[0].Headers.Contains("x-goog-api-key"));
        Assert.DoesNotContain("secret-key", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-3.1-flash-lite:generateContent",
            handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task Provider_uses_temperature_only_for_models_that_support_it()
    {
        var response = JsonResponse("gemini-3.7-flash", 0.8);
        var handler = new RecordingHandler(response);
        var provider = new GeminiVisionProvider(
            new HttpClient(handler),
            new GeminiVisionOptions("secret-key"));

        _ = await provider.AnalyzeAsync(
            new ObservationRequest(new byte[] { 1 }, "Inspect", "gemini-3.7-flash"),
            CancellationToken.None);

        var payload = JsonNode.Parse(handler.RequestBodies[0])!.AsObject();
        Assert.Null(payload["generationConfig"]!["temperature"]);
    }

    [Fact]
    public async Task Parser_accepts_fenced_json_redacts_sensitive_text_and_clamps_numbers()
    {
        var response = JsonResponseText(
            "gemini-3.1-flash-lite",
            "```json\n{\"scene\":\"mail me at user@example.com\",\"event\":\"api_key: super-secret\",\"reaction\":\"ok\",\"confidence\":2,\"novelty\":-1,\"comment_worthiness\":0.7,\"visible_text\":[\"password=hunter2\",\"tab\"]}\n```\n");
        var provider = new GeminiVisionProvider(
            new HttpClient(new RecordingHandler(response)),
            new GeminiVisionOptions("secret-key"));

        var observation = await provider.AnalyzeAsync(
            new ObservationRequest(new byte[] { 1 }, "Inspect", "gemini-3.1-flash-lite"),
            CancellationToken.None);

        Assert.DoesNotContain("user@example.com", observation!.Scene, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", observation.Event, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", observation.VisibleText[0], StringComparison.Ordinal);
        Assert.Equal(1.0, observation.Confidence);
        Assert.Equal(0.0, observation.Novelty);
        Assert.Equal(0.7, observation.CommentWorthiness);
    }

    [Fact]
    public async Task Provider_maps_missing_key_invalid_candidates_and_http_failures_to_bounded_results()
    {
        var missing = new GeminiVisionProvider(
            new HttpClient(new RecordingHandler(JsonResponse("gemini-3.1-flash-lite", 0.8))),
            new GeminiVisionOptions(" "));
        var skipped = await Assert.ThrowsAsync<ObservationProviderException>(() => missing.AnalyzeAsync(
            new ObservationRequest(new byte[] { 1 }, "Inspect", "gemini-3.1-flash-lite"),
            CancellationToken.None));
        Assert.Equal("observation_skipped", skipped.Code);

        var malformed = new GeminiVisionProvider(
            new HttpClient(new RecordingHandler(JsonResponseText("gemini-3.1-flash-lite", "not-json"))),
            new GeminiVisionOptions("secret-key")
            {
                Models = ["gemini-3.1-flash-lite"],
            });
        var failed = await Assert.ThrowsAsync<ObservationProviderException>(() => malformed.AnalyzeAsync(
            new ObservationRequest(new byte[] { 1 }, "Inspect", "gemini-3.1-flash-lite"),
            CancellationToken.None));
        Assert.Equal("observation_failed", failed.Code);
        Assert.DoesNotContain("provider secret body", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_rejects_oversized_image_before_network_call()
    {
        var handler = new RecordingHandler(JsonResponse("gemini-3.1-flash-lite", 0.8));
        var provider = new GeminiVisionProvider(new HttpClient(handler), new GeminiVisionOptions("key"));
        var image = new byte[RuntimeLimits.MaxObservationImageBytes + 1];

        await Assert.ThrowsAsync<ArgumentException>(() => provider.AnalyzeAsync(
            new ObservationRequest(image, "Inspect", "gemini-3.1-flash-lite"),
            CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Provider_skips_without_network_when_observation_lease_is_busy()
    {
        await using var leases = new RuntimeLeaseCoordinator();
        await using var held = await leases.AcquireAsync(ResourceKind.Observation, CancellationToken.None);
        var handler = new RecordingHandler(JsonResponse("gemini-3.1-flash-lite", 0.8));
        var provider = new GeminiVisionProvider(
            new HttpClient(handler),
            new GeminiVisionOptions("key"),
            leases);

        var skipped = await Assert.ThrowsAsync<ObservationProviderException>(() => provider.AnalyzeAsync(
            new ObservationRequest(new byte[] { 1 }, "Inspect", "gemini-3.1-flash-lite"),
            CancellationToken.None));

        Assert.Equal("observation_skipped", skipped.Code);
        Assert.Equal("generation_busy", skipped.Reason);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Provider_does_not_expose_http_error_body()
    {
        var provider = new GeminiVisionProvider(
            new HttpClient(new RecordingHandler(
                new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("provider secret body", Encoding.UTF8, "text/plain"),
                })),
            new GeminiVisionOptions("key")
            {
                Models = ["gemini-3.1-flash-lite"],
            });

        var failed = await Assert.ThrowsAsync<ObservationProviderException>(() => provider.AnalyzeAsync(
            new ObservationRequest(new byte[] { 1 }, "Inspect", "gemini-3.1-flash-lite"),
            CancellationToken.None));

        Assert.Equal("observation_failed", failed.Code);
        Assert.DoesNotContain("provider secret body", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parser_redacts_urls_provider_tokens_and_private_markers()
    {
        var redacted = VisionObservationParser.Redact(
            "https://example.test/private?token=abc sk_live_secret eyJaaa.bbb.ccc [[VTS:Wave]] [SEARCH:cats]");

        Assert.DoesNotContain("https://example.test", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("sk_live_secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJaaa.bbb.ccc", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("[[VTS:Wave]]", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("[SEARCH:cats]", redacted, StringComparison.Ordinal);
    }

    private static HttpResponseMessage JsonResponse(string model, double confidence) => JsonResponseText(
        model,
        new JsonObject
        {
            ["scene"] = "Editor",
            ["event"] = model,
            ["confidence"] = confidence,
            ["novelty"] = 0.5,
            ["comment_worthiness"] = 0.5,
            ["visible_text"] = new JsonArray("code"),
        }.ToJsonString());

    private static HttpResponseMessage JsonResponseText(string model, string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            new JsonObject
            {
                ["candidates"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["content"] = new JsonObject
                        {
                            ["parts"] = new JsonArray(new JsonObject { ["text"] = text }),
                        },
                    },
                },
            }.ToJsonString(),
            Encoding.UTF8,
            "application/json"),
    };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public RecordingHandler(params HttpResponseMessage[] responses) => _responses = new Queue<HttpResponseMessage>(responses);
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return _responses.Dequeue();
        }
    }
}
