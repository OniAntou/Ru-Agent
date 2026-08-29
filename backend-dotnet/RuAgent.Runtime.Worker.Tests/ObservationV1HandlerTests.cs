using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Protocol.Harness;
using RuAgent.Runtime.Adapters.Observation;
using RuAgent.Runtime.Adapters.Runtime;
using RuAgent.Runtime.Core.Contracts;
using RuAgent.Runtime.Worker;
using Xunit;

namespace RuAgent.Runtime.Worker.Tests;

public sealed class ObservationV1HandlerTests
{
    [Fact]
    public async Task Runtime_observation_analyze_returns_bounded_observation()
    {
        var router = Router(new FakeObservationProvider());
        var body = new JsonObject
        {
            ["image_b64"] = Convert.ToBase64String(new byte[] { 1, 2, 3 }),
            ["prompt"] = "Inspect",
            ["model_name"] = "gemini-3.1-flash-lite",
        }.ToJsonString();

        var replies = await CollectAsync(router.RouteAsync(
            WorkerV1Contracts.RuntimeRequest,
            RequestPayload(body),
            CancellationToken.None));

        Assert.Single(replies);
        Assert.Equal(WorkerV1Contracts.RuntimeResponse, replies[0].Type);
        var response = JsonNode.Parse(replies[0].Payload["body"]!.GetValue<string>())!.AsObject();
        Assert.Equal("ok", response["status"]!.GetValue<string>());
        Assert.Equal("Desktop", response["scene"]!.GetValue<string>());
    }

    [Fact]
    public async Task Runtime_observation_analyze_maps_missing_provider_and_invalid_image_safely()
    {
        var unavailable = Router(new FakeObservationProvider
        {
            Failure = new ObservationProviderException(
                "observation_skipped",
                retryable: false,
                reason: "generation_busy"),
        });
        var body = new JsonObject
        {
            ["image_b64"] = Convert.ToBase64String(new byte[] { 1 }),
            ["prompt"] = "Inspect",
            ["model_name"] = "gemini-3.1-flash-lite",
        }.ToJsonString();
        var skipped = await CollectAsync(unavailable.RouteAsync(
            WorkerV1Contracts.RuntimeRequest,
            RequestPayload(body),
            CancellationToken.None));
        var skippedPayload = JsonNode.Parse(skipped.Single().Payload["body"]!.GetValue<string>())!.AsObject();
        Assert.Equal("skipped", skippedPayload["status"]!.GetValue<string>());
        Assert.Equal("generation_busy", skippedPayload["reason"]!.GetValue<string>());

        var invalid = await CollectAsync(unavailable.RouteAsync(
            WorkerV1Contracts.RuntimeRequest,
            RequestPayload(new JsonObject { ["image_b64"] = "not-base64", ["prompt"] = "Inspect" }.ToJsonString()),
            CancellationToken.None));
        Assert.Equal(422, invalid.Single().Payload["status_code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Runtime_observation_analyze_rejects_prompt_over_byte_limit_before_provider()
    {
        var provider = new FakeObservationProvider();
        var router = Router(provider);
        var body = new JsonObject
        {
            ["image_b64"] = Convert.ToBase64String(new byte[] { 1 }),
            ["prompt"] = new string('é', RuntimeLimits.MaxObservationPromptBytes / 2 + 1),
        }.ToJsonString();

        var replies = await CollectAsync(router.RouteAsync(
            WorkerV1Contracts.RuntimeRequest,
            RequestPayload(body),
            CancellationToken.None));

        Assert.Equal(422, replies.Single().Payload["status_code"]!.GetValue<int>());
        Assert.Equal(0, provider.Calls);
    }

    private static ManagedWorkerV1RequestRouter Router(IObservationProvider provider) =>
        new(
            new ManagedRuntimeSettingsService(new RuntimeSettingsSnapshot(
                "OpenCode Zen", "opencode/free", null, "Default", false)),
            new PrivateRuntimeRequestRouter(new StubPrivateHandler()),
            observation: provider);

    private static JsonObject RequestPayload(string body) => new()
    {
        ["method"] = "POST",
        ["path_and_query"] = "/observation/analyze",
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

    private sealed class FakeObservationProvider : IObservationProvider
    {
        public ObservationProviderException? Failure { get; init; }

        public int Calls { get; private set; }

        public Task<VisionObservation?> AnalyzeAsync(ObservationRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Failure is not null)
            {
                throw Failure;
            }

            return Task.FromResult<VisionObservation?>(new VisionObservation(
                "Desktop",
                "Working",
                1.0,
                0.5,
                0.5,
                ["window"],
                null));
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
