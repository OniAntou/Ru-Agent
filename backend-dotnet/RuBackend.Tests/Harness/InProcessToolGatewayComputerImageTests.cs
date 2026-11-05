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
using ComputerUseService = Broker::RuAgent.Broker.Tools.Computer.ComputerUseService;
using DesktopMonitor = Broker::RuAgent.Broker.Tools.Computer.DesktopMonitor;
using DesktopElement = Broker::RuAgent.Broker.Tools.Computer.DesktopElement;
using DesktopWindow = Broker::RuAgent.Broker.Tools.Computer.DesktopWindow;
using IComputerStopHotkey = Broker::RuAgent.Broker.Tools.Computer.IComputerStopHotkey;
using IDesktopController = Broker::RuAgent.Broker.Tools.Computer.IDesktopController;
using KeyChord = Broker::RuAgent.Broker.Tools.Computer.KeyChord;
using PixelRect = Broker::RuAgent.Broker.Tools.Computer.PixelRect;
using PointerButton = Broker::RuAgent.Broker.Tools.Computer.PointerButton;

namespace RuBackend.Tests.Harness;

/// <summary>
/// ADRs 0026 and 0031 end to end: a computer screenshot learns the run's pinned
/// image-input decision from the gateway envelope, exactly like a workspace image read.
/// </summary>
public sealed class InProcessToolGatewayComputerImageTests
{
    [Fact]
    public async Task Screenshot_returns_an_image_on_the_receipt_when_the_run_accepts_images()
    {
        using var fixture = new InProcessExecutionFixture(imageInputAccepted: (_, _) => Task.FromResult(true));
        fixture.Tools.UseComputer(new ComputerUseService(new BlankDesktop(), new NoopHotkey()));
        var runId = new RunId("run_" + new string('c', 32));

        var receipt = await ScreenshotAsync(fixture, runId);

        Assert.Equal(ToolExecutionOutcome.Succeeded, receipt.Outcome);
        Assert.Equal("image/png", receipt.Image!.MediaType);
        await fixture.Factory.CloseRunAsync(runId.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Screenshot_fails_by_name_when_the_run_does_not_accept_images(bool? accepts)
    {
        using var fixture = new InProcessExecutionFixture(imageInputAccepted: accepts is { } value
            ? (_, _) => Task.FromResult(value)
            : null);
        fixture.Tools.UseComputer(new ComputerUseService(new BlankDesktop(), new NoopHotkey()));
        var runId = new RunId("run_" + new string('d', 32));

        var receipt = await ScreenshotAsync(fixture, runId);

        Assert.Equal(ToolExecutionOutcome.Failed, receipt.Outcome);
        Assert.Equal("image_unsupported_by_model", receipt.Code);
        Assert.Null(receipt.Image);
        Assert.DoesNotContain("iVBOR", Encoding.UTF8.GetString(receipt.ModelContent.Span), StringComparison.Ordinal);
        await fixture.Factory.CloseRunAsync(runId.Value);
    }

    private static async Task<ToolExecutionReceipt> ScreenshotAsync(InProcessExecutionFixture fixture, RunId runId)
    {
        var gateway = await fixture.Factory.CreateAsync(fixture.WorkspaceRoot, Snapshot(), runId, "session-computer-shot", CancellationToken.None);
        var proposal = new ToolProposal(
            runId,
            new ModelStepId("step_" + new string('0', 32)),
            new ToolCallId("call_" + new string('0', 32)),
            "provider_call_1",
            modelOrder: 1,
            "computer",
            "1.0.0",
            JsonSerializer.SerializeToElement(new JsonObject { ["action"] = "screenshot", ["monitor"] = 0 }));
        var allow = await gateway.EvaluateAsync(proposal, CancellationToken.None);
        Assert.NotNull(allow.CanonicalIntentToken);
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
                new WorkspaceId("ws_" + new string('2', 32)),
                "VOL-INPROCESS",
                "fid-inprocess",
                new string('b', 64)),
            [],
            DateTimeOffset.Parse(
                "2026-08-24T12:00:00.0000000+00:00",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind));

    /// <summary>A 64x32 desktop with no windows: a standard screenshot is fully masked but still an image.</summary>
    private sealed class BlankDesktop : IDesktopController
    {
        private static readonly PixelRect Bounds = new(0, 0, 64, 32);

        public bool IsInputDesktopAvailable() => true;
        public IReadOnlyList<DesktopMonitor> Monitors() => [new DesktopMonitor(0, Bounds, true)];
        public IReadOnlyList<DesktopWindow> Windows() => [];
        public byte[] Capture(PixelRect bounds) => new byte[bounds.Width * bounds.Height * 4];
        public DesktopWindow? RootWindowAt(int x, int y) => null;
        public DesktopWindow? ForegroundWindow() => null;
        public DesktopWindow? TopWindowOf(string appName) => null;
        public bool BringToForeground(nint handle) => false;
        public bool IsShieldedFromInput(int processId) => false;
        public void MovePointer(int x, int y) { }
        public void Click(int x, int y, PointerButton button, int count, nint expectedRoot) { }
        public void Drag(int fromX, int fromY, int toX, int toY, Func<int, int, nint> authorizeTargetAt, CancellationToken cancellationToken) { }
        public void Scroll(int x, int y, int deltaX, int deltaY, Func<int, int, nint> authorizeTargetAt) { }
        public void TypeText(string text, nint expectedRoot, CancellationToken cancellationToken) { }
        public void PressChord(KeyChord chord, nint expectedRoot) { }
        public IReadOnlyList<DesktopElement> Elements(nint window, CancellationToken cancellationToken) => [];
        public IReadOnlySet<int> RuHostProcessIds() => new HashSet<int>();
    }

    private sealed class NoopHotkey : IComputerStopHotkey
    {
        public event Action? Pressed { add { } remove { } }
        public bool EnsureRegistered() => true;
    }
}
