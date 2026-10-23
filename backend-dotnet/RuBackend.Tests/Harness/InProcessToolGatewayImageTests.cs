using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>
/// ADR 0026 end to end through the in-process gateway: the run's pinned image-input
/// decision reaches the Broker on the envelope, and the image comes back on the receipt.
/// </summary>
public sealed class InProcessToolGatewayImageTests
{
    private static readonly byte[] Png =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 2, 0, 0, 0, 3, 8, 6, 0, 0, 0];

    [Fact]
    public async Task Read_of_an_image_returns_it_on_the_receipt_when_the_run_accepts_images()
    {
        var asked = new List<string>();
        using var fixture = new InProcessExecutionFixture(imageInputAccepted: (runId, _) =>
        {
            asked.Add(runId);
            return Task.FromResult(true);
        });
        File.WriteAllBytes(Path.Combine(fixture.WorkspaceRoot, "shot.png"), Png);
        var runId = new RunId("run_" + new string('a', 32));

        var receipt = await ReadAsync(fixture, runId, "shot.png");

        Assert.Equal(ToolExecutionOutcome.Succeeded, receipt.Outcome);
        Assert.Equal("image/png", receipt.Image!.MediaType);
        Assert.Equal(Png, receipt.Image.Bytes.ToArray());
        using var metadata = JsonDocument.Parse(receipt.ModelContent);
        Assert.Equal(2, metadata.RootElement.GetProperty("width").GetInt32());
        Assert.Equal(3, metadata.RootElement.GetProperty("height").GetInt32());
        Assert.Equal([runId.Value], asked);
        await fixture.Factory.CloseRunAsync(runId.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Read_of_an_image_fails_by_name_when_the_run_does_not_accept_images(bool? accepts)
    {
        using var fixture = new InProcessExecutionFixture(imageInputAccepted: accepts is { } value
            ? (_, _) => Task.FromResult(value)
            : null);
        File.WriteAllBytes(Path.Combine(fixture.WorkspaceRoot, "shot.png"), Png);
        var runId = new RunId("run_" + new string('b', 32));

        var receipt = await ReadAsync(fixture, runId, "shot.png");

        Assert.Equal(ToolExecutionOutcome.Failed, receipt.Outcome);
        Assert.Equal("image_unsupported_by_model", receipt.Code);
        Assert.Null(receipt.Image);
        Assert.DoesNotContain("iVBOR", Encoding.UTF8.GetString(receipt.ModelContent.Span), StringComparison.Ordinal);
        await fixture.Factory.CloseRunAsync(runId.Value);
    }

    private static async Task<ToolExecutionReceipt> ReadAsync(InProcessExecutionFixture fixture, RunId runId, string path)
    {
        var gateway = await fixture.Factory.CreateAsync(fixture.WorkspaceRoot, Snapshot(), runId, "session-image-read", CancellationToken.None);
        var proposal = new ToolProposal(
            runId,
            new ModelStepId("step_" + new string('0', 32)),
            new ToolCallId("call_" + new string('0', 32)),
            "provider_call_1",
            modelOrder: 1,
            "read",
            "1.0.0",
            JsonSerializer.SerializeToElement(new JsonObject { ["path"] = path }));
        var allow = await gateway.EvaluateAsync(proposal, CancellationToken.None);
        var prepared = await gateway.PrepareAsync(
            new ApprovedToolExecution(
                proposal,
                new ExecutionId("exec_" + new string('0', 32)),
                allow.CanonicalIntentToken!,
                allow.GatewayGeneration),
            CancellationToken.None);
        return await gateway.ExecuteAsync(prepared, _ => ValueTask.CompletedTask, CancellationToken.None);
    }

    private static PolicySnapshot Snapshot() =>
        PolicySnapshotCanonicalizer.Create(
            1,
            AgentPermissionPreset.Standard,
            new WorkspaceIdentity(
                new WorkspaceId("ws_" + new string('1', 32)),
                "VOL-INPROCESS",
                "fid-inprocess",
                new string('a', 64)),
            [],
            DateTimeOffset.Parse(
                "2026-08-24T12:00:00.0000000+00:00",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind));
}
