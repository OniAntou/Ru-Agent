using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Protocol.Harness;
using RuAgent.Runtime.Adapters.Runtime;
using RuAgent.Runtime.Core.Contracts;
using RuAgent.Runtime.Worker;
using Xunit;

namespace RuAgent.Runtime.Worker.Tests;

public sealed class VtsV1HandlerTests
{
    [Fact]
    public async Task Vts_catalog_and_action_use_managed_client_and_bound_codes()
    {
        var client = new FakeVtsClient();
        var router = Router(client);

        var catalogReplies = await CollectAsync(router.RouteAsync(
            WorkerV1Contracts.VtsCatalog,
            new JsonObject { ["operation"] = "catalog" },
            CancellationToken.None));
        var actionReplies = await CollectAsync(router.RouteAsync(
            WorkerV1Contracts.VtsAction,
            new JsonObject
            {
                ["operation"] = "action",
                ["codes"] = new JsonArray("VTS_01", "expr_01"),
            },
            CancellationToken.None));

        Assert.Equal(WorkerV1Contracts.VtsCatalog, catalogReplies.Single().Type);
        Assert.Equal("model-1", catalogReplies[0].Payload["model_id"]!.GetValue<string>());
        Assert.Equal(["vts_01"], catalogReplies[0].Payload["hotkey_codes"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Equal(WorkerV1Contracts.VtsAction, actionReplies.Single().Type);
        Assert.Equal(["vts_01", "expr_01"], client.Codes);
    }

    [Fact]
    public async Task Vts_action_rejects_duplicate_or_unknown_codes_without_calling_client()
    {
        var client = new FakeVtsClient();
        var router = Router(client);

        foreach (var codes in new[]
        {
            new JsonArray("vts_01", "vts_01"),
            new JsonArray("vts_99"),
        })
        {
            var replies = await CollectAsync(router.RouteAsync(
                WorkerV1Contracts.VtsAction,
                new JsonObject { ["operation"] = "action", ["codes"] = codes },
                CancellationToken.None));

            Assert.Single(replies);
            Assert.Equal(WorkerV1Contracts.Error, replies[0].Type);
            Assert.Equal("unknown_vts_code", replies[0].Payload["code"]!.GetValue<string>());
        }

        Assert.Empty(client.Codes);
    }

    private static ManagedWorkerV1RequestRouter Router(IVtubeStudioClient client) =>
        new(
            new ManagedRuntimeSettingsService(new RuntimeSettingsSnapshot(
                "OpenCode Zen", "opencode/free", null, "Default", false)),
            new PrivateRuntimeRequestRouter(new StubPrivateHandler()),
            vts: client);

    private static async Task<List<WorkerV1Reply>> CollectAsync(IAsyncEnumerable<WorkerV1Reply> replies)
    {
        var result = new List<WorkerV1Reply>();
        await foreach (var reply in replies)
        {
            result.Add(reply);
        }

        return result;
    }

    private sealed class FakeVtsClient : IVtubeStudioClient
    {
        public IReadOnlyList<string> Codes { get; private set; } = [];

        public Task<VtsCatalog> ReadCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new VtsCatalog("model-1", "Ru", ["vts_01"], ["expr_01"]));

        public Task TriggerAsync(IReadOnlyList<string> catalogCodes, CancellationToken cancellationToken)
        {
            Codes = catalogCodes.ToArray();
            return Task.CompletedTask;
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
