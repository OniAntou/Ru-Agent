extern alias Broker;

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Protocol.Harness;
using Xunit;
using BrowserConsoleEntry = Broker::RuAgent.Broker.Tools.Browser.BrowserConsoleEntry;
using BrowserDialog = Broker::RuAgent.Broker.Tools.Browser.BrowserDialog;
using BrowserNetworkEntry = Broker::RuAgent.Broker.Tools.Browser.BrowserNetworkEntry;
using BrowserPageHandle = Broker::RuAgent.Broker.Tools.Browser.BrowserPageHandle;
using BrowserSnapshot = Broker::RuAgent.Broker.Tools.Browser.BrowserSnapshot;
using BrowserUseService = Broker::RuAgent.Broker.Tools.Browser.BrowserUseService;
using IBrowserEngine = Broker::RuAgent.Broker.Tools.Browser.IBrowserEngine;

namespace RuBackend.Tests.Harness;

/// <summary>
/// ADR 0055 amendment 2026-11-25 end to end: a browser screenshot learns the run's pinned
/// image-input decision, and a run's pinned persistent profile chooses the engine, both from the
/// gateway envelope rather than the model's arguments.
/// </summary>
public sealed class InProcessToolGatewayBrowserTests
{
    [Fact]
    public async Task Screenshot_reaches_the_receipt_as_an_image_when_the_run_accepts_images()
    {
        using var fixture = new InProcessExecutionFixture(imageInputAccepted: (_, _) => Task.FromResult(true));
        await using var service = new BrowserUseService(new PngEngine());
        fixture.Tools.UseBrowser(service);
        var runId = new RunId("run_" + new string('e', 32));
        var gateway = await GatewayAsync(fixture, runId);

        Assert.Equal(ToolExecutionOutcome.Succeeded, (await CallAsync(gateway, runId, 1, new JsonObject { ["action"] = "open" })).Outcome);
        var shot = await CallAsync(gateway, runId, 2, new JsonObject { ["action"] = "screenshot" });

        Assert.Equal(ToolExecutionOutcome.Succeeded, shot.Outcome);
        Assert.Equal("image/png", shot.Image!.MediaType);
        await fixture.Factory.CloseRunAsync(runId.Value);
    }

    [Fact]
    public async Task Screenshot_fails_by_name_and_carries_no_image_for_a_text_only_run()
    {
        using var fixture = new InProcessExecutionFixture(imageInputAccepted: (_, _) => Task.FromResult(false));
        await using var service = new BrowserUseService(new PngEngine());
        fixture.Tools.UseBrowser(service);
        var runId = new RunId("run_" + new string('f', 32));
        var gateway = await GatewayAsync(fixture, runId);

        await CallAsync(gateway, runId, 1, new JsonObject { ["action"] = "open" });
        var shot = await CallAsync(gateway, runId, 2, new JsonObject { ["action"] = "screenshot" });

        Assert.Equal(ToolExecutionOutcome.Failed, shot.Outcome);
        Assert.Equal("image_unsupported_by_model", shot.Code);
        Assert.Null(shot.Image);
        Assert.DoesNotContain("iVBOR", Encoding.UTF8.GetString(shot.ModelContent.Span), StringComparison.Ordinal);
        await fixture.Factory.CloseRunAsync(runId.Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_pinned_profile_chooses_the_engine(bool persistent)
    {
        using var fixture = new InProcessExecutionFixture(browserPersistentProfile: (_, _) => Task.FromResult(persistent));
        var ephemeral = new PngEngine();
        var kept = new PngEngine();
        await using var service = new BrowserUseService(ephemeral, kept);
        fixture.Tools.UseBrowser(service);
        var runId = new RunId("run_" + new string(persistent ? '1' : '2', 32));
        var gateway = await GatewayAsync(fixture, runId);

        var open = await CallAsync(gateway, runId, 1, new JsonObject { ["action"] = "open" });

        Assert.Equal(ToolExecutionOutcome.Succeeded, open.Outcome);
        Assert.Equal(persistent, kept.Opened);
        Assert.Equal(!persistent, ephemeral.Opened);
        await fixture.Factory.CloseRunAsync(runId.Value);
    }

    private static Task<global::RuBackend.Harness.Broker.InProcessToolGateway> GatewayAsync(InProcessExecutionFixture fixture, RunId runId) =>
        fixture.Factory.CreateAsync(fixture.WorkspaceRoot, Snapshot(), runId, "session-browser-" + runId.Value[^4..], CancellationToken.None)
            .ContinueWith(task => (global::RuBackend.Harness.Broker.InProcessToolGateway)task.Result, TaskScheduler.Default);

    private static async Task<ToolExecutionReceipt> CallAsync(
        global::RuBackend.Harness.Broker.InProcessToolGateway gateway,
        RunId runId,
        int order,
        JsonObject arguments)
    {
        var id = order.ToString("x32", CultureInfo.InvariantCulture);
        var proposal = new ToolProposal(
            runId,
            new ModelStepId("step_" + id),
            new ToolCallId("call_" + id),
            "provider_call_" + order,
            modelOrder: 1,
            "browser",
            "1.0.0",
            JsonSerializer.SerializeToElement(arguments));
        var allow = await gateway.EvaluateAsync(proposal, CancellationToken.None);
        Assert.NotNull(allow.CanonicalIntentToken);
        var prepared = await gateway.PrepareAsync(
            new ApprovedToolExecution(proposal, new ExecutionId("exec_" + id), allow.CanonicalIntentToken!, allow.GatewayGeneration),
            CancellationToken.None);
        return await gateway.ExecuteAsync(prepared, _ => ValueTask.CompletedTask, CancellationToken.None);
    }

    // full_access opens the browser without an ask, so the test reaches execution directly.
    private static PolicySnapshot Snapshot() =>
        PolicySnapshotCanonicalizer.Create(
            1,
            AgentPermissionPreset.FullAccess,
            new WorkspaceIdentity(
                new WorkspaceId("ws_" + new string('3', 32)),
                "VOL-INPROCESS",
                "fid-inprocess",
                new string('b', 64)),
            [],
            DateTimeOffset.Parse("2026-08-24T12:00:00.0000000+00:00", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

}

/// <summary>An engine whose screenshot is a one-pixel PNG; every other page call succeeds empty.</summary>
internal sealed class PngEngine : IBrowserEngine
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    public bool Opened { get; private set; }

    public bool IsAvailable => true;

    public Task<BrowserPageHandle> OpenPageAsync(bool headless, CancellationToken cancellationToken)
    {
        Opened = true;
        return Task.FromResult(new BrowserPageHandle("target", "page"));
    }

    public Task NavigateAsync(BrowserPageHandle page, string url, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<BrowserSnapshot> SnapshotAsync(BrowserPageHandle page, int maxElements, int maxTextChars, CancellationToken cancellationToken) =>
        Task.FromResult(new BrowserSnapshot("about:blank", string.Empty, [], string.Empty, false));

    public Task ClickAsync(BrowserPageHandle page, string elementRef, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task TypeAsync(BrowserPageHandle page, string? elementRef, string text, bool replace, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task FillAsync(BrowserPageHandle page, string elementRef, string text, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task PressAsync(BrowserPageHandle page, string key, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<string> EvaluateAsync(BrowserPageHandle page, string expression, CancellationToken cancellationToken) => Task.FromResult("null");

    public Task<string> TitleAsync(BrowserPageHandle page, CancellationToken cancellationToken) => Task.FromResult(string.Empty);

    public Task<IReadOnlyList<BrowserConsoleEntry>> ConsoleAsync(BrowserPageHandle page, int maxEntries, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BrowserConsoleEntry>>([]);

    public Task<IReadOnlyList<BrowserNetworkEntry>> NetworkAsync(BrowserPageHandle page, int maxEntries, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BrowserNetworkEntry>>([]);

    public Task ClosePageAsync(BrowserPageHandle page, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<byte[]> ScreenshotAsync(BrowserPageHandle page, string? elementRef, CancellationToken cancellationToken) =>
        Task.FromResult(OnePixelPng);

    public Task HoverAsync(BrowserPageHandle page, string elementRef, CancellationToken cancellationToken) => Task.CompletedTask;

    public void SetNextDialogAnswer(BrowserPageHandle page, bool accept, string? promptText)
    {
    }

    public IReadOnlyList<BrowserDialog> TakeDialogs(BrowserPageHandle page) => [];

    public IReadOnlyList<BrowserPageHandle> TakeOpenedPages(BrowserPageHandle page) => [];

    public Task SetFilesAsync(BrowserPageHandle page, string elementRef, IReadOnlyList<string> paths, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
